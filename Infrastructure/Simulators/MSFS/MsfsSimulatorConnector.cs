using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Domain;
using Microsoft.Extensions.Logging;
using SimConnect.NET;
using SimConnect.NET.AI;
using SimConnect.NET.SimVar;

namespace BARS_Client_V2.Infrastructure.Simulators.Msfs;

public sealed class MsfsSimulatorConnector : ISimulatorConnector, IDisposable
{
    private readonly ILogger<MsfsSimulatorConnector> _logger;
    private SimConnectClient? _client;
    private const int PollDelayMs = 500; // faster polling for precise stopbar crossing detection
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _simVarGate = new(1, 1);
    private double? _cachedGroundAltFeet;
    private DateTime _cachedGroundAltAt;
    private static readonly TimeSpan GroundAltCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SimVarRequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SampleBackoffFloor = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SampleBackoffCeiling = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SubscribedSampleRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SubscribedSampleStaleThreshold = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, bool> _lateAttachedPoints = new();
    // Track successful creations so late attach logic can correlate
    private readonly ConcurrentDictionary<string, int> _createdObjectIds = new();
    // Avoid tearing down the connection on a single transient timeout
    private int _consecutiveSampleErrors;
    private const int MaxConsecutiveSampleErrorsBeforeDisconnect = 5;
    private readonly object _sampleBackoffLock = new();
    private TimeSpan _sampleBackoff = TimeSpan.Zero;
    private DateTime _nextSampleAllowedUtc = DateTime.MinValue;
    private readonly object _sampleSubscriptionLock = new();
    private ISimVarSubscription? _latitudeSubscription;
    private ISimVarSubscription? _longitudeSubscription;
    private ISimVarSubscription? _onGroundSubscription;
    private ISimVarSubscription? _altitudeSubscription;
    private ISimVarSubscription? _headingSubscription;
    private double? _subscribedLatitude;
    private double? _subscribedLongitude;
    private bool? _subscribedOnGround;
    private double? _subscribedHeading;
    private RawFlightSample? _latestSubscribedSample;
    private DateTime _latestSubscribedSampleUtc = DateTime.MinValue;
    private DateTime _lastDeliveredSampleUtc = DateTime.MinValue;
    private bool _subscriptionsInitialized;
    private DateTime _subscriptionsStartedUtc = DateTime.MinValue;

    public MsfsSimulatorConnector(ILogger<MsfsSimulatorConnector> logger) => _logger = logger;

    public string SimulatorId => "MSFS";
    public string DisplayName
    {
        get
        {
            var is2024 = IsMsfs2024;
            if (is2024 == true) return "Microsoft Flight Simulator 2024";
            if (is2024 == false) return "Microsoft Flight Simulator 2020";
            return "Microsoft Flight Simulator"; // unknown (not yet connected)
        }
    }
    public bool IsConnected => _client?.IsConnected == true;
    /// <summary>
    /// Indicates whether the connected MSFS instance is the 2024 version. Null if not connected or undetermined.
    /// Relies on SimConnectClient.IsMSFS2024 (exposed by SimConnect.NET) as hinted by user.
    /// </summary>
    public bool? IsMsfs2024 => _client?.IsMSFS2024;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;

