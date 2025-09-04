using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Infrastructure.Networking;
using BARS_Client_V2.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimConnect.NET.AI;

namespace BARS_Client_V2.Infrastructure.Simulators.Msfs;

/// <summary>
/// Queues point state changes and (eventually) reflects them inside MSFS by spawning / updating custom SimObjects.
/// Currently contains stubs for spawn/despawn until concrete SimObject titles & WASM variables are defined.
/// </summary>
internal sealed class MsfsPointController : BackgroundService, IPointStateListener
{
    private readonly ILogger<MsfsPointController> _logger;
    private readonly ISimulatorConnector _connector; // assumed MSFS
    private readonly AirportStateHub _hub;
    private readonly SimulatorManager _simManager;
    private readonly ConcurrentQueue<PointState> _queue = new();
    private readonly ConcurrentDictionary<string, PointState> _latestStates = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<LightLayout>> _layoutCache = new();
    private readonly System.Threading.SemaphoreSlim _spawnConcurrency = new(4, 4);
    // Track stateId for each spawned SimObject (objectId -> stateId) so we don't rely on ContainerTitle which proved unreliable.
    private readonly ConcurrentDictionary<uint, int> _objectStateIds = new();

    // Config
    private readonly int _maxObjects;
    private readonly int _spawnPerSecond;
    private readonly int _idleDelayMs;
    private readonly int _disconnectedDelayMs;
    private readonly int _errorBackoffMs;
    private readonly double _spawnRadiusMeters;
    private readonly TimeSpan _proximitySweepInterval;
    private DateTime _nextProximitySweepUtc = DateTime.UtcNow;
    private readonly bool _dynamicPruneEnabled;

    // Rate tracking
    private DateTime _nextSpawnWindow = DateTime.UtcNow;
    private int _spawnedThisWindow;

    // Stats
    private long _totalReceived;
    private long _totalSpawnAttempts;
    private long _totalDespawned;
    private long _totalDeferredRate;
    private long _totalSkippedCap;
    private DateTime _lastSummary = DateTime.UtcNow;

    private volatile bool _suspended;

    // Stopbar crossing detection
    private double? _prevLat;
    private double? _prevLon;
    private readonly ConcurrentDictionary<string, (double LatA, double LonA, double LatB, double LonB)> _stopbarSegments = new();
    private readonly ConcurrentDictionary<string, DateTime> _crossDebounceUntil = new();
    private readonly TimeSpan _crossDebounceWindow = TimeSpan.FromSeconds(5);

    // Failure/backoff
    private readonly ConcurrentDictionary<string, (int Failures, DateTime LastFailureUtc)> _spawnFailures = new();
    private readonly TimeSpan _failureCooldown = TimeSpan.FromSeconds(10);
    private const int FailureThresholdForCooldown = 3;
    private readonly ConcurrentDictionary<string, DateTime> _nextAttemptUtc = new();
    private readonly ConcurrentDictionary<string, DateTime> _hardCooldownUntil = new();

    public MsfsPointController(IEnumerable<ISimulatorConnector> connectors,
                               ILogger<MsfsPointController> logger,
                               AirportStateHub hub,
                               SimulatorManager simManager,
                               MsfsPointControllerOptions? options = null)
    {
        _connector = connectors.FirstOrDefault(c => c.SimulatorId.Equals("MSFS", StringComparison.OrdinalIgnoreCase)) ?? connectors.First();
        _logger = logger;
        options ??= new MsfsPointControllerOptions();
        _hub = hub;
        _simManager = simManager;
        _hub.PointStateChanged += OnPointStateChanged;
        _hub.MapLoaded += _ => { _stopbarSegments.Clear(); ResyncActivePointsAfterLayout(); };
        _maxObjects = options.MaxObjects;
        _spawnPerSecond = options.SpawnPerSecond;
        _idleDelayMs = options.IdleDelayMs;
        _disconnectedDelayMs = options.DisconnectedDelayMs;
        _errorBackoffMs = options.ErrorBackoffMs;
        _spawnRadiusMeters = options.SpawnRadiusMeters;
        _proximitySweepInterval = TimeSpan.FromSeconds(options.ProximitySweepSeconds);
        _dynamicPruneEnabled = options.DynamicPruneEnabled;
    }

