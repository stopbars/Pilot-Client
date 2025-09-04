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
    private double? _cachedGroundAltFeet;
    private DateTime _cachedGroundAltAt;
    private static readonly TimeSpan GroundAltCacheDuration = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, bool> _lateAttachedPoints = new();
    // Track successful creations so late attach logic can correlate
    private readonly ConcurrentDictionary<string, int> _createdObjectIds = new();

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
                try { await Task.Delay(1000, ct); } catch { yield break; }
                continue;
            }

            var sample = await TryGetSampleAsync(ct);
            if (sample is RawFlightSample s) yield return s;
            try { await Task.Delay(PollDelayMs, ct); } catch { yield break; }
        }
    }

    private async Task<RawFlightSample?> TryGetSampleAsync(CancellationToken ct)
    {
        var client = _client;
        if (client == null) return null;
        try
        {
            var svm = client.SimVars;
            if (svm == null) return null;
            double lat = await svm.GetAsync<double>("PLANE LATITUDE", "degrees");
            double lon = await svm.GetAsync<double>("PLANE LONGITUDE", "degrees");
            bool onGround = (await svm.GetAsync<int>("SIM ON GROUND", "bool")) == 1;
            return new RawFlightSample(lat, lon, onGround);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MSFS sample retrieval failed (will retry)");
            return null;
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
                    altitudeFeet = await client.SimVars.GetAsync<double>("PLANE ALTITUDE", "feet", cancellationToken: ct).ConfigureAwait(false);
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
                var candidates = mgr.ManagedObjects.Values.Where(o => o.IsActive).ToList();
                SimObject? match = null;
                foreach (var c in candidates)
                {
                    try
                    {
                        if (c.UserData is string ud && string.Equals(ud, pointId, StringComparison.Ordinal))
                        {
                            match = c; break;
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
