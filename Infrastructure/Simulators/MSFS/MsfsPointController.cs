using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BARS_Client_V2.Application;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Infrastructure.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimConnect.NET.AI;

namespace BARS_Client_V2.Infrastructure.Simulators.Msfs;

/// <summary>
/// Event args for debug mode state changes.
/// </summary>
public sealed class DebugModeChangedEventArgs : EventArgs
{
    public bool IsDebugMode { get; }
    public int CurrentStateId { get; }
    public string? ClosestPointId { get; }

    public DebugModeChangedEventArgs(bool isDebugMode, int currentStateId, string? closestPointId)
    {
        IsDebugMode = isDebugMode;
        CurrentStateId = currentStateId;
        ClosestPointId = closestPointId;
    }
}

/// <summary>
/// Distance-based point controller that keeps MSFS SimObjects in sync with the server state
/// within a visibility radius around the aircraft. Server state is the sole source of truth;
/// spawned state simply mirrors whichever objects are currently inside the visibility bubble.
/// </summary>
public sealed class MsfsPointController : BackgroundService, IPointStateListener
{
    private readonly ILogger<MsfsPointController> _logger;
    private readonly AirportStateHub _hub;
    private readonly SimulatorManager _simManager;
    private readonly ISimulatorConnector _connector;
    private readonly MsfsPointControllerOptions _options;
    private readonly LightDrawDistanceSettings? _drawDistance;
    private double _visibilityRadiusMeters;
    private double RequestedVisibilityRadiusMeters => _drawDistance?.Meters ?? _options.VisibilityRadiusMeters;
    private static readonly FieldInfo? MsfsConnectorClientField = typeof(MsfsSimulatorConnector)
        .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly ConcurrentDictionary<string, PointState> _serverStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SpawnedPoint> _spawnedPoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<AirportStateHub.LightLayout>> _layoutCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _updateVersions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AlternatingTransition> _alternatingTransitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queuedUpdates = new(StringComparer.Ordinal);
    private readonly Channel<string> _pendingUpdates = Channel.CreateBounded<string>(new BoundedChannelOptions(8192)
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    private readonly object _visibilityLock = new();
    private readonly HashSet<string> _visiblePointIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, (string PointId, int Slot)> _objectIndex = new();
    private readonly SemaphoreSlim _spawnSemaphore = new(1, 1);

    private readonly object _rateLock = new();
    private TimeSpan _perSpawnInterval;
    private DateTime _nextSpawnUtc = DateTime.MinValue;
    private DateTime _nextVisibilitySweepUtc = DateTime.MinValue;
    private SimObjectManager? _cachedManager;
    private readonly List<string> _orderedWorkset = new();
    private readonly List<string> _visibilitySnapshot = new();
    private readonly List<string> _staleVisibilityIds = new();
    private readonly List<SpawnRequest> _spawnQueue = new();

    private (double Lat, double Lon)? _lastCenter;
    private volatile bool _suspended;
    private volatile string? _currentAirportIcao;
    private volatile bool _mapResetInProgress;
    private long _mapGeneration;
    private int _queueOverflowed;

    // Debug mode fields
    private static readonly int[] DebugStateSequence = { 0, 1, 2, 3, 4, 5, 6, 7, 20, 21, 22, 23, 24, 25, 26, 27 };
    private readonly object _debugLock = new();
    private volatile bool _debugMode;
    private string? _debugPointId;
    private int _debugStateIndex;
    private DateTime _debugNextCycleUtc = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, PointState> _frozenStates = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised when debug mode is toggled or the debug state changes.
    /// </summary>
    public event EventHandler<DebugModeChangedEventArgs>? DebugModeChanged;

    /// <summary>
    /// Gets whether debug mode is currently active.
    /// </summary>
    public bool IsDebugMode => _debugMode;

    public MsfsPointController(IEnumerable<ISimulatorConnector> connectors,
                               ILogger<MsfsPointController> logger,
                               AirportStateHub hub,
                               SimulatorManager simManager,
                               MsfsPointControllerOptions? options = null,
                               LightDrawDistanceSettings? drawDistance = null)
    {
        _logger = logger;
        _hub = hub;
        _simManager = simManager;
        _options = options ?? new MsfsPointControllerOptions();
        _drawDistance = drawDistance;
        _visibilityRadiusMeters = RequestedVisibilityRadiusMeters;
        _connector = connectors.FirstOrDefault(c => c.SimulatorId.Equals("MSFS", StringComparison.OrdinalIgnoreCase))
                     ?? connectors.First();

        _hub.PointStateChanged += OnPointStateChanged;
        _hub.MultiPointStateChanged += OnMultiPointStateChanged;
        _hub.MapLoaded += OnMapLoaded;
        _hub.TestingModeChanged += OnTestingModeChanged;

        _perSpawnInterval = _options.SpawnRatePerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1.0 / _options.SpawnRatePerSecond);
    }