        await _connectGate.WaitAsync(ct);
        try
        {
            if (IsConnected) return true;
            int attempt = 0;
            while (!ct.IsCancellationRequested && !IsConnected)
            {
                attempt++;
                try
                {
                    _logger.LogInformation("MSFS connect attempt {attempt}...", attempt);
                    var client = new SimConnectClient("BARS Client");
                    await client.ConnectAsync();
                    if (client.IsConnected)
                    {
                        _client = client;
                        InitializeSimVarSubscriptions(client);
                        _logger.LogInformation("Connected to MSFS via SimConnect.NET after {attempt} attempt(s)", attempt);
                        break;
                    }
                    else
                    {
                        client.Dispose();
                        _logger.LogWarning("MSFS connect attempt {attempt} failed (not connected after ConnectAsync)", attempt);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MSFS connection attempt {attempt} failed", attempt);
                }

                if (!IsConnected)
                {
                    try
                    {
                        _logger.LogInformation("Retrying MSFS connection in {delaySeconds} seconds", (int)RetryDelay.TotalSeconds);
                        await Task.Delay(RetryDelay, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }
        finally
        {
            _connectGate.Release();
        }
        return IsConnected;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client != null)
        {
            DisposeSimVarSubscriptions();
            try { client.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing SimConnect client"); }
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RawFlightSample> StreamRawAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!IsConnected)
            {
                // Stop streaming so manager can observe disconnect and trigger reconnection.
                yield break;
            }

            var sample = await TryGetSampleAsync(ct);
            if (sample is RawFlightSample s)
            {
                yield return s;
                continue;
            }

            try
            {
                await Task.Delay(PollDelayMs, ct);
            }
            catch
            {
                yield break;
            }
        }
    }

    private async Task<RawFlightSample?> TryGetSampleAsync(CancellationToken ct)
    {
        if (TryReadSubscribedSample(out var subscribedSample) && subscribedSample is RawFlightSample subscribedValue)
        {
            return subscribedValue;
        }

        if (_subscriptionsInitialized)
        {
            var age = GetSubscribedSampleAge();
            if (age.HasValue)
            {
                if (age.Value <= SubscribedSampleStaleThreshold)
                {
                    return null; // recent sample already delivered; wait for next
                }
            }
            else if (_subscriptionsStartedUtc != DateTime.MinValue &&
                     (DateTime.UtcNow - _subscriptionsStartedUtc) <= SubscribedSampleStaleThreshold)
            {
                return null; // give subscriptions a moment to deliver first value
            }
        }

        if (ShouldDeferSample(out var remaining))
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("[SampleBackoff] Skipping sample; remaining={remaining:F0}ms", remaining.TotalMilliseconds);
            }
            return null;
        }

        var client = _client;
        if (client == null) return null;
        try
        {
            var svm = client.SimVars;
            if (svm == null) return null;

            await _simVarGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var latTask = svm.GetAsync<double>("PLANE LATITUDE", "degrees", cancellationToken: ct)
                    .WaitAsync(SimVarRequestTimeout, ct);
                var lonTask = svm.GetAsync<double>("PLANE LONGITUDE", "degrees", cancellationToken: ct)
                    .WaitAsync(SimVarRequestTimeout, ct);
                var onGroundTask = svm.GetAsync<int>("SIM ON GROUND", "bool", cancellationToken: ct)
                    .WaitAsync(SimVarRequestTimeout, ct);
                var headingTask = svm.GetAsync<double>("PLANE HEADING DEGREES TRUE", "degrees", cancellationToken: ct)
                    .WaitAsync(SimVarRequestTimeout, ct);

                await Task.WhenAll(latTask, lonTask, onGroundTask, headingTask).ConfigureAwait(false);

                var lat = latTask.Result;
                var lon = lonTask.Result;
                var onGround = onGroundTask.Result == 1;
                var heading = NormalizeHeading(headingTask.Result);

                // success -> reset error budget
                _consecutiveSampleErrors = 0;
                ResetSampleBackoff();
                return new RawFlightSample(lat, lon, onGround, heading);
            }
            finally
            {
                _simVarGate.Release();
            }
        }
        catch (OperationCanceledException oce)
        {
            if (ct.IsCancellationRequested) throw; // external cancellation – bubble up
            // Per-request timeout or transient cancellation – treat as soft miss
            var n = Interlocked.Increment(ref _consecutiveSampleErrors);
            _logger.LogDebug(oce, "MSFS sample timed out/cancelled (#{count}/{max}) – will retry without disconnect", n, MaxConsecutiveSampleErrorsBeforeDisconnect);
            ScheduleSampleBackoff();
            if (n >= MaxConsecutiveSampleErrorsBeforeDisconnect)
            {
                _logger.LogWarning("MSFS sample repeatedly failing ({count} in a row) – disposing client to recover", n);
                try { await DisconnectAsync(); } catch { }
                _consecutiveSampleErrors = 0;
            }
            return null;
        }
        catch (TimeoutException tex)
        {
            // Some SimConnect.NET versions throw TimeoutException directly
            var n = Interlocked.Increment(ref _consecutiveSampleErrors);
            _logger.LogDebug(tex, "MSFS sample TimeoutException (#{count}/{max}) – will retry without immediate disconnect", n, MaxConsecutiveSampleErrorsBeforeDisconnect);
            ScheduleSampleBackoff();
            if (n >= MaxConsecutiveSampleErrorsBeforeDisconnect)
            {
                _logger.LogWarning("MSFS sample repeatedly timing out ({count} in a row) – disposing client to recover", n);
                try { await DisconnectAsync(); } catch { }
                _consecutiveSampleErrors = 0;
            }
            return null;
        }
        catch (Exception ex)
        {
            // Treat other exceptions as transient, escalate only after several occurrences
            var n = Interlocked.Increment(ref _consecutiveSampleErrors);
            _logger.LogDebug(ex, "MSFS sample retrieval error (#{count}/{max})", n, MaxConsecutiveSampleErrorsBeforeDisconnect);
            ScheduleSampleBackoff();
            if (n >= MaxConsecutiveSampleErrorsBeforeDisconnect)
            {
                _logger.LogWarning(ex, "MSFS sample repeatedly failing – disposing client to recover");
                try { await DisconnectAsync(); } catch { }
                _consecutiveSampleErrors = 0;
            }
            return null;
        }
    }