    public void OnPointStateChanged(PointState state)
    {
        _latestStates[state.Metadata.Id] = state;
        if (_suspended) return; // cache only
        _queue.Enqueue(state);
        var total = Interlocked.Increment(ref _totalReceived);
        if (total <= 5 || (total % 500) == 0)
        {
            var m = state.Metadata;
            _logger.LogInformation("[RecvSample] id={id} on={on} type={type} apt={apt} lat={lat:F6} lon={lon:F6} total={tot}",
                m.Id, state.IsOn, m.Type, m.AirportId, m.Latitude, m.Longitude, total);
        }
        else if (_logger.IsEnabled(LogLevel.Trace))
        {
            var m = state.Metadata;
            _logger.LogTrace("[Recv] id={id} on={on} type={type} apt={apt} lat={lat:F6} lon={lon:F6}",
                m.Id, state.IsOn, m.Type, m.AirportId, m.Latitude, m.Longitude);
        }
    }

    /// <summary>
    /// Temporarily suspend all spawning/despawning activity (except explicit DespawnAllAsync) and clear queued work.
    /// Used when the upstream server / VATSIM disconnects so we freeze visual state instead of thrashing.
    /// </summary>
    public void Suspend()
    {
        _suspended = true;
        while (_queue.TryDequeue(out _)) { }
        _logger.LogInformation("[Suspend] MsfsPointController suspended; activeLights={lights}", TotalActiveLightCount());
    }