    public void OnPointStateChanged(PointState state)
    {
        var pointId = state.Metadata.Id;
        _logger.LogDebug("[PointUpdate] Received state change point={pointId} on={isOn}",
            pointId, state.IsOn);

        // In debug mode, freeze all incoming state updates
        if (_debugMode)
        {
            _frozenStates[pointId] = state;
            _logger.LogDebug("[PointUpdate] Debug mode active, freezing state for point={pointId}", pointId);
            return;
        }

        _serverStates[pointId] = state;
        _alternatingTransitions.TryRemove(pointId, out _);
        IncrementVersion(pointId);
        QueuePointSync(pointId);
    }

    private void OnMultiPointStateChanged(IReadOnlyList<PointState> updates)
    {
        if (updates == null || updates.Count == 0)
        {
            return;
        }

        var airportIcao = _currentAirportIcao;
        if (string.IsNullOrWhiteSpace(airportIcao) || !airportIcao.StartsWith("Y", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var hasStopbarOff = updates.Any(p => IsStopbarType(p.Metadata.Type) && !p.IsOn);
        var hasLeadonOn = updates.Any(p => IsLeadonType(p.Metadata.Type) && p.IsOn);
        var hasStopbarOn = updates.Any(p => IsStopbarType(p.Metadata.Type) && p.IsOn);
        var hasLeadonOff = updates.Any(p => IsLeadonType(p.Metadata.Type) && !p.IsOn);

        var enableLeadonPattern = hasStopbarOff && hasLeadonOn;
        var disableLeadonPattern = hasStopbarOn && hasLeadonOff;
        if (!enableLeadonPattern && !disableLeadonPattern)
        {
            return;
        }

        var transitionUntil = DateTime.UtcNow.AddMilliseconds(_options.MultiStateTransitionDelayMs);
        foreach (var state in updates)
        {
            var type = state.Metadata.Type;
            var isStopbar = IsStopbarType(type);
            var isLeadon = IsLeadonType(type);
            if (!isStopbar && !isLeadon)
            {
                continue;
            }

            var shouldAnimate = enableLeadonPattern
                ? (isStopbar && !state.IsOn) || (isLeadon && state.IsOn)
                : (isStopbar && state.IsOn) || (isLeadon && !state.IsOn);
            if (!shouldAnimate)
            {
                continue;
            }

            _alternatingTransitions[state.Metadata.Id] = new AlternatingTransition(state.IsOn, transitionUntil);
            QueuePointSync(state.Metadata.Id);
            ScheduleTransitionFinalize(state.Metadata.Id, transitionUntil);
        }
    }

    public void Suspend()
    {
        _suspended = true;
        _logger.LogInformation("[Suspend] Distance controller paused; active={count}", _spawnedPoints.Count);
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        foreach (var kv in _serverStates)
        {
            QueuePointSync(kv.Key);
        }
        _logger.LogInformation("[Resume] Distance controller resumed; queued resync={count}", _serverStates.Count);
    }

    /// <summary>
    /// Toggles debug/test mode. When enabled, freezes network state updates and cycles
    /// the closest point through all possible light states (0-7, 20-27).
    /// </summary>
    public void ToggleDebugMode()
    {
        lock (_debugLock)
        {
            _debugMode = !_debugMode;

            if (_debugMode)
            {
                _logger.LogInformation("[DebugMode] Enabled - freezing network updates");
                _debugStateIndex = 0;
                _debugNextCycleUtc = DateTime.MinValue;
                _frozenStates.Clear();

                // Find the closest point to the player
                var flight = _simManager.LatestState;
                if (flight != null)
                {
                    _debugPointId = FindClosestPointId(flight.Latitude, flight.Longitude);
                    _logger.LogInformation("[DebugMode] Closest point: {pointId}", _debugPointId ?? "(none)");
                }
                else
                {
                    _debugPointId = null;
                }

                RaiseDebugModeChanged();
            }
            else
            {
                _logger.LogInformation("[DebugMode] Disabled - restoring network state");

                // Apply all frozen states that accumulated during debug mode
                foreach (var kv in _frozenStates)
                {
                    _serverStates[kv.Key] = kv.Value;
                    IncrementVersion(kv.Key);
                    QueuePointSync(kv.Key);
                }
                _frozenStates.Clear();

                // Also re-sync the debug point to its proper state
                if (_debugPointId != null)
                {
                    IncrementVersion(_debugPointId);
                    QueuePointSync(_debugPointId);
                }

                _debugPointId = null;
                RaiseDebugModeChanged();
            }
        }
    }

    private string? FindClosestPointId(double lat, double lon)
    {
        string? closestId = null;
        double closestDistance = double.MaxValue;

        foreach (var kv in _serverStates)
        {
            var meta = kv.Value.Metadata;
            var dist = DistanceMeters(lat, lon, meta.Latitude, meta.Longitude);
            if (dist < closestDistance)
            {
                closestDistance = dist;
                closestId = kv.Key;
            }
        }

        return closestId;
    }

    private void RaiseDebugModeChanged()
    {
        var currentStateId = _debugMode && _debugStateIndex < DebugStateSequence.Length
            ? DebugStateSequence[_debugStateIndex]
            : 0;
        try
        {
            DebugModeChanged?.Invoke(this, new DebugModeChangedEventArgs(_debugMode, currentStateId, _debugPointId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DebugMode] Event handler error");
        }
    }

    private async Task ProcessDebugCycleAsync(CancellationToken ct)
    {
        if (!_debugMode || string.IsNullOrEmpty(_debugPointId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now < _debugNextCycleUtc)
        {
            return;
        }

        // Cycle to next state
        lock (_debugLock)
        {
            _debugStateIndex = (_debugStateIndex + 1) % DebugStateSequence.Length;
            _debugNextCycleUtc = now.AddMilliseconds(_options.DebugCycleIntervalMs);
        }

        var targetStateId = DebugStateSequence[_debugStateIndex];
        _logger.LogInformation("[DebugMode] Cycling point {pointId} to state {state} ({index}/{total})",
            _debugPointId, targetStateId, _debugStateIndex + 1, DebugStateSequence.Length);

        // Force respawn the debug point with the new state
        await ForceDebugSpawnAsync(_debugPointId, targetStateId, ct);

        RaiseDebugModeChanged();
    }

    private async Task ForceDebugSpawnAsync(string pointId, int stateId, CancellationToken ct)
    {
        if (!_serverStates.TryGetValue(pointId, out var state))
        {
            return;
        }

        var layouts = GetLayouts(pointId, state);
        if (layouts.Count == 0)
        {
            return;
        }

        var spawned = _spawnedPoints.GetOrAdd(pointId, id => new SpawnedPoint(id));

        for (int slot = 0; slot < layouts.Count; slot++)
        {
            var layout = layouts[slot];
            spawned.Lights.TryGetValue(slot, out var existing);

            // Remove existing light if present
            if (existing != null)
            {
                try
                {
                    await DespawnLightAsync(existing.Object, ct);
                    _objectIndex.TryRemove(existing.Object.ObjectId, out _);
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "[DebugMode] Failed to despawn existing light for slot {slot}", slot);
                }
            }

            // Spawn with the debug state
            var simObject = await SpawnLightAsync(pointId, layout, stateId, slot, ct);
            if (simObject != null)
            {
                var newLight = new SpawnedLight(simObject, stateId, slot);
                spawned.Lights[slot] = newLight;
                _objectIndex[simObject.ObjectId] = (pointId, slot);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MsfsPointController (distance-based) started radius={radius}m", RequestedVisibilityRadiusMeters);
        var workset = new HashSet<string>(StringComparer.Ordinal);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_suspended || _mapResetInProgress)
                {
                    await Task.Delay(_options.IdleDelayMs, stoppingToken);
                    continue;
                }

                if (_simManager.ActiveConnector != _connector || !_connector.IsConnected || !IsObjectManagerReady())
                {
                    await Task.Delay(_options.DisconnectedDelayMs, stoppingToken);
                    continue;
                }

                var flight = _simManager.LatestState;
                if (flight == null)
                {
                    await Task.Delay(_options.IdleDelayMs, stoppingToken);
                    continue;
                }

                // Handle debug mode cycling
                if (_debugMode)
                {
                    await ProcessDebugCycleAsync(stoppingToken);
                    await Task.Delay(_options.IdleDelayMs, stoppingToken);
                    continue;
                }

                var center = (Lat: flight.Latitude, Lon: flight.Longitude);
                var geoCenter = new GeoReference(center.Lat, center.Lon);
                RefreshVisibility(center, workset);

                QueueVisibilityRechecks(workset);

                if (Interlocked.Exchange(ref _queueOverflowed, 0) != 0)
                {
                    foreach (var pointId in _serverStates.Keys) workset.Add(pointId);
                }

                DrainPendingUpdates(workset);

                if (workset.Count == 0)
                {
                    await Task.Delay(_options.IdleDelayMs, stoppingToken);
                    continue;
                }

                OrderWorksetByDistance(workset, geoCenter, _orderedWorkset);
                foreach (var pointId in _orderedWorkset)
                {
                    if (RequestedVisibilityRadiusMeters != _visibilityRadiusMeters) break;
                    await SyncPointAsync(pointId, geoCenter, stoppingToken);
                }

                _orderedWorkset.Clear();
                if (RequestedVisibilityRadiusMeters == _visibilityRadiusMeters) workset.Clear();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MsfsPointController loop failure");
                try { await Task.Delay(_options.ErrorBackoffMs, stoppingToken); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private void DrainPendingUpdates(HashSet<string> workset)
    {
        while (_pendingUpdates.Reader.TryRead(out var id))
        {
            _queuedUpdates.TryRemove(id, out _);
            workset.Add(id);
        }
    }

    private void QueuePointSync(string pointId)
    {
        if (_suspended || _simManager.ActiveConnector != _connector) return;
        if (!_queuedUpdates.TryAdd(pointId, 0)) return;
        if (!_pendingUpdates.Writer.TryWrite(pointId))
        {
            _queuedUpdates.TryRemove(pointId, out _);
            Interlocked.Exchange(ref _queueOverflowed, 1);
        }
    }

    private long GetVersionToken(string pointId)
    {
        return _updateVersions.TryGetValue(pointId, out var version) ? version : 0;
    }

    private void IncrementVersion(string pointId)
    {
        _updateVersions.AddOrUpdate(pointId, 1, static (_, current) => current == long.MaxValue ? 1 : current + 1);
    }

    private bool AbortIfSuperseded(string pointId, long versionToken)
    {
        if (RequestedVisibilityRadiusMeters != _visibilityRadiusMeters)
        {
            QueuePointSync(pointId);
            return true;
        }

        if (!_updateVersions.TryGetValue(pointId, out var latest) || latest <= versionToken)
        {
            return false;
        }

        QueuePointSync(pointId);
        return true;
    }

    private void OrderWorksetByDistance(HashSet<string> workset, in GeoReference center, List<string> ordered)
    {
        if (workset.Count == 0)
        {
            return;
        }

        if (workset.Count == 1)
        {
            ordered.Add(workset.First());
            return;
        }

        var pool = ArrayPool<WorkItem>.Shared;
        var buffer = pool.Rent(workset.Count);
        var radius = _visibilityRadiusMeters;
        var length = 0;

        try
        {
            foreach (var id in workset)
            {
                var distance = DistanceForOrdering(id, center);
                var outside = distance >= radius && distance < double.MaxValue;
                var hasSpawned = _spawnedPoints.ContainsKey(id);
                buffer[length++] = new WorkItem(id, distance, outside, hasSpawned);
            }

            Array.Sort(buffer, 0, length, WorkItemComparer.Instance);

            for (var i = 0; i < length; i++)
            {
                ordered.Add(buffer[i].Id);
            }
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private double DistanceForOrdering(string pointId, in GeoReference center)
    {
        if (_serverStates.TryGetValue(pointId, out var state))
        {
            var dist = DistanceMeters(center, state.Metadata.Latitude, state.Metadata.Longitude);
            if (double.IsNaN(dist) || double.IsInfinity(dist))
            {
                return double.MaxValue;
            }

            return dist;
        }

        return double.MaxValue;
    }

    private void RefreshVisibility((double Lat, double Lon) center, HashSet<string> workset)
    {
        var requestedRadius = RequestedVisibilityRadiusMeters;
        var distanceChanged = requestedRadius != _visibilityRadiusMeters;
        _visibilityRadiusMeters = requestedRadius;
        if (ShouldReevaluateVisibility(center) || distanceChanged)
        {
            ApplyVisibilityChanges(new GeoReference(center.Lat, center.Lon), workset, distanceChanged);
        }
    }

    private bool ShouldReevaluateVisibility((double Lat, double Lon) center)
    {
        if (!_lastCenter.HasValue)
        {
            _lastCenter = center;
            return true;
        }

        var moved = DistanceMeters(_lastCenter.Value.Lat, _lastCenter.Value.Lon, center.Lat, center.Lon);
        if (moved >= _options.CenterRecalcThresholdMeters)
        {
            _lastCenter = center;
            return true;
        }

        return false;
    }

    private void ApplyVisibilityChanges(in GeoReference center, HashSet<string> workset, bool distanceChanged = false)
    {
        var entryRadius = _visibilityRadiusMeters;
        // A settings change uses the exact new boundary; movement keeps its existing hysteresis.
        var exitRadius = entryRadius + (distanceChanged ? 0 : _options.VisibilityHysteresisMeters);
        if (distanceChanged)
        {
            foreach (var pointId in _spawnedPoints.Keys) workset.Add(pointId);
        }
        var staleIds = _staleVisibilityIds;
        staleIds.Clear();

        lock (_visibilityLock)
        {
            foreach (var kv in _serverStates)
            {
                var dist = DistanceMeters(center, kv.Value.Metadata.Latitude, kv.Value.Metadata.Longitude);
                if (dist <= entryRadius)
                {
                    if (_visiblePointIds.Add(kv.Key))
                    {
                        workset.Add(kv.Key);
                    }
                }
                else if (_visiblePointIds.Contains(kv.Key) && dist > exitRadius)
                {
                    _visiblePointIds.Remove(kv.Key);
                    workset.Add(kv.Key);
                }
            }

            foreach (var id in _visiblePointIds)
            {
                if (!_serverStates.ContainsKey(id))
                {
                    staleIds.Add(id);
                }
            }

            if (staleIds.Count > 0)
            {
                foreach (var staleId in staleIds)
                {
                    _visiblePointIds.Remove(staleId);
                    workset.Add(staleId);
                }
            }
        }

        staleIds.Clear();
    }

    private void QueueVisibilityRechecks(HashSet<string> workset)
    {
        if (_options.VisibilitySweepIntervalMs <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now < _nextVisibilitySweepUtc)
        {
            return;
        }

        List<string>? snapshot = null;
        lock (_visibilityLock)
        {
            if (_visiblePointIds.Count > 0)
            {
                snapshot = _visibilitySnapshot;
                snapshot.Clear();
                snapshot.AddRange(_visiblePointIds);
            }
        }

        if (snapshot != null)
        {
            foreach (var id in snapshot)
            {
                workset.Add(id);
            }

            snapshot.Clear();
        }

        _nextVisibilitySweepUtc = now.AddMilliseconds(_options.VisibilitySweepIntervalMs);
    }

    private async Task SyncPointAsync(string pointId, GeoReference center, CancellationToken ct)
    {
        var versionToken = GetVersionToken(pointId);

        if (!_serverStates.TryGetValue(pointId, out var state))
        {
            if (AbortIfSuperseded(pointId, versionToken))
            {
                return;
            }
            await DespawnPointAsync(pointId, ct);
            return;
        }

        if (AbortIfSuperseded(pointId, versionToken))
        {
            return;
        }

        var insideRadius = IsWithinRadius(pointId, state, center);
        lock (_visibilityLock)
        {
            if (insideRadius) _visiblePointIds.Add(pointId);
            else _visiblePointIds.Remove(pointId);
        }

        if (AbortIfSuperseded(pointId, versionToken))
        {
            return;
        }

        if (!insideRadius)
        {
            if (AbortIfSuperseded(pointId, versionToken))
            {
                return;
            }
            await DespawnPointAsync(pointId, ct);
            return;
        }

        var layouts = GetLayouts(pointId, state);
        if (layouts.Count == 0)
        {
            if (AbortIfSuperseded(pointId, versionToken))
            {
                return;
            }
            await DespawnPointAsync(pointId, ct);
            return;
        }

        var spawned = _spawnedPoints.GetOrAdd(pointId, id => new SpawnedPoint(id));

        var spawnQueue = _spawnQueue;
        spawnQueue.Clear();

        for (int slot = 0; slot < layouts.Count; slot++)
        {
            if (AbortIfSuperseded(pointId, versionToken))
            {
                spawnQueue.Clear();
                return;
            }

            var layout = layouts[slot];
            var desiredState = ResolveStateId(layout, ResolveAlternatingTargetState(pointId, state.IsOn, slot));
            spawned.Lights.TryGetValue(slot, out var existing);

            if (existing != null && existing.StateId == desiredState && existing.Object.IsActive)
            {
                continue;
            }

            spawnQueue.Add(new SpawnRequest(layout, slot, desiredState, existing));
        }

        if (spawnQueue.Count > 0)
        {
            var updated = await SpawnBatchWithOverlapAsync(pointId, versionToken, spawnQueue, spawned, ct);
            spawnQueue.Clear();

            if (!updated)
            {
                return;
            }

            if (AbortIfSuperseded(pointId, versionToken))
            {
                return;
            }
        }
        else
        {
            spawnQueue.Clear();
        }

        foreach (var extra in spawned.Lights.Keys.Where(k => k >= layouts.Count).ToList())
        {
            if (spawned.Lights.TryRemove(extra, out var light))
            {
                await RemoveLightAsync(pointId, extra, light, ct);

                if (AbortIfSuperseded(pointId, versionToken))
                {
                    return;
                }
            }
        }

        if (AbortIfSuperseded(pointId, versionToken))
        {
            return;
        }
    }

    private IReadOnlyList<AirportStateHub.LightLayout> GetLayouts(string pointId, PointState state)
    {
        return _layoutCache.GetOrAdd(pointId, _ =>
        {
            if (_hub.TryGetLightLayout(pointId, out var layouts) && layouts.Count > 0)
            {
                return layouts.ToList();
            }

            return new List<AirportStateHub.LightLayout>
            {
                new(state.Metadata.Latitude, state.Metadata.Longitude, null, state.Metadata.Color, null, null)
            };
        });
    }

    private static int ResolveStateId(AirportStateHub.LightLayout layout, bool isOn)
    {
        if (isOn)
        {
            if (layout.StateId.HasValue && layout.StateId.Value > 0)
            {
                return layout.StateId.Value;
            }

            return 1;
        }

        if (layout.OffStateId.HasValue)
        {
            return layout.OffStateId.Value;
        }

        // Keep the elevated fixture visible when the map omits its off state.
        return layout.StateId is 6 or 7 ? 7 : 0;
    }

    private bool ResolveAlternatingTargetState(string pointId, bool finalIsOn, int slotIndex)
    {
        if (!_alternatingTransitions.TryGetValue(pointId, out var transition))
        {
            return finalIsOn;
        }

        if (DateTime.UtcNow >= transition.PhaseTwoUtc)
        {
            _alternatingTransitions.TryRemove(pointId, out _);
            return finalIsOn;
        }

        var firstHalfEnabled = (slotIndex & 1) == 0;
        return transition.FinalIsOn ? firstHalfEnabled : !firstHalfEnabled;
    }

    private void ScheduleTransitionFinalize(string pointId, DateTime phaseTwoUtc)
    {
        var mapGeneration = Volatile.Read(ref _mapGeneration);
        _ = Task.Run(async () =>
        {
            var delay = phaseTwoUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay).ConfigureAwait(false); }
                catch { return; }
            }

            if (mapGeneration == Volatile.Read(ref _mapGeneration) &&
                _alternatingTransitions.TryGetValue(pointId, out var transition) &&
                transition.PhaseTwoUtc <= DateTime.UtcNow)
            {
                QueuePointSync(pointId);
            }
        });
    }

    private async Task RemoveLightAsync(string pointId, int slot, SpawnedLight light, CancellationToken ct)
    {
        try
        {
            await DespawnLightAsync(light.Object, ct);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "[DespawnFail] point={id} slot={slot}", pointId, slot);
        }
        finally
        {
            _objectIndex.TryRemove(light.Object.ObjectId, out _);
        }
    }

    private async Task DespawnPointAsync(string pointId, CancellationToken ct)
    {
        await _spawnSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_spawnedPoints.TryRemove(pointId, out var spawned))
            {
                lock (_visibilityLock)
                {
                    _visiblePointIds.Remove(pointId);
                }
                return;
            }

            foreach (var kv in spawned.Lights)
            {
                await RemoveLightAsync(pointId, kv.Key, kv.Value, ct).ConfigureAwait(false);
            }

            lock (_visibilityLock)
            {
                _visiblePointIds.Remove(pointId);
            }
        }
        finally
        {
            _spawnSemaphore.Release();
        }
    }

    private async Task<SimObject?> SpawnLightAsync(string pointId,
                                                   AirportStateHub.LightLayout layout,
                                                   int desiredStateId,
                                                   int slotIndex,
                                                   CancellationToken ct)
    {
        var manager = GetManager();
        if (manager == null)
        {
            return null;
        }

        if (layout == null)
        {
            return null;
        }

        if (_visibilityRadiusMeters <= 0)
        {
            return null;
        }

        await WaitForSpawnSlotAsync(ct);
        await SimConnectRequestLimiter.WaitAsync(1, ct).ConfigureAwait(false);

        await _spawnSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tag = $"{pointId}|{slotIndex}";
            return await manager.CreateObjectAsync(ResolveModel(desiredStateId), new SimConnect.NET.SimConnectDataInitPosition
            {
                Latitude = layout.Latitude,
                Longitude = layout.Longitude,
                Altitude = _options.SpawnAltitudeFeet,
                Heading = layout.Heading ?? 0,
                Pitch = 0,
                Bank = 0,
                OnGround = 1,
                Airspeed = 0
            }, tag, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref _cachedManager, null);
            _logger.LogDebug("[SpawnFail] point={id} slot={slot} state={state} - SimConnect disposed, clearing cache", pointId, slotIndex, desiredStateId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SpawnFail] point={id} slot={slot} state={state}", pointId, slotIndex, desiredStateId);
            return null;
        }
        finally
        {
            _spawnSemaphore.Release();
        }
    }

    private async Task DespawnLightAsync(SimObject simObject, CancellationToken ct)
    {
        var manager = GetManager();
        if (manager == null)
        {
            return;
        }

        try
        {
            await SimConnectRequestLimiter.WaitAsync(1, ct).ConfigureAwait(false);
            await manager.RemoveObjectAsync(simObject, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref _cachedManager, null);
            _logger.LogDebug("[DespawnFail] objectId={id} - SimConnect disposed, clearing cache", simObject.ObjectId);
        }
    }

    private async Task WaitForSpawnSlotAsync(CancellationToken ct)
    {
        if (_perSpawnInterval <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan delay;
        lock (_rateLock)
        {
            var now = DateTime.UtcNow;
            if (_nextSpawnUtc < now)
            {
                _nextSpawnUtc = now;
            }
            delay = _nextSpawnUtc - now;
            _nextSpawnUtc = _nextSpawnUtc + _perSpawnInterval;
        }

        if (delay > TimeSpan.Zero)
        {
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { }
        }
    }

    private bool IsWithinRadius(string pointId, PointState state, GeoReference center)
    {
        var dist = DistanceMeters(center, state.Metadata.Latitude, state.Metadata.Longitude);
        if (dist <= _visibilityRadiusMeters)
        {
            return true;
        }

        var exitRadius = _visibilityRadiusMeters + _options.VisibilityHysteresisMeters;
        lock (_visibilityLock)
        {
            if (_visiblePointIds.Contains(pointId) && dist <= exitRadius)
            {
                return true;
            }
        }

        return false;
    }

    private readonly struct GeoReference
    {
        public GeoReference(double lat, double lon)
        {
            Lat = lat;
            Lon = lon;
            LatitudeRadians = DegreesToRadians(lat);
            LongitudeRadians = DegreesToRadians(lon);
            SinLatitude = Math.Sin(LatitudeRadians);
            CosLatitude = Math.Cos(LatitudeRadians);
        }

        public double Lat { get; }
        public double Lon { get; }
        public double LatitudeRadians { get; }
        public double LongitudeRadians { get; }
        public double SinLatitude { get; }
        public double CosLatitude { get; }
    }

    private static double DistanceMeters(in GeoReference origin, double lat2, double lon2)
    {
        const double R = 6371000;
        double lat2Rad = DegreesToRadians(lat2);
        double dLat = lat2Rad - origin.LatitudeRadians;
        double lon2Rad = DegreesToRadians(lon2);
        double dLon = lon2Rad - origin.LongitudeRadians;
        double sinLat = Math.Sin(dLat / 2);
        double sinLon = Math.Sin(dLon / 2);
        double a = sinLat * sinLat + origin.CosLatitude * Math.Cos(lat2Rad) * sinLon * sinLon;
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        double dLat = DegreesToRadians(lat2 - lat1);
        double dLon = DegreesToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double DegreesToRadians(double deg) => deg * Math.PI / 180.0;

    private async void OnMapLoaded(string airport)
    {
        var mapGeneration = Interlocked.Increment(ref _mapGeneration);
        _mapResetInProgress = true;
        try
        {
            _currentAirportIcao = airport?.Trim().ToUpperInvariant();
            _layoutCache.Clear();
            lock (_visibilityLock)
            {
                _visiblePointIds.Clear();
            }
            _serverStates.Clear();
            _alternatingTransitions.Clear();
            _updateVersions.Clear();
            _queuedUpdates.Clear();
            while (_pendingUpdates.Reader.TryRead(out _)) { }
            _nextVisibilitySweepUtc = DateTime.MinValue;
            Volatile.Write(ref _cachedManager, null);
            await DespawnAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MapReload] Failed to reset controller for {apt}", airport);
        }
        finally
        {
            if (mapGeneration == Volatile.Read(ref _mapGeneration))
            {
                _mapResetInProgress = false;
            }
        }
    }

    private void OnTestingModeChanged(AirportStateHub.TestingModeChangedEventArgs e)
    {
        if (!e.IsTestingMode)
        {
            return;
        }

        _logger.LogInformation("[TestingMode] Enabled - queued loaded points for distance-based sync");
        foreach (var pointId in _serverStates.Keys)
        {
            QueuePointSync(pointId);
        }
    }

    public async Task DespawnAllAsync(CancellationToken ct = default)
    {
        foreach (var pointId in _spawnedPoints.Keys.ToList())
        {
            await DespawnPointAsync(pointId, ct);
        }
    }

    private bool IsObjectManagerReady()
    {
        var manager = GetManager();
        return manager != null && _connector.IsConnected;
    }

    private SimObjectManager? GetManager()
    {
        if (!_connector.IsConnected)
        {
            Volatile.Write(ref _cachedManager, null);
            return null;
        }

        var cached = Volatile.Read(ref _cachedManager);

        if (_connector is not MsfsSimulatorConnector msfs)
        {
            return null;
        }

        var client = MsfsConnectorClientField?.GetValue(msfs) as SimConnect.NET.SimConnectClient;
        var currentManager = client?.AIObjects;

        if (cached != null && currentManager != cached)
        {
            Volatile.Write(ref _cachedManager, null);
            cached = null;
        }

        if (cached != null)
        {
            return cached;
        }

        if (currentManager != null)
        {
            Volatile.Write(ref _cachedManager, currentManager);
        }

        return currentManager;
    }

    private static string ResolveModel(int stateId)
    {
        if (stateId < 0) stateId = 0;
        return $"BARS_Light_{stateId}";
    }

    private static bool IsStopbarType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        return type.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0 &&
               type.IndexOf("BAR", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsLeadonType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        return type.IndexOf("LEAD", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private readonly struct SpawnRequest
    {
        public SpawnRequest(AirportStateHub.LightLayout layout, int slotIndex, int desiredStateId, SpawnedLight? existing)
        {
            Layout = layout;
            SlotIndex = slotIndex;
            DesiredStateId = desiredStateId;
            Existing = existing;
        }

        public AirportStateHub.LightLayout Layout { get; }
        public int SlotIndex { get; }
        public int DesiredStateId { get; }
        public SpawnedLight? Existing { get; }
    }

    private async Task<bool> SpawnBatchWithOverlapAsync(string pointId,
                                                        long versionToken,
                                                        List<SpawnRequest> spawnQueue,
                                                        SpawnedPoint spawned,
                                                        CancellationToken ct)
    {
        var pendingAdds = new List<SpawnedLight>(spawnQueue.Count);
        var stagedRemovals = new List<SpawnedLight>(spawnQueue.Count);

        try
        {
            foreach (var request in spawnQueue)
            {
                if (AbortIfSuperseded(pointId, versionToken))
                {
                    return await RollbackPendingAsync(pointId, pendingAdds, ct);
                }

                var simObject = await SpawnLightAsync(pointId, request.Layout, request.DesiredStateId, request.SlotIndex, ct);
                if (simObject == null)
                {
                    return await RollbackPendingAsync(pointId, pendingAdds, ct);
                }

                pendingAdds.Add(new SpawnedLight(simObject, request.DesiredStateId, request.SlotIndex));
                if (request.Existing != null)
                {
                    stagedRemovals.Add(request.Existing);
                }
            }

            if (AbortIfSuperseded(pointId, versionToken))
            {
                return await RollbackPendingAsync(pointId, pendingAdds, ct);
            }

            foreach (var newLight in pendingAdds)
            {
                spawned.Lights[newLight.SlotIndex] = newLight;
                _objectIndex[newLight.Object.ObjectId] = (pointId, newLight.SlotIndex);
            }

            foreach (var oldLight in stagedRemovals)
            {
                await RemoveLightAsync(pointId, oldLight.SlotIndex, oldLight, ct);
            }

            return true;
        }
        finally
        {
            pendingAdds.Clear();
            stagedRemovals.Clear();
        }
    }

    private async Task<bool> RollbackPendingAsync(string pointId, List<SpawnedLight> pendingAdds, CancellationToken ct)
    {
        foreach (var pending in pendingAdds)
        {
            try
            {
                await DespawnLightAsync(pending.Object, ct);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "[SpawnRollbackFail] point={id} slot={slot}", pointId, pending.SlotIndex);
            }
        }

        if (pendingAdds.Count > 0)
        {
            QueuePointSync(pointId);
        }

        return false;
    }

    private readonly struct WorkItem
    {
        public WorkItem(string id, double distance, bool outside, bool hasSpawned)
        {
            Id = id;
            Distance = distance;
            Outside = outside;
            HasSpawned = hasSpawned;
        }

        public string Id { get; }
        public double Distance { get; }
        public bool Outside { get; }
        public bool HasSpawned { get; }
    }

    private sealed class WorkItemComparer : IComparer<WorkItem>
    {
        public static readonly WorkItemComparer Instance = new();

        public int Compare(WorkItem x, WorkItem y)
        {
            var xPriority = x.Outside ? (x.HasSpawned ? 2 : 3) : (x.HasSpawned ? 0 : 1);
            var yPriority = y.Outside ? (y.HasSpawned ? 2 : 3) : (y.HasSpawned ? 0 : 1);

            if (xPriority != yPriority)
            {
                return xPriority.CompareTo(yPriority);
            }

            return x.Distance.CompareTo(y.Distance);
        }
    }

    private sealed class SpawnedPoint
    {
        public SpawnedPoint(string pointId) => PointId = pointId;

        public string PointId { get; }
        public ConcurrentDictionary<int, SpawnedLight> Lights { get; } = new();
    }

    private sealed class SpawnedLight
    {
        public SpawnedLight(SimObject obj, int stateId, int slot)
        {
            Object = obj;
            StateId = stateId;
            SlotIndex = slot;
        }

        public SimObject Object { get; }
        public int StateId { get; }
        public int SlotIndex { get; }
    }

    private readonly record struct AlternatingTransition(bool FinalIsOn, DateTime PhaseTwoUtc);
}

public sealed class MsfsPointControllerOptions
{
    public double VisibilityRadiusMeters { get; init; } = 500;
    public double VisibilityHysteresisMeters { get; init; } = 200;
    public double CenterRecalcThresholdMeters { get; init; } = 50;
    public int VisibilitySweepIntervalMs { get; init; } = 200;
    public int SpawnRatePerSecond { get; init; } = 0;
    public int IdleDelayMs { get; init; } = 50;
    public int DisconnectedDelayMs { get; init; } = 500;
    public int ErrorBackoffMs { get; init; } = 250;
    public double SpawnAltitudeFeet { get; init; } = 0;
    public int MultiStateTransitionDelayMs { get; init; } = 1000;
    /// <summary>
    /// Interval in milliseconds between debug mode state cycles. Default 1 second.
    /// </summary>
    public int DebugCycleIntervalMs { get; init; } = 1000;
}