    private bool ShouldDeferSample(out TimeSpan remaining)
    {
        lock (_sampleBackoffLock)
        {
            if (_sampleBackoff <= TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
                return false;
            }

            var now = DateTime.UtcNow;
            if (now >= _nextSampleAllowedUtc)
            {
                remaining = TimeSpan.Zero;
                _sampleBackoff = TimeSpan.Zero;
                _nextSampleAllowedUtc = DateTime.MinValue;
                return false;
            }

            remaining = _nextSampleAllowedUtc - now;
            return true;
        }
    }

    private void ScheduleSampleBackoff()
    {
        lock (_sampleBackoffLock)
        {
            var nextSeconds = _sampleBackoff <= TimeSpan.Zero
                ? SampleBackoffFloor.TotalSeconds
                : Math.Min(_sampleBackoff.TotalSeconds * 2, SampleBackoffCeiling.TotalSeconds);
            _sampleBackoff = TimeSpan.FromSeconds(nextSeconds);
            _nextSampleAllowedUtc = DateTime.UtcNow + _sampleBackoff;
        }
    }

    private void ResetSampleBackoff()
    {
        lock (_sampleBackoffLock)
        {
            _sampleBackoff = TimeSpan.Zero;
            _nextSampleAllowedUtc = DateTime.MinValue;
        }
    }

    private static double? NormalizeHeading(double headingDeg)
    {
        if (double.IsNaN(headingDeg) || double.IsInfinity(headingDeg)) return null;
        var normalized = headingDeg % 360.0;
        if (normalized < 0) normalized += 360.0;
        if (normalized >= 360.0) normalized -= 360.0;
        return normalized;
    }

    private void InitializeSimVarSubscriptions(SimConnectClient client)
    {
        var svm = client.SimVars;
        if (svm == null) return;

        lock (_sampleSubscriptionLock)
        {
            DisposeSimVarSubscriptions_NoLock();
            try
            {
                _latitudeSubscription = svm.Subscribe<double>(
                    "PLANE LATITUDE",
                    "degrees",
                    SimConnectPeriod.Second,
                    latitude => SafeInvoke(OnLatitudeUpdate, latitude));

                _longitudeSubscription = svm.Subscribe<double>(
                    "PLANE LONGITUDE",
                    "degrees",
                    SimConnectPeriod.Second,
                    longitude => SafeInvoke(OnLongitudeUpdate, longitude));

                _onGroundSubscription = svm.Subscribe<int>(
                    "SIM ON GROUND",
                    "bool",
                    SimConnectPeriod.Second,
                    value => SafeInvoke(OnOnGroundUpdate, value == 1));

                _altitudeSubscription = svm.Subscribe<double>(
                    "PLANE ALTITUDE",
                    "feet",
                    SimConnectPeriod.Second,
                    altitude => SafeInvoke(OnAltitudeUpdate, altitude));

                _headingSubscription = svm.Subscribe<double>(
                    "PLANE HEADING DEGREES TRUE",
                    "degrees",
                    SimConnectPeriod.Second,
                    heading => SafeInvoke(OnHeadingUpdate, heading));

                _subscriptionsInitialized = true;
                _subscriptionsStartedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to initialize SimVar subscriptions");
                DisposeSimVarSubscriptions_NoLock();
                _subscriptionsInitialized = false;
                _subscriptionsStartedUtc = DateTime.MinValue;
            }
        }
    }

    private void DisposeSimVarSubscriptions()
    {
        lock (_sampleSubscriptionLock)
        {
            DisposeSimVarSubscriptions_NoLock();
            _subscribedLatitude = null;
            _subscribedLongitude = null;
            _subscribedOnGround = null;
            _subscribedHeading = null;
            _latestSubscribedSample = null;
            _latestSubscribedSampleUtc = DateTime.MinValue;
            _lastDeliveredSampleUtc = DateTime.MinValue;
            _subscriptionsInitialized = false;
            _subscriptionsStartedUtc = DateTime.MinValue;
        }
    }