    /// <summary>
    /// Resume normal spawning/despawning operations. Re-enqueues current ON states so they reconcile.
    /// </summary>
    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        int requeued = 0;
        foreach (var kv in _latestStates) if (kv.Value.IsOn) { _queue.Enqueue(kv.Value); requeued++; }
        _logger.LogInformation("[Resume] MsfsPointController resumed; requeuedActiveOn={requeued} queue={q}", requeued, _queue.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MsfsPointController started (manager-driven mode) max={max} rate/s={rate}", _maxObjects, _spawnPerSecond);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_connector.IsConnected)
                {
                    if ((_totalReceived % 25) == 0) _logger.LogDebug("[Loop] Waiting for simulator connection.");
                    await Task.Delay(_disconnectedDelayMs, stoppingToken);
                    continue;
                }
                if (_suspended)
                {
                    await Task.Delay(_idleDelayMs * 5, stoppingToken);
                    continue;
                }
                // Stopbar crossing detection based on latest aircraft movement
                var flightForCross = _simManager.LatestState;
                if (flightForCross != null) { try { DetectStopbarCrossings(flightForCross); } catch (Exception ex) { _logger.LogDebug(ex, "DetectStopbarCrossings failed"); } }
                if (_queue.TryDequeue(out var ps))
                {
                    await ProcessAsync(ps, stoppingToken);
                }
                else
                {
                    await Task.Delay(_idleDelayMs, stoppingToken);
                }
                if (DateTime.UtcNow >= _nextProximitySweepUtc)
                {
                    _nextProximitySweepUtc = DateTime.UtcNow + _proximitySweepInterval;
                    try { await ProximitySweepAsync(stoppingToken); } catch (Exception ex) { _logger.LogDebug(ex, "ProximitySweep failed"); }
                }
                if ((DateTime.UtcNow - _lastSummary) > TimeSpan.FromSeconds(30))
                {
                    _lastSummary = DateTime.UtcNow;
                    _logger.LogInformation("[Summary] received={rec} spawnAttempts={spAtt} activeLights={active} despawned={des} deferredRate={def} skippedCap={cap} queue={q}",
                        _totalReceived, _totalSpawnAttempts, TotalActiveLightCount(), _totalDespawned, _totalDeferredRate, _totalSkippedCap, _queue.Count);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Loop error");
                try { await Task.Delay(_errorBackoffMs, stoppingToken); } catch { }
            }
        }
    }

    private void DetectStopbarCrossings(FlightState flight)
    {
        var currLat = flight.Latitude;
        var currLon = flight.Longitude;
        if (!_prevLat.HasValue || !_prevLon.HasValue)
        {
            _prevLat = currLat; _prevLon = currLon; return;
        }
        var prevLat = _prevLat!.Value; var prevLon = _prevLon!.Value;
        // If aircraft barely moved, skip
        if (DistanceMeters(prevLat, prevLon, currLat, currLon) < 1.0) { _prevLat = currLat; _prevLon = currLon; return; }

        // Consider only nearby stopbars whose state is OFF (dropped)
        foreach (var kv in _latestStates)
        {
            var ps = kv.Value;
            if (ps.IsOn) continue; // we only report when dropped
            var type = ps.Metadata.Type ?? string.Empty;
            if (!type.Contains("STOP", StringComparison.OrdinalIgnoreCase) || !type.Contains("BAR", StringComparison.OrdinalIgnoreCase)) continue;
            // Debounce this object id if recently reported
            if (_crossDebounceUntil.TryGetValue(ps.Metadata.Id, out var until) && DateTime.UtcNow < until) continue;

            // Quick distance gate to avoid scanning far objects
            var dCurr = DistanceMeters(currLat, currLon, ps.Metadata.Latitude, ps.Metadata.Longitude);
            if (dCurr > 200) continue; // 200m radius heuristic

            var seg = GetOrBuildStopbarSegment(ps.Metadata.Id, ps);
            if (seg == null) continue;
            var (aLat, aLon, bLat, bLon) = seg.Value;
            if (Crosses(prevLat, prevLon, currLat, currLon, aLat, aLon, bLat, bLon))
            {
                _crossDebounceUntil[ps.Metadata.Id] = DateTime.UtcNow + _crossDebounceWindow;
                _hub.SendStopbarCrossing(ps.Metadata.Id);
                _logger.LogInformation("[StopbarCrossing] objectId={id} pos=({lat:F6},{lon:F6})", ps.Metadata.Id, currLat, currLon);
            }
        }

        _prevLat = currLat; _prevLon = currLon;
    }

    private (double LatA, double LonA, double LatB, double LonB)? GetOrBuildStopbarSegment(string pointId, PointState ps)
    {
        if (_stopbarSegments.TryGetValue(pointId, out var seg)) return seg;
        if (!_hub.TryGetLightLayout(pointId, out var lights) || lights.Count < 2) return null;
        // Choose the two lights with maximum separation as segment endpoints
        double best = -1; (double la, double lo, double lb, double lob) bestPair = default;
        for (int i = 0; i < lights.Count; i++)
        {
            for (int j = i + 1; j < lights.Count; j++)
            {
                var di = DistanceMeters(lights[i].Latitude, lights[i].Longitude, lights[j].Latitude, lights[j].Longitude);
                if (di > best)
                {
                    best = di; bestPair = (lights[i].Latitude, lights[i].Longitude, lights[j].Latitude, lights[j].Longitude);
                }
            }
        }
        if (best <= 0) return null;
        var result = (bestPair.la, bestPair.lo, bestPair.lb, bestPair.lob);
        _stopbarSegments[pointId] = result;
        return result;
    }

    private static bool Crosses(double pLat0, double pLon0, double pLat1, double pLon1, double aLat, double aLon, double bLat, double bLon)
    {
        // Project to a local flat plane using simple equirectangular approximation around the stopbar midpoint for small distances.
        var midLat = (aLat + bLat) * 0.5;
        (double x, double y) P(double lat, double lon)
        {
            double x = (lon - aLon) * Math.Cos(midLat * Math.PI / 180.0) * 111320.0; // meters per deg lon
            double y = (lat - aLat) * 110540.0; // meters per deg lat
            return (x, y);
        }
        var p0 = P(pLat0, pLon0);
        var p1 = P(pLat1, pLon1);
        var a = (0.0, 0.0);
        var b = P(bLat, bLon);

        // Orientation signs relative to AB
        static double Orient((double x, double y) a, (double x, double y) b, (double x, double y) p)
            => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        var s0 = Orient(a, b, p0);
        var s1 = Orient(a, b, p1);

        // If signs are same or either is extremely close to zero, consider near-miss. We'll require sign flip and proximity.
        if (s0 == 0 || s1 == 0) return false;
        if (Math.Sign(s0) == Math.Sign(s1)) return false;

        // Ensure the perpendicular projection falls within segment extents and distance within tolerance
        static double Dot((double x, double y) u, (double x, double y) v) => u.x * v.x + u.y * v.y;
        static (double x, double y) Sub((double x, double y) u, (double x, double y) v) => (u.x - v.x, u.y - v.y);
        var ab = Sub(b, a);
        var ap0 = Sub(p0, a);
        var ap1 = Sub(p1, a);
        double abLen2 = Dot(ab, ab);
        if (abLen2 < 1) return false;
        // Closest approach from movement segment to AB
        // Compute intersection t on AB using average of projections from both endpoints (heuristic)
        var t0 = Math.Clamp(Dot(ap0, ab) / abLen2, 0, 1);
        var t1 = Math.Clamp(Dot(ap1, ab) / abLen2, 0, 1);
        var t = 0.5 * (t0 + t1);
        var closest = (x: a.Item1 + ab.x * t, y: a.Item2 + ab.y * t);
        // Distance from movement segment to closest point
        double DistPointToSeg((double x, double y) p, (double x, double y) u, (double x, double y) v)
        {
            var uv = Sub(v, u);
            var up = Sub(p, u);
            var tproj = Math.Clamp(Dot(up, uv) / (Dot(uv, uv) + 1e-6), 0, 1);
            var proj = (x: u.x + uv.x * tproj, y: u.y + uv.y * tproj);
            var dx = p.x - proj.x; var dy = p.y - proj.y; return Math.Sqrt(dx * dx + dy * dy);
        }
        var dist = DistPointToSeg(closest, p0, p1);
        const double tolMeters = 12.0; // crossing tolerance
        return dist <= tolMeters;
    }

    private async Task ProcessAsync(PointState ps, CancellationToken ct)
    {
        if (_suspended) return;
        var id = ps.Metadata.Id;
        var layouts = GetOrBuildLayouts(ps);
        if (layouts.Count == 0) return;
        var flight = _simManager.LatestState;
        if (ps.IsOn && _nextAttemptUtc.TryGetValue(id, out var next) && DateTime.UtcNow < next) { if (_latestStates.TryGetValue(id, out var latest) && (next - DateTime.UtcNow).TotalMilliseconds < _idleDelayMs * 4) _queue.Enqueue(latest); return; }
        if (ps.IsOn && _spawnFailures.TryGetValue(id, out var fi)) { var since = DateTime.UtcNow - fi.LastFailureUtc; if (fi.Failures >= FailureThresholdForCooldown && since < _failureCooldown) return; }
        if (ps.IsOn && _hardCooldownUntil.TryGetValue(id, out var hardUntil) && DateTime.UtcNow < hardUntil) return;
        ClassifyPointObjects(id, out var placeholders, out var variants);
        _logger.LogTrace("[ProcessState] {id} on={on} placeholders={ph}/{need} variants={varCnt}/{need}", id, ps.IsOn, placeholders.Count, layouts.Count, variants.Count, layouts.Count);

        // Guard: if we somehow have exploded variants count, trim extras (runaway protection)
        int runawayLimit = layouts.Count * 3;
        if (variants.Count > runawayLimit)
        {
            var excess = variants.Skip(layouts.Count).ToList(); // keep first layout.Count (arbitrary order)
            _logger.LogWarning("[Runaway] {id} variants={varCnt} expected={exp} trimming={trim}", id, variants.Count, layouts.Count, excess.Count);
            await RemoveObjectsAsync(excess, id, ct, "[RunawayTrim]");
            ClassifyPointObjects(id, out placeholders, out variants); // refresh
        }

        if (!ps.IsOn)
        {
            // OFF: Build full placeholder set, then remove ALL variants.
            if (placeholders.Count < layouts.Count)
            {
                int need = layouts.Count - placeholders.Count;
                await SpawnBatchAsync(id, layouts, need, isPlaceholder: true, ct);
                if (_latestStates.TryGetValue(id, out var latestOff)) _queue.Enqueue(latestOff); // re-evaluate later
                _logger.LogTrace("[OverlapPending] {id} placeholders={ph}/{need}", id, placeholders.Count, layouts.Count);
                return;
            }
            if (variants.Count > 0)
            {
                await RemoveObjectsAsync(variants, id, ct, "[OverlapRemove:Variants]");
                _logger.LogTrace("[OverlapRemovedVariants] {id}", id);
            }
            return;
        }

        // ON path: Build variants first then remove placeholders.
        if (variants.Count < layouts.Count)
        {
            int need = layouts.Count - variants.Count;
            await SpawnBatchAsync(id, layouts, need, isPlaceholder: false, ct);
            if (_latestStates.TryGetValue(id, out var latestOn)) _queue.Enqueue(latestOn);
            _logger.LogTrace("[OverlapPendingVariants] {id} variants={var}/{need}", id, variants.Count, layouts.Count);
            return;
        }
        if (placeholders.Count > 0)
        {
            await RemoveObjectsAsync(placeholders, id, ct, "[OverlapRemove:Placeholders]");
            _logger.LogTrace("[OverlapRemovedPlaceholders] {id}", id);
        }
    }

    private async Task SpawnBatchAsync(string pointId, IReadOnlyList<LightLayout> layouts, int maxToSpawn, bool isPlaceholder, CancellationToken ct)
    {
        if (maxToSpawn <= 0) return;
        int spawned = 0;
        for (int i = 0; i < layouts.Count && spawned < maxToSpawn; i++)
        {
            if (TotalActiveLightCount() >= _maxObjects)
            {
                bool freed = false;
                if (_dynamicPruneEnabled)
                {
                    try { freed = await EnsureCapacityForSpawnAsync(pointId, 1, ct); } catch (Exception ex) { _logger.LogDebug(ex, "[PruneError]"); }
                }
                if (!freed && TotalActiveLightCount() >= _maxObjects)
                {
                    Interlocked.Increment(ref _totalSkippedCap);
                    if (_latestStates.TryGetValue(pointId, out var latestCap)) _queue.Enqueue(latestCap);
                    break;
                }
            }
            if (!CanSpawnNow())
            {
                Interlocked.Increment(ref _totalDeferredRate);
                if (_latestStates.TryGetValue(pointId, out var latestRate)) _queue.Enqueue(latestRate);
                break;
            }
            var layout = layouts[i];
            int? variantState = layout.StateId;
            if (!isPlaceholder)
            {
                // Ensure we don't accidentally spawn placeholders for ON lights when StateId missing
                if (!variantState.HasValue || variantState == 0) variantState = 1; // default variant state
            }
            var desired = isPlaceholder ? layout with { StateId = 0 } : layout with { StateId = variantState };
            try
            {
                var handle = await SpawnLightAsync(pointId, desired, ct);
                Interlocked.Increment(ref _totalSpawnAttempts);
                if (handle == null) { RegisterSpawnFailure(pointId); break; }
                _spawnFailures.TryRemove(pointId, out _);
                var sid = desired.StateId ?? 0;
                _objectStateIds[handle.ObjectId] = sid;
                spawned++;
                _logger.LogTrace("[Spawned] {id} placeholder={ph} stateId={sid} obj={obj}", pointId, isPlaceholder, sid, handle.ObjectId);
            }
            catch (Exception ex)
            {
                RegisterSpawnFailure(pointId);
                _logger.LogDebug(ex, "[SpawnError:Batch] {id}", pointId);
                break;
            }
        }
    }

    private async Task RemoveObjectsAsync(List<SimObject> objects, string pointId, CancellationToken ct, string contextTag)
    {
        foreach (var obj in objects)
        {
            try { await DespawnLightAsync(obj, ct); Interlocked.Increment(ref _totalDespawned); _objectStateIds.TryRemove(obj.ObjectId, out _); }
            catch (Exception ex) { _logger.LogTrace(ex, "{tag} {id} obj={objId}", contextTag, pointId, obj.ObjectId); }
        }
        _logger.LogDebug("{tag} {id} removed={count} activeLights={active}", contextTag, pointId, objects.Count, TotalActiveLightCount());
    }

    private void TryCompleteOverlap(string pointId) { }

    private bool CanSpawnNow()
    {
        var now = DateTime.UtcNow;
        if (now > _nextSpawnWindow) { _nextSpawnWindow = now.AddSeconds(1); _spawnedThisWindow = 0; }
        if (_spawnedThisWindow < _spawnPerSecond) { _spawnedThisWindow++; return true; }
        return false;
    }

    private async Task<SimObject?> SpawnLightAsync(string pointId, LightLayout layout, CancellationToken ct)
    {
        if (_connector is not MsfsSimulatorConnector msfs || !msfs.IsConnected) return null;
        var clientField = typeof(MsfsSimulatorConnector).GetField("_client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var client = clientField?.GetValue(msfs) as SimConnect.NET.SimConnectClient;
        var mgr = client?.AIObjects;
        if (mgr == null) return null;
        await _spawnConcurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await mgr.CreateObjectAsync(ResolveModel(layout.StateId), new SimConnect.NET.SimConnectDataInitPosition
            {
                Latitude = layout.Latitude,
                Longitude = layout.Longitude,
                Altitude = 50,
                Heading = layout.Heading ?? 0,
                Pitch = 0,
                Bank = 0,
                OnGround = 1,
                Airspeed = 0
            }, userData: pointId, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Connector.Spawn.Fail] point={pointId} stateId={sid}", pointId, layout.StateId);
            throw;
        }
        finally { _spawnConcurrency.Release(); }
    }

    private Task DespawnLightAsync(SimObject simObject, CancellationToken ct)
    {
        if (_connector is not MsfsSimulatorConnector msfs) return Task.CompletedTask;
        var clientField = typeof(MsfsSimulatorConnector).GetField("_client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var client = clientField?.GetValue(msfs) as SimConnect.NET.SimConnectClient;
        var mgr = client?.AIObjects;
        if (mgr == null) return Task.CompletedTask;
        return mgr.RemoveObjectAsync(simObject, ct);
    }


    private int TotalActiveLightCount()
    {
        var mgr = GetManager();
        if (mgr == null) return 0;
        return mgr.ManagedObjects.Values.Count(o => o.IsActive && o.ContainerTitle.StartsWith("BARS_Light_", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record LightLayout(double Latitude, double Longitude, double? Heading, string? Color, int? StateId);

    private IReadOnlyList<LightLayout> GetOrBuildLayouts(PointState ps) => _layoutCache.GetOrAdd(ps.Metadata.Id, _ =>
    {
        IReadOnlyList<AirportStateHub.LightLayout> raw;
        if (!_hub.TryGetLightLayout(ps.Metadata.Id, out var hubLights) || hubLights.Count == 0)
            raw = new List<AirportStateHub.LightLayout> { new AirportStateHub.LightLayout(ps.Metadata.Latitude, ps.Metadata.Longitude, null, ps.Metadata.Color, null) };
        else raw = hubLights;
        return (IReadOnlyList<LightLayout>)raw.Select(l => new LightLayout(l.Latitude, l.Longitude, l.Heading, l.Color, l.StateId)).ToList();
    });

    // group spawning logic removed in manager-driven mode

    private void RegisterSpawnFailure(string pointId)
    {
        var now = DateTime.UtcNow;
        var updated = _spawnFailures.AddOrUpdate(pointId,
            _ => (1, now),
            (_, prev) => (prev.Failures + 1, now));

        // Dynamic backoff now exponential: 2^n * 400ms capped at 15s (pre threshold)
        var backoffMs = (int)Math.Min(Math.Pow(2, updated.Failures) * 400, 15000);
        if (updated.Failures >= FailureThresholdForCooldown)
        {
            // ensure at least failureCooldown (e.g. 10s) after threshold reached, escalate cap to 30s
            backoffMs = Math.Max(backoffMs, (int)_failureCooldown.TotalMilliseconds);
            backoffMs = Math.Min(backoffMs, 30000);
        }
        var next = now.AddMilliseconds(backoffMs);
        _nextAttemptUtc[pointId] = next;

        if (updated.Failures == FailureThresholdForCooldown)
            _logger.LogWarning("[SpawnFail:BackoffStart] {id} failures={fail} backoffMs={ms}", pointId, updated.Failures, backoffMs);
        else if (updated.Failures > FailureThresholdForCooldown)
            _logger.LogTrace("[SpawnFail:Backoff] {id} failures={fail} backoffMs={ms}", pointId, updated.Failures, backoffMs);
        else if (updated.Failures == 1)
            _logger.LogDebug("[SpawnFail] {id} firstFailure backoffMs={ms}", pointId, backoffMs);

        // Escalate to hard cooldown if failures very high (likely persistent model issue)
        if (updated.Failures == 6)
        {
            var hardUntil = now.AddMinutes(1);
            _hardCooldownUntil[pointId] = hardUntil;
            _logger.LogWarning("[SpawnFail:HardCooldownStart] {id} failures={fail} pauseUntil={until:O}", pointId, updated.Failures, hardUntil);
        }
    }

    // Overlap despawn removed in simplified implementation

    private async Task DespawnPointAsync(string pointId, CancellationToken ct)
    {
        var mgr = GetManager();
        if (mgr == null) return;
        var list = mgr.ManagedObjects.Values.Where(o => o.IsActive && o.UserData is string s && s == pointId).ToList();
        if (list.Count == 0) return;
        _logger.LogDebug("[DespawnPointStart] {id} count={count}", pointId, list.Count);
        foreach (var obj in list)
        {
            try { await DespawnLightAsync(obj, ct); Interlocked.Increment(ref _totalDespawned); _objectStateIds.TryRemove(obj.ObjectId, out _); }
            catch (Exception ex) { _logger.LogTrace(ex, "[DespawnPointError] {id} obj={objId}", pointId, obj.ObjectId); }
        }
        _logger.LogInformation("[DespawnPoint] {id} removed={removed} activeLights={active}", pointId, list.Count, TotalActiveLightCount());
    }

    // Perform ordering & pruning based on aircraft proximity.
    private Task ProximitySweepAsync(CancellationToken ct)
    {
        var flight = _simManager.LatestState;
        if (flight == null) return Task.CompletedTask;
        // Build active point set via manager
        var activePointIds = new HashSet<string>(StringComparer.Ordinal);
        var mgr = GetManager();
        if (mgr != null)
        {
            foreach (var o in mgr.ManagedObjects.Values)
                if (o.IsActive && o.UserData is string sid)
                    activePointIds.Add(sid);
        }
        // Radius-based despawn removed: keep all previously spawned objects; rely on global caps for safety.
        // Identify spawn candidates
        var candidates = new List<(PointState State, double Dist)>();
        foreach (var kv in _latestStates)
        {
            var st = kv.Value;
            if (!st.IsOn) continue;
            var dist = DistanceMeters(flight.Latitude, flight.Longitude, st.Metadata.Latitude, st.Metadata.Longitude);
            // Distance requirement removed; include all ON points (distance retained only for ordering)
            var (objs, _) = GetPointObjects(st.Metadata.Id);
            var layouts = GetOrBuildLayouts(st);
            if (objs.Count >= layouts.Count) continue;
            candidates.Add((st, dist));
        }
        if (candidates.Count == 0) return Task.CompletedTask;
        // Order by distance (closest first)
        foreach (var c in candidates.OrderBy(c => c.Dist))
        {
            if (ct.IsCancellationRequested) break;
            if (TotalActiveLightCount() >= _maxObjects) break;
            _queue.Enqueue(c.State); // enqueue for ProcessAsync which will respect cap & rate
        }
        _logger.LogTrace("[ProximityEnqueue] added={count} queue={q}", candidates.Count, _queue.Count);
        return Task.CompletedTask;
    }

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        // Haversine formula
        const double R = 6371000; // meters
        double dLat = DegreesToRadians(lat2 - lat1);
        double dLon = DegreesToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double DegreesToRadians(double deg) => deg * Math.PI / 180.0;

    private void ResyncActivePointsAfterLayout()
    {
        int queued = 0;
        foreach (var kv in _latestStates)
        {
            var ps = kv.Value;
            if (!ps.IsOn) continue;
            if (!_hub.TryGetLightLayout(ps.Metadata.Id, out var layout) || layout.Count == 0) continue;
            var (objs, _) = GetPointObjects(ps.Metadata.Id);
            if (objs.Count >= layout.Count) continue;
            _queue.Enqueue(ps);
            queued++;
        }
        if (queued > 0) _logger.LogInformation("Resync queued {count} active points for full layout spawn", queued);
    }

    /// <summary>
    /// Despawn all currently active SimObjects immediately (e.g. on server disconnect) without altering cached states.
    /// New incoming states will respawn as needed.
    /// </summary>
    public async Task DespawnAllAsync(CancellationToken ct = default)
    {
        var mgr = GetManager();
        if (mgr == null)
        {
            _logger.LogInformation("[DespawnAll] AI manager not available");
            return;
        }
        var ours = mgr.ManagedObjects.Values.Where(o => o.IsActive && o.ContainerTitle.StartsWith("BARS_Light_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (ours.Count == 0)
        {
            _logger.LogInformation("[DespawnAll] No active lights to remove");
            return;
        }
        _logger.LogInformation("[DespawnAllStart] lights={lights}", ours.Count);
        foreach (var obj in ours)
        {
            try { await DespawnLightAsync(obj, ct); Interlocked.Increment(ref _totalDespawned); _objectStateIds.TryRemove(obj.ObjectId, out _); }
            catch (Exception ex) { _logger.LogTrace(ex, "[DespawnAllError] obj={id}", obj.ObjectId); }
        }
        _logger.LogInformation("[DespawnAll] removedLights={removed} activeLights={active}", ours.Count, TotalActiveLightCount());
    }

    private (List<SimObject> Objects, int Count) GetPointObjects(string pointId)
    {
        var mgr = GetManager();
        if (mgr == null) return (new List<SimObject>(), 0);
        var list = mgr.ManagedObjects.Values.Where(o => o.IsActive && o.UserData is string s && s == pointId).ToList();
        return (list, list.Count);
    }

    private SimObjectManager? GetManager()
    {
        if (_connector is not MsfsSimulatorConnector msfs) return null;
        var clientField = typeof(MsfsSimulatorConnector).GetField("_client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var client = clientField?.GetValue(msfs) as SimConnect.NET.SimConnectClient;
        return client?.AIObjects;
    }

    private static string ResolveModel(int? stateId)
    {
        if (!stateId.HasValue) return "BARS_Light_0";
        var s = stateId.Value; if (s < 0) s = 0; return $"BARS_Light_{s}";
    }
    private async Task<bool> EnsureCapacityForSpawnAsync(string priorityPointId, int requiredSlots, CancellationToken ct)
    {
        var flight = _simManager.LatestState;
        if (flight == null) return false;
        if (TotalActiveLightCount() + requiredSlots < _maxObjects) return true; // already enough
        var mgr = GetManager();
        if (mgr == null) return false;

        // Build distinct active point set with object counts
        var pointCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in mgr.ManagedObjects.Values)
        {
            if (!o.IsActive || o.UserData is not string pid) continue;
            if (!pointCounts.TryAdd(pid, 1)) pointCounts[pid]++;
        }
        if (pointCounts.Count == 0) return false;
        // Build distance list
        var distances = new List<(string PointId, double Dist, int Count)>();
        foreach (var kv in pointCounts)
        {
            if (!_latestStates.TryGetValue(kv.Key, out var ps)) continue; // stale
            var d = DistanceMeters(flight.Latitude, flight.Longitude, ps.Metadata.Latitude, ps.Metadata.Longitude);
            distances.Add((kv.Key, d, kv.Value));
        }
        if (distances.Count == 0) return false;

        // Order farthest first, but never prune the priority point
        foreach (var item in distances.OrderByDescending(d => d.Dist))
        {
            if (item.PointId == priorityPointId) continue;
            if (TotalActiveLightCount() + requiredSlots < _maxObjects) break;
            _logger.LogTrace("[PruneBegin] freeing point={id} dist={dist:F0}m count={count} active={active}/{cap}", item.PointId, item.Dist, item.Count, TotalActiveLightCount(), _maxObjects);
            try { await DespawnPointAsync(item.PointId, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "[PruneFail] point={id}", item.PointId); }
        }

        var success = TotalActiveLightCount() + requiredSlots <= _maxObjects;
        if (success) _logger.LogTrace("[PruneSuccess] priority={prio} needed={need} active={active}/{cap}", priorityPointId, requiredSlots, TotalActiveLightCount(), _maxObjects);
        else _logger.LogDebug("[PruneInsufficient] priority={prio} needed={need} active={active}/{cap}", priorityPointId, requiredSlots, TotalActiveLightCount(), _maxObjects);
        return success;
    }

    private void ClassifyPointObjects(string pointId, out List<SimObject> placeholders, out List<SimObject> variants)
    {
        placeholders = new List<SimObject>();
        variants = new List<SimObject>();
        var (objs, _) = GetPointObjects(pointId);
        foreach (var o in objs)
        {
            int sid;
            if (!_objectStateIds.TryGetValue(o.ObjectId, out sid))
            {
                // Fallback: attempt parse from title tail
                sid = 0;
                try
                {
                    var title = o.ContainerTitle ?? string.Empty;
                    var tail = title.Split('_').LastOrDefault();
                    if (int.TryParse(tail, out var parsed)) sid = parsed; else sid = 0; // default placeholder assumption
                }
                catch { sid = 0; }
            }
            if (sid == 0) placeholders.Add(o); else variants.Add(o);
        }
    }
}

internal sealed class MsfsPointControllerOptions
{
    public int MaxObjects { get; init; } = 900;
    public int SpawnPerSecond { get; init; } = 20;
    public int IdleDelayMs { get; init; } = 10;
    public int DisconnectedDelayMs { get; init; } = 500;
    public int ErrorBackoffMs { get; init; } = 200;
    public int OverlapDespawnDelayMs { get; init; } = 1000;
    public double SpawnRadiusMeters { get; init; } = 8000;
    public int ProximitySweepSeconds { get; init; } = 5;
    public bool DynamicPruneEnabled { get; init; } = true;
}
