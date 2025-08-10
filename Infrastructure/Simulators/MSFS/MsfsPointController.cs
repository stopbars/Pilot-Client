using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
// XML parsing handled centrally in AirportStateHub
using BARS_Client_V2.Domain;
using BARS_Client_V2.Infrastructure.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
using SimConnect.NET.AI;

namespace BARS_Client_V2.Infrastructure.Simulators.Msfs;

/// <summary>
/// Queues point state changes and (eventually) reflects them inside MSFS by spawning / updating custom SimObjects.
/// Currently contains stubs for spawn/despawn until concrete SimObject titles & WASM variables are defined.
/// </summary>
internal sealed class MsfsPointController : BackgroundService, IPointStateListener
{
    private readonly ILogger<MsfsPointController> _logger;
    private readonly ISimulatorConnector _connector; // rely on same DI instance (assumed MSFS)
    private readonly ConcurrentQueue<PointState> _queue = new();
    // pointId -> spawned group (stores sim objects and their layout positions)
    private readonly Dictionary<string, SpawnGroup> _spawned = new();
    // Synchronize access to _spawned to allow external mass-despawn while background loop runs
    private readonly object _spawnLock = new();

    // Track latest states and reference to central hub for layouts
    private readonly ConcurrentDictionary<string, PointState> _latestStates = new();
    private readonly AirportStateHub _hub;

    private readonly int _maxObjects;
    private readonly int _spawnPerSecond;
    private DateTime _nextSpawnWindow = DateTime.UtcNow;
    private int _spawnedThisWindow;
    private readonly int _idleDelayMs;
    private readonly int _disconnectedDelayMs;
    private readonly int _errorBackoffMs;
    private readonly int _overlapDespawnDelayMs;

    // Stats
    private long _totalReceived;
    private long _totalSpawnAttempts;
    private long _totalSpawned;
    private long _totalDespawned;
    private long _totalDeferredRate;
    private long _totalSkippedCap;
    private DateTime _lastSummary = DateTime.UtcNow;

    // Track repeated spawn failures per point to apply a cooldown and avoid flooding SimConnect
    private readonly ConcurrentDictionary<string, (int Failures, DateTime LastFailureUtc)> _spawnFailures = new();
    private readonly TimeSpan _failureCooldown = TimeSpan.FromSeconds(10); // per point pause after repeated failures
    private const int FailureThresholdForCooldown = 3; // consecutive failures before we start cooldown
    private readonly ConcurrentDictionary<string, DateTime> _nextAttemptUtc = new(); // per-point dynamic backoff
    // For overlap transitions store old handles until replacement group fully spawned
    private readonly ConcurrentDictionary<string, List<SimObject>> _pendingOverlapOld = new();

    public MsfsPointController(IEnumerable<ISimulatorConnector> connectors,
        ILogger<MsfsPointController> logger,
        AirportStateHub hub,
        MsfsPointControllerOptions? options = null)
    {
        // Select the MSFS connector (first one with SimulatorId == MSFS or just first)
        _connector = connectors.FirstOrDefault(c => string.Equals(c.SimulatorId, "MSFS", StringComparison.OrdinalIgnoreCase))
                      ?? connectors.First();
        _logger = logger;
        options ??= new MsfsPointControllerOptions();
        _maxObjects = options.MaxObjects;
        _spawnPerSecond = options.SpawnPerSecond;
        _hub = hub;
        _hub.PointStateChanged += OnPointStateChanged;
        _hub.MapLoaded += _ => ResyncActivePointsAfterLayout();

        _idleDelayMs = options.IdleDelayMs;
        _disconnectedDelayMs = options.DisconnectedDelayMs;
        _errorBackoffMs = options.ErrorBackoffMs;
        _overlapDespawnDelayMs = options.OverlapDespawnDelayMs;
    }