    private void DisposeSimVarSubscriptions_NoLock()
    {
        _latitudeSubscription?.Dispose();
        _latitudeSubscription = null;
        _longitudeSubscription?.Dispose();
        _longitudeSubscription = null;
        _onGroundSubscription?.Dispose();
        _onGroundSubscription = null;
        _altitudeSubscription?.Dispose();
        _altitudeSubscription = null;
        _headingSubscription?.Dispose();
        _headingSubscription = null;
    }

    private void OnLatitudeUpdate(double latitude)
    {
        lock (_sampleSubscriptionLock)
        {
            _subscribedLatitude = latitude;
            PublishSubscribedSample_NoLock();
        }
    }

    private void OnLongitudeUpdate(double longitude)
    {
        lock (_sampleSubscriptionLock)
        {
            _subscribedLongitude = longitude;
            PublishSubscribedSample_NoLock();
        }
    }

    private void OnOnGroundUpdate(bool onGround)
    {
        lock (_sampleSubscriptionLock)
        {
            _subscribedOnGround = onGround;
            PublishSubscribedSample_NoLock();
        }
    }

    private void OnAltitudeUpdate(double altitudeFeet)
    {
        _cachedGroundAltFeet = altitudeFeet;
        _cachedGroundAltAt = DateTime.UtcNow;
    }

    private void OnHeadingUpdate(double headingDeg)
    {
        lock (_sampleSubscriptionLock)
        {
            _subscribedHeading = NormalizeHeading(headingDeg);
            PublishSubscribedSample_NoLock();
        }
    }

    private void PublishSubscribedSample_NoLock()
    {
        if (_subscribedLatitude.HasValue && _subscribedLongitude.HasValue && _subscribedOnGround.HasValue)
        {
            _latestSubscribedSample = new RawFlightSample(
                _subscribedLatitude.Value,
                _subscribedLongitude.Value,
                _subscribedOnGround.Value,
                _subscribedHeading);
            _latestSubscribedSampleUtc = DateTime.UtcNow;
        }
    }

    private bool TryReadSubscribedSample(out RawFlightSample? sample)
    {
        lock (_sampleSubscriptionLock)
        {
            if (_latestSubscribedSample is RawFlightSample current)
            {
                if (_latestSubscribedSampleUtc > _lastDeliveredSampleUtc)
                {
                    sample = current;
                    _lastDeliveredSampleUtc = _latestSubscribedSampleUtc;
                    return true;
                }

                if (_lastDeliveredSampleUtc != DateTime.MinValue &&
                    (DateTime.UtcNow - _lastDeliveredSampleUtc) >= SubscribedSampleRefreshInterval)
                {
                    sample = current;
                    _lastDeliveredSampleUtc = DateTime.UtcNow;
                    return true;
                }
            }
        }

        sample = null;
        return false;
    }

    private TimeSpan? GetSubscribedSampleAge()
    {
        lock (_sampleSubscriptionLock)
        {
            if (_latestSubscribedSample is RawFlightSample && _latestSubscribedSampleUtc != DateTime.MinValue)
            {
                return DateTime.UtcNow - _latestSubscribedSampleUtc;
            }
        }

        return null;
    }

    private void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SimVar subscription callback failed");
        }
    }

    private void SafeInvoke<T>(Action<T> action, T argument)
    {
        try
        {
            action(argument);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SimVar subscription callback failed");
        }
    }

    public void Dispose() => _ = DisconnectAsync();

    internal async Task<SimObject?> SpawnLightAsync(string pointId, double lat, double lon, double? heading, int? stateId, CancellationToken ct)
    {
        if (!IsConnected) return null;
        var client = _client;
        if (client == null) return null;
        var mgr = client.AIObjects;
        if (mgr == null) return null; // defensive: library should provide this when connected
        try
        {
            if (_lateAttachedPoints.ContainsKey(pointId))
            {
                _logger.LogTrace("[Connector.Spawn.SkipLate] point={pointId} already late-attached", pointId);
                return null;
            }
            double altitudeFeet;
            var now = DateTime.UtcNow;
            if (_cachedGroundAltFeet.HasValue && (now - _cachedGroundAltAt) < GroundAltCacheDuration)
            {
                altitudeFeet = _cachedGroundAltFeet.Value;
            }
            else
            {
                try
                {
                    await _simVarGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        altitudeFeet = await client.SimVars.GetAsync<double>("PLANE ALTITUDE", "feet", cancellationToken: ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _simVarGate.Release();
                    }
                    _cachedGroundAltFeet = altitudeFeet;
                    _cachedGroundAltAt = now;
                }
                catch
                {
                    altitudeFeet = 50; // fallback nominal
                }
            }
            var pos = new SimConnectDataInitPosition
            {
                Latitude = lat,
                Longitude = lon,
                Altitude = altitudeFeet,
                Pitch = 0,
                Bank = 0,
                Heading = heading ?? 0,
                OnGround = 1,
                Airspeed = 0
            };
            var model = ResolveModelVariant(stateId);
            _logger.LogTrace("[Connector.Spawn] point={pointId} model={model} lat={lat:F6} lon={lon:F6} hdg={hdg:F1} stateId={sid}", pointId, model, lat, lon, heading ?? 0, stateId);
            SimObject simObj;
            try
            {
                simObj = await mgr.CreateObjectAsync(model, pos, userData: pointId, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception createEx)
            {
                _logger.LogWarning(createEx, "[Connector.Spawn.CreateFail] point={pointId} model={model} stateId={sid}", pointId, model, stateId);
                throw; // propagate to outer catch -> late attach fallback
            }
            _logger.LogInformation("[Connector.Spawned] point={pointId} model={model} objectId={obj} stateIdInit={sid} activeCount={count}", pointId, model, simObj.ObjectId, stateId, mgr.ActiveObjectCount);
            // Record association for late attach correlation / diagnostics
            _createdObjectIds[pointId] = unchecked((int)simObj.ObjectId);
            return simObj;
        }
        catch (OperationCanceledException oce)
        {
            if (ct.IsCancellationRequested) throw; // external cancel
            _logger.LogWarning(oce, "[Connector.Spawn.Timeout] point={pointId} probable creation timeout; will watch for late object", pointId);
            _ = Task.Run(() => TryLateAttachAsync(pointId, lat, lon, client, CancellationToken.None));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Connector.Spawn.Fail] point={pointId} stateId={sid}", pointId, stateId);
            _ = Task.Run(() => TryLateAttachAsync(pointId, lat, lon, client, CancellationToken.None));
            return null;
        }
    }

    internal async Task DespawnLightAsync(SimObject simObject, CancellationToken ct)
    {
        var client = _client;
        var mgr = client?.AIObjects;
        if (mgr == null) return;
        try { await mgr.RemoveObjectAsync(simObject, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "DespawnLightAsync failed {obj}", simObject.ObjectId); }
    }

    private async Task TryLateAttachAsync(string pointId, double lat, double lon, SimConnectClient client, CancellationToken cancellationToken)
    {
        if (!_lateAttachedPoints.TryAdd(pointId, false)) return; // already attempting
        try
        {
            var mgr = client.AIObjects;
            const int maxSeconds = 30;
            for (int i = 0; i < maxSeconds && !cancellationToken.IsCancellationRequested; i++)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                // If create eventually succeeded normally, association already recorded
                if (_createdObjectIds.ContainsKey(pointId))
                {
                    _lateAttachedPoints[pointId] = true;
                    _logger.LogTrace("[Connector.LateAttach.Skip] point={pointId} normalSpawnRecorded", pointId);
                    return;
                }
                // Attempt to locate by userData if library exposes it; fall back to positional proximity heuristic
                SimObject? match = null;
                foreach (var candidate in mgr.ManagedObjects.Values)
                {
                    if (!candidate.IsActive) continue;
                    try
                    {
                        if (candidate.UserData is string ud && string.Equals(ud, pointId, StringComparison.Ordinal))
                        {
                            match = candidate;
                            break;
                        }
                    }
                    catch { }
                }
                // (Position-based heuristic removed; SimObject.Position not available in current API)
                if (match != null)
                {
                    _lateAttachedPoints[pointId] = true;
                    _createdObjectIds[pointId] = unchecked((int)match.ObjectId);
                    _logger.LogInformation("[Connector.LateAttach] point={pointId} objectId={obj}", pointId, match.ObjectId);
                    return;
                }
            }
            _logger.LogDebug("[Connector.LateAttach.None] point={pointId} no matching object found", pointId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Connector.LateAttach.Error] point={pointId}", pointId);
        }
        finally
        {
            // Allow future attempts if we never succeeded
            if (!_lateAttachedPoints.TryGetValue(pointId, out var success) || !success)
                _lateAttachedPoints.TryRemove(pointId, out _);
        }
    }

    // (Haversine helper removed – no longer needed after heuristic removal)

    private static string ResolveModelVariant(int? stateId)
    {
        if (!stateId.HasValue) return "BARS_Light_0"; // default off variant model
        var s = stateId.Value;
        if (s < 0) s = 0;
        return $"BARS_Light_{s}";
    }
}