    public void OnPointStateChanged(PointState state)
    {
        _latestStates[state.Metadata.Id] = state;
        _queue.Enqueue(state);
        var qLen = _queue.Count;
        var meta = state.Metadata;
        Interlocked.Increment(ref _totalReceived);
        _logger.LogInformation("[Recv] {id} on={on} type={type} name={name} airport={apt} lat={lat:F6} lon={lon:F6} queue={q}",
            meta.Id, state.IsOn, meta.Type, meta.Name, meta.AirportId, meta.Latitude, meta.Longitude, qLen);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MsfsPointController started (max={max} rate/s={rate})", _maxObjects, _spawnPerSecond);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_connector.IsConnected)
                {
                    if ((_totalReceived % 25) == 0) _logger.LogDebug("[Loop] Waiting for simulator connection. Queue={q}", _queue.Count);
                    await Task.Delay(_disconnectedDelayMs, stoppingToken);
                    continue;
                }
                if (_queue.TryDequeue(out var ps))
                {
                    _logger.LogTrace("Dequeued {id} on={on}", ps.Metadata.Id, ps.IsOn);
                    await ProcessAsync(ps, stoppingToken);
                }
                else
                {
                    // Occasionally log idle state
                    if (DateTime.UtcNow.Second % 20 == 0)
                        _logger.LogTrace("[Idle] queue empty activePoints={points} totalLights={lights}", _spawned.Count, TotalActiveLightCount());
                    await Task.Delay(_idleDelayMs, stoppingToken); // reduced idle backoff
                }

                // Periodic summary every 30s
                if ((DateTime.UtcNow - _lastSummary) > TimeSpan.FromSeconds(30))
                {
                    _lastSummary = DateTime.UtcNow;
                    _logger.LogInformation("[Summary] received={rec} spawned={sp} active={active} despawned={des} deferredRate={def} skippedCap={cap} queue={q}",
                        _totalReceived, _totalSpawned, _spawned.Count, _totalDespawned, _totalDeferredRate, _totalSkippedCap, _queue.Count);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in MsfsPointController loop");
                try { await Task.Delay(_errorBackoffMs, stoppingToken); } catch { }
            }
        }
    }

    private async Task ProcessAsync(PointState ps, CancellationToken ct)
    {
        var id = ps.Metadata.Id;
        bool exists; SpawnGroup? existingGroup;
        lock (_spawnLock) exists = _spawned.TryGetValue(id, out existingGroup);

        var layouts = GetOrBuildLayouts(ps, existingGroup?.Layouts);
        if (layouts.Count == 0) return;
        bool toOn = ps.IsOn;
        bool isToggle = exists && existingGroup != null && existingGroup.IsOn != toOn;

        if (!toOn)
        {
            // OFF transition behavior:
            // 1. If currently ON: spawn a full set of OFF placeholders (stateId=0) overlapping existing ON lights, then delayed-despawn old lights.
            // 2. If already OFF but incomplete: incrementally spawn missing OFF placeholders.
            // 3. If already OFF and full: skip.
            if (exists && existingGroup != null && !existingGroup.IsOn)
            {
                // Already off placeholders present
                if (existingGroup.Objects.Count >= layouts.Count)
                {
                    _logger.LogTrace("[ProcessSkip:OffFull] {id} lights={lights}", id, existingGroup.Objects.Count);
                    return;
                }
                // Need to top up missing placeholders
                await SpawnMissingAsync(id, existingGroup, layouts, ct, isOn: false, useLayoutStateIds: false, forcedStateId: 0);
                TryCompleteOverlap(id);
                return;
            }

            // Was ON -> overlap spawn OFF placeholders (stateId=0) but keep old ON lights until full set of OFF spawned
            if (exists && existingGroup != null && existingGroup.IsOn)
            {
                // store old handles if first time in this overlap
                _pendingOverlapOld.AddOrUpdate(id,
                    _ => existingGroup.Objects.ToList(),
                    (_, prev) => prev); // keep original list
            }
            await SpawnGroupFreshAsync(id, layouts, useLayoutStateIds: false, ct, ignoreCapForTransition: true, isOn: false, forcedStateId: 0);
            TryCompleteOverlap(id);
            return;
        }

        // ON flow
        if (!isToggle && exists && existingGroup != null && existingGroup.IsOn && existingGroup.Objects.Count >= layouts.Count)
        {
            _logger.LogTrace("[ProcessSkip:OnFull] {id} lights={lights}", id, existingGroup.Objects.Count);
            return;
        }

        // Backoff (ON only)
        if (_nextAttemptUtc.TryGetValue(id, out var next) && DateTime.UtcNow < next)
        {
            var remaining = (next - DateTime.UtcNow).TotalMilliseconds;
            if (remaining > 0 && remaining < 1500)
                _logger.LogTrace("[ProcessDefer:Backoff] {id} msLeft={ms:F0}", id, remaining);
            if (remaining < _idleDelayMs * 4 && _latestStates.TryGetValue(id, out var latestBack))
                _queue.Enqueue(latestBack);
            return;
        }

        // Failure cooldown (ON only)
        if (_spawnFailures.TryGetValue(id, out var failureInfo))
        {
            var since = DateTime.UtcNow - failureInfo.LastFailureUtc;
            if (failureInfo.Failures >= FailureThresholdForCooldown && since < _failureCooldown)
            {
                if (since.TotalSeconds < 1)
                    _logger.LogDebug("[ProcessDefer:FailureCooldown] {id} remainingMs={ms:F0}", id, (_failureCooldown - since).TotalMilliseconds);
                if ((_failureCooldown - since) < TimeSpan.FromMilliseconds(_idleDelayMs * 5) && _latestStates.TryGetValue(id, out var latestCool))
                    _queue.Enqueue(latestCool);
                return;
            }
        }

        if (exists && existingGroup != null)
        {
            // If coming from OFF -> ON toggle initiate overlap (store old off placeholders)
            if (isToggle && !existingGroup.IsOn)
            {
                _pendingOverlapOld.AddOrUpdate(id,
                    _ => existingGroup.Objects.ToList(),
                    (_, prev) => prev);
            }
            if (existingGroup.IsOn && existingGroup.Objects.Count < layouts.Count)
            {
                await SpawnMissingAsync(id, existingGroup, layouts, ct, isOn: true, useLayoutStateIds: true, forcedStateId: null);
                TryCompleteOverlap(id);
                return;
            }
        }

        // Fresh (new point or transitioning from OFF with no existing ON objects recorded yet)
        await SpawnGroupFreshAsync(id, layouts, useLayoutStateIds: true, ct, ignoreCapForTransition: isToggle, isOn: true, forcedStateId: null);
        TryCompleteOverlap(id);
    }

    private void TryCompleteOverlap(string pointId)
    {
        if (!_pendingOverlapOld.TryGetValue(pointId, out var oldHandles)) return;
        SpawnGroup? current;
        lock (_spawnLock)
        {
            if (!_spawned.TryGetValue(pointId, out current)) return;
        }
        if (current == null || current.Objects.Count < current.Layouts.Count) return; // not yet full
        // Full new group spawned -> remove old
        if (_pendingOverlapOld.TryRemove(pointId, out oldHandles))
        {
            _ = Task.Run(async () =>
            {
                // Validate that the current cached desired state matches the replacement group's IsOn to avoid race conditions.
                if (_latestStates.TryGetValue(pointId, out var desired))
                {
                    if (desired.IsOn != current.IsOn)
                    {
                        _logger.LogTrace("[OverlapAbort:StateChanged] {id} desiredOn={des} currentOn={cur}", pointId, desired.IsOn, current.IsOn);
                        // Re-store old handles (in case another transition will clean them up) and exit.
                        _pendingOverlapOld[pointId] = oldHandles;
                        return;
                    }
                }
                int removed = 0;
                foreach (var h in oldHandles)
                {
                    try
                    {
                        await DespawnLightAsync(h, CancellationToken.None);
                        Interlocked.Increment(ref _totalDespawned);
                        removed++;
                    }
                    catch { }
                }
                _logger.LogDebug("[OverlapComplete] {id} oldRemoved={removed}", pointId, removed);
            });
        }
    }

    private bool CanSpawnNow()
    {
        var now = DateTime.UtcNow;
        if (now > _nextSpawnWindow)
        {
            _nextSpawnWindow = now.AddSeconds(1);
            _spawnedThisWindow = 0;
        }
        if (_spawnedThisWindow < _spawnPerSecond)
        {
            _spawnedThisWindow++;
            return true;
        }
        return false;
    }

    private async Task<SimObject?> SpawnLightAsync(string pointId, LightLayout layout, CancellationToken ct)
    {
        var connector = _connector as MsfsSimulatorConnector;
        if (connector == null) return null;
        _logger.LogTrace("[SpawnLightAsyncCall] point={id} stateId={sid} lat={lat:F6} lon={lon:F6} hdg={hdg:F1}", pointId, layout.StateId, layout.Latitude, layout.Longitude, layout.Heading ?? 0);
        var simObj = await connector.SpawnLightAsync(pointId, layout.Latitude, layout.Longitude, layout.Heading, layout.StateId, ct);
        if (simObj != null)
        {
            _logger.LogTrace("Spawned SimObject {obj} for {id}", simObj.ObjectId, pointId);
        }
        return simObj;
    }

    private Task DespawnLightAsync(SimObject simObject, CancellationToken ct)
    {
        var connector = _connector as MsfsSimulatorConnector;
        if (connector == null) return Task.CompletedTask;
        return connector.DespawnLightAsync(simObject, ct);
    }


    private int TotalActiveLightCount()
    {
        lock (_spawnLock) return _spawned.Values.Sum(g => g.Objects.Count);
    }

    private sealed record LightLayout(double Latitude, double Longitude, double? Heading, string? Color, int? StateId);

    private sealed record SpawnGroup(IReadOnlyList<SimObject> Objects, IReadOnlyList<LightLayout> Layouts, bool IsOn);

    private IReadOnlyList<LightLayout> GetOrBuildLayouts(PointState ps, IReadOnlyList<LightLayout>? existingLayouts)
    {
        if (existingLayouts != null && existingLayouts.Count > 0) return existingLayouts;
        IReadOnlyList<AirportStateHub.LightLayout> rawLights;
        if (!_hub.TryGetLightLayout(ps.Metadata.Id, out var hubLights) || hubLights.Count == 0)
        {
            rawLights = new List<AirportStateHub.LightLayout> { new AirportStateHub.LightLayout(ps.Metadata.Latitude, ps.Metadata.Longitude, null, ps.Metadata.Color, null) };
        }
        else rawLights = hubLights;
        return rawLights.Select(l => new LightLayout(l.Latitude, l.Longitude, l.Heading, l.Color, l.StateId)).ToList();
    }

    // Full (fresh) spawn, used only for new points or layout reduction / state change requiring full replace
    private async Task SpawnGroupFreshAsync(string pointId, IReadOnlyList<LightLayout> layouts, bool useLayoutStateIds, CancellationToken ct, bool ignoreCapForTransition = false, bool isOn = true, int? forcedStateId = null)
    {
        var spawnedHandles = new List<SimObject>();
        foreach (var layout in layouts)
        {
            if (!ignoreCapForTransition && TotalActiveLightCount() >= _maxObjects)
            {
                Interlocked.Increment(ref _totalSkippedCap);
                _logger.LogWarning("[SpawnSkip:Cap] {id} cap={cap} active={active}", pointId, _maxObjects, TotalActiveLightCount());
                break;
            }
            if (!CanSpawnNow())
            {
                Interlocked.Increment(ref _totalDeferredRate);
                _logger.LogDebug("[SpawnDefer:Rate] {id} remainingLayoutsWillRetry", pointId);
                // requeue a synthetic point state using latest cached state so we finish group later
                if (_latestStates.TryGetValue(pointId, out var latest)) _queue.Enqueue(latest);
                break;
            }
            int? stateId;
            if (forcedStateId.HasValue)
                stateId = forcedStateId.Value;
            else if (useLayoutStateIds)
                stateId = layout.StateId;
            else
                stateId = null; // base model (off)
            SimObject? handle = null;
            try
            {
                handle = await SpawnLightAsync(pointId, new LightLayout(layout.Latitude, layout.Longitude, layout.Heading, layout.Color, stateId), ct);
                if (handle != null)
                {
                    spawnedHandles.Add(handle);
                    // reset failure streak on success
                    _spawnFailures.TryRemove(pointId, out _);
                }
                else
                {
                    // treat null as a failure (e.g., connector not MSFS)
                    RegisterSpawnFailure(pointId);
                }
            }
            catch (Exception ex)
            {
                RegisterSpawnFailure(pointId);
                _logger.LogDebug(ex, "[SpawnError] {id} stateId={sid}", pointId, stateId ?? -1);
                // After a failure, break to avoid rapid-fire attempts this tick
                break;
            }
        }
        if (spawnedHandles.Count > 0)
        {
            lock (_spawnLock)
            {
                _spawned[pointId] = new SpawnGroup(spawnedHandles, layouts, isOn);
                Interlocked.Add(ref _totalSpawned, spawnedHandles.Count);
                _logger.LogInformation("[SpawnGroup] {id} lights={lights} activePoints={points} totalLights={total} overlapCapIgnore={ignoreCap} isOn={on}", pointId, spawnedHandles.Count, _spawned.Count, TotalActiveLightCount(), ignoreCapForTransition, isOn);
            }
        }
    }

    // Incrementally spawn only missing layouts (no overlap) for partially-complete points
    private async Task SpawnMissingAsync(string pointId, SpawnGroup existing, IReadOnlyList<LightLayout> layouts, CancellationToken ct, bool isOn, bool useLayoutStateIds, int? forcedStateId)
    {
        int already = existing.Objects.Count;
        if (already >= layouts.Count) return;
        var newHandles = new List<SimObject>();
        for (int i = already; i < layouts.Count; i++)
        {
            var layout = layouts[i];
            int? stateId;
            if (forcedStateId.HasValue) stateId = forcedStateId.Value;
            else if (useLayoutStateIds) stateId = layout.StateId;
            else stateId = 0; // off placeholder
            if (TotalActiveLightCount() >= _maxObjects)
            {
                Interlocked.Increment(ref _totalSkippedCap);
                _logger.LogWarning("[SpawnSkip:Cap] {id} cap={cap} active={active} partialProgress={done}/{total}", pointId, _maxObjects, TotalActiveLightCount(), i, layouts.Count);
                break;
            }
            if (!CanSpawnNow())
            {
                Interlocked.Increment(ref _totalDeferredRate);
                _logger.LogDebug("[SpawnDefer:Rate] {id} partialProgress={done}/{total}", pointId, i, layouts.Count);
                if (_latestStates.TryGetValue(pointId, out var latest)) _queue.Enqueue(latest);
                break;
            }
            try
            {
                var handle = await SpawnLightAsync(pointId, new LightLayout(layout.Latitude, layout.Longitude, layout.Heading, layout.Color, stateId), ct);
                if (handle != null)
                {
                    newHandles.Add(handle);
                    _spawnFailures.TryRemove(pointId, out _); // reset on any success
                }
                else RegisterSpawnFailure(pointId);
            }
            catch (Exception ex)
            {
                RegisterSpawnFailure(pointId);
                _logger.LogDebug(ex, "[SpawnError:Incremental] {id} index={idx}", pointId, i);
                break; // stop this cycle on first exception
            }
        }
        if (newHandles.Count > 0)
        {
            lock (_spawnLock)
            {
                // Merge with existing handles
                var merged = existing.Objects.Concat(newHandles).ToList();
                _spawned[pointId] = new SpawnGroup(merged, layouts, isOn);
                Interlocked.Add(ref _totalSpawned, newHandles.Count);
                _logger.LogInformation("[SpawnGroup:Incremental] {id} added={added} now={now}/{total} activePoints={points} totalLights={lights} isOn={on}", pointId, newHandles.Count, merged.Count, layouts.Count, _spawned.Count, TotalActiveLightCount(), isOn);
            }
        }
    }

    private void RegisterSpawnFailure(string pointId)
    {
        var now = DateTime.UtcNow;
        var updated = _spawnFailures.AddOrUpdate(pointId,
            _ => (1, now),
            (_, prev) => (prev.Failures + 1, now));

        // Dynamic backoff: base 500ms * failures^2 (capped) and extra fixed cooldown after threshold
        var backoffMs = Math.Min(500 * updated.Failures * updated.Failures, 8000); // cap 8s
        if (updated.Failures >= FailureThresholdForCooldown)
        {
            // ensure at least failureCooldown (e.g. 10s) after threshold reached
            backoffMs = Math.Max(backoffMs, (int)_failureCooldown.TotalMilliseconds);
        }
        var next = now.AddMilliseconds(backoffMs);
        _nextAttemptUtc[pointId] = next;

        if (updated.Failures == FailureThresholdForCooldown)
            _logger.LogWarning("[SpawnFail:BackoffStart] {id} failures={fail} backoffMs={ms}", pointId, updated.Failures, backoffMs);
        else if (updated.Failures > FailureThresholdForCooldown)
            _logger.LogTrace("[SpawnFail:Backoff] {id} failures={fail} backoffMs={ms}", pointId, updated.Failures, backoffMs);
        else if (updated.Failures == 1)
            _logger.LogDebug("[SpawnFail] {id} firstFailure backoffMs={ms}", pointId, backoffMs);
    }

    private async Task DelayedDespawnAsync(string pointId, List<SimObject> handles, int delayMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
            _logger.LogTrace("[OverlapDespawn] {id} delayMs={delay} count={count}", pointId, delayMs, handles.Count);
            foreach (var h in handles)
            {
                try
                {
                    await DespawnLightAsync(h, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _totalDespawned);
                }
                catch { }
            }
            _logger.LogDebug("[OverlapDespawnDone] {id} removed={removed}", pointId, handles.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OverlapDespawnError] {id}", pointId);
        }
    }

    private async Task DespawnGroupAsync(string pointId, SpawnGroup group, CancellationToken ct)
    {
        List<SimObject> handles;
        lock (_spawnLock)
        {
            if (!_spawned.Remove(pointId, out var existing)) return;
            handles = existing.Objects.ToList();
        }
        _logger.LogDebug("[DespawnGroupStart] {id} handles={count}", pointId, handles.Count);
        foreach (var h in handles)
        {
            await DespawnLightAsync(h, ct);
            Interlocked.Increment(ref _totalDespawned);
        }
        _logger.LogInformation("[DespawnGroup] {id} removedLights={removed} remainingPoints={points} totalLights={total}", pointId, handles.Count, _spawned.Count, TotalActiveLightCount());
    }

    private void ResyncActivePointsAfterLayout()
    {
        int queued = 0;
        foreach (var kv in _latestStates)
        {
            var ps = kv.Value;
            if (!ps.IsOn) continue;
            if (!_hub.TryGetLightLayout(ps.Metadata.Id, out var layout) || layout.Count == 0) continue;
            bool alreadyFull = false;
            lock (_spawnLock)
            {
                if (_spawned.TryGetValue(ps.Metadata.Id, out var existing) && existing.Objects.Count >= layout.Count) alreadyFull = true;
            }
            if (alreadyFull) continue; // already full
            // Queue for reprocessing to spawn full light group
            _queue.Enqueue(ps);
            queued++;
        }
        if (queued > 0)
        {
            _logger.LogInformation("Resync queued {count} active points for full layout spawn", queued);
        }
    }

    /// <summary>
    /// Despawn all currently active SimObjects immediately (e.g. on server disconnect) without altering cached states.
    /// New incoming states will respawn as needed.
    /// </summary>
    public async Task DespawnAllAsync(CancellationToken ct = default)
    {
        List<SimObject> all;
        int pointCount;
        lock (_spawnLock)
        {
            all = _spawned.Values.SelectMany(v => v.Objects).ToList();
            pointCount = _spawned.Count;
            _spawned.Clear();
        }
        if (all.Count == 0)
        {
            _logger.LogInformation("[DespawnAll] No active lights to remove");
            return;
        }
        _logger.LogInformation("[DespawnAllStart] points={points} lights={lights}", pointCount, all.Count);
        foreach (var obj in all)
        {
            try
            {
                await DespawnLightAsync(obj, ct);
                Interlocked.Increment(ref _totalDespawned);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DespawnAllAsync failed for {obj}", obj.ObjectId);
            }
        }
        _logger.LogInformation("[DespawnAll] removedLights={removed} remainingPoints=0 totalLights=0", all.Count);
    }
}

internal sealed class MsfsPointControllerOptions
{
    public int MaxObjects { get; init; } = 1000;
    public int SpawnPerSecond { get; init; } = 20;
    public int IdleDelayMs { get; init; } = 10;
    public int DisconnectedDelayMs { get; init; } = 500;
    public int ErrorBackoffMs { get; init; } = 200;
    public int OverlapDespawnDelayMs { get; init; } = 1000;
}
