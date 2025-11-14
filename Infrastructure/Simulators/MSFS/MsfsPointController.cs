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
/// Distance-based point controller that keeps MSFS SimObjects in sync with the server state
/// within a visibility radius around the aircraft. Server state is the sole source of truth;
/// spawned state simply mirrors whichever objects are currently inside the visibility bubble.
/// </summary>
internal sealed class MsfsPointController : BackgroundService, IPointStateListener
{
    private readonly ILogger<MsfsPointController> _logger;
    private readonly AirportStateHub _hub;
    private readonly SimulatorManager _simManager;
    private readonly ISimulatorConnector _connector;
    private readonly MsfsPointControllerOptions _options;
    private static readonly FieldInfo? MsfsConnectorClientField = typeof(MsfsSimulatorConnector)
        .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly ConcurrentDictionary<string, PointState> _serverStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SpawnedPoint> _spawnedPoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<AirportStateHub.LightLayout>> _layoutCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _updateVersions = new(StringComparer.Ordinal);
    private readonly Channel<string> _pendingUpdates = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false
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

    private (double Lat, double Lon)? _lastCenter;
    private volatile bool _suspended;

    public MsfsPointController(IEnumerable<ISimulatorConnector> connectors,
                               ILogger<MsfsPointController> logger,
                               AirportStateHub hub,
                               SimulatorManager simManager,
                               MsfsPointControllerOptions? options = null)
    {
        _logger = logger;
        _hub = hub;
        _simManager = simManager;
        _options = options ?? new MsfsPointControllerOptions();
        _connector = connectors.FirstOrDefault(c => c.SimulatorId.Equals("MSFS", StringComparison.OrdinalIgnoreCase))
                     ?? connectors.First();

        _hub.PointStateChanged += OnPointStateChanged;
        _hub.MapLoaded += OnMapLoaded;

        _perSpawnInterval = _options.SpawnRatePerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1.0 / _options.SpawnRatePerSecond);
    }

    public void OnPointStateChanged(PointState state)
    {
        var pointId = state.Metadata.Id;
        _serverStates[pointId] = state;
        IncrementVersion(pointId);
        QueuePointSync(pointId);
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MsfsPointController (distance-based) started radius={radius}m", _options.VisibilityRadiusMeters);
        var workset = new HashSet<string>(StringComparer.Ordinal);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_suspended)
                {
                    await Task.Delay(_options.IdleDelayMs, stoppingToken);
                    continue;
                }

                if (!_connector.IsConnected || !IsObjectManagerReady())
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

                var center = (Lat: flight.Latitude, Lon: flight.Longitude);
                var geoCenter = new GeoReference(center.Lat, center.Lon);
                if (ShouldReevaluateVisibility(center))
                {
                    ApplyVisibilityChanges(geoCenter, workset);
                }

                QueueVisibilityRechecks(workset);

                DrainPendingUpdates(workset);

                if (workset.Count == 0)
                {
                    await Task.Delay(_options.IdleDelayMs, stoppingToken);
                    continue;
                }

                OrderWorksetByDistance(workset, geoCenter, _orderedWorkset);
                foreach (var pointId in _orderedWorkset)
                {
                    await SyncPointAsync(pointId, geoCenter, stoppingToken);
                }

                _orderedWorkset.Clear();
                workset.Clear();
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
            workset.Add(id);
        }
    }

    private void QueuePointSync(string pointId)
    {
        if (!_pendingUpdates.Writer.TryWrite(pointId))
        {
            _ = _pendingUpdates.Writer.WriteAsync(pointId);
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
        var radius = _options.VisibilityRadiusMeters;
        var length = 0;

        try
        {
            foreach (var id in workset)
            {
                var distance = DistanceForOrdering(id, center);
                var outside = distance >= radius && distance < double.MaxValue;
                buffer[length++] = new WorkItem(id, distance, outside);
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

    private void ApplyVisibilityChanges(in GeoReference center, HashSet<string> workset)
    {
        var entryRadius = _options.VisibilityRadiusMeters;
        var exitRadius = entryRadius + _options.VisibilityHysteresisMeters;
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

        for (int slot = 0; slot < layouts.Count; slot++)
        {
            if (AbortIfSuperseded(pointId, versionToken))
            {
                return;
            }

            var layout = layouts[slot];
            var desiredState = ResolveStateId(layout, state.IsOn);
            SpawnedLight? overlapLight = null;
            var overlapRemoval = false;
            if (spawned.Lights.TryGetValue(slot, out var existing))
            {
                if (existing.StateId == desiredState && existing.Object.IsActive)
                {
                    continue;
                }

                overlapRemoval = ShouldOverlapPlaceholder(existing.StateId, desiredState, layout, existing.Object.IsActive);
                if (overlapRemoval)
                {
                    overlapLight = existing;
                }
                else
                {
                    await RemoveLightAsync(pointId, slot, existing, ct);
                }

                if (AbortIfSuperseded(pointId, versionToken))
                {
                    return;
                }
            }

            var simObject = await SpawnLightAsync(pointId, layout, desiredState, slot, ct);
            if (simObject != null)
            {
                var light = new SpawnedLight(simObject, desiredState, slot);
                spawned.Lights[slot] = light;
                _objectIndex[simObject.ObjectId] = (pointId, slot);
                if (overlapRemoval && overlapLight != null)
                {
                    await RemoveLightAsync(pointId, slot, overlapLight, ct);
                }
            }

            if (AbortIfSuperseded(pointId, versionToken))
            {
                return;
            }
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

        return 0;
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

    private bool ShouldOverlapPlaceholder(int existingStateId, int desiredStateId, AirportStateHub.LightLayout layout, bool existingIsActive)
    {
        if (!existingIsActive)
        {
            return false;
        }

        var onState = ResolveStateId(layout, true);
        var offState = ResolveStateId(layout, false);

        if (onState == offState)
        {
            return false;
        }

        var goingOn = desiredStateId == onState && existingStateId == offState;
        var goingOff = desiredStateId == offState && existingStateId == onState;
        return goingOn || goingOff;
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

        if (_options.VisibilityRadiusMeters <= 0)
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

        await SimConnectRequestLimiter.WaitAsync(1, ct).ConfigureAwait(false);
        await manager.RemoveObjectAsync(simObject, ct).ConfigureAwait(false);
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
        if (dist <= _options.VisibilityRadiusMeters)
        {
            return true;
        }

        var exitRadius = _options.VisibilityRadiusMeters + _options.VisibilityHysteresisMeters;
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
        try
        {
            _layoutCache.Clear();
            lock (_visibilityLock)
            {
                _visiblePointIds.Clear();
            }
            _serverStates.Clear();
            _nextVisibilitySweepUtc = DateTime.MinValue;
            Volatile.Write(ref _cachedManager, null);
            await DespawnAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MapReload] Failed to reset controller for {apt}", airport);
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
        if (cached != null)
        {
            return cached;
        }

        if (_connector is not MsfsSimulatorConnector msfs)
        {
            return null;
        }

        var client = MsfsConnectorClientField?.GetValue(msfs) as SimConnect.NET.SimConnectClient;
        var manager = client?.AIObjects;
        if (manager != null)
        {
            Volatile.Write(ref _cachedManager, manager);
        }

        return manager;
    }

    private static string ResolveModel(int stateId)
    {
        if (stateId < 0) stateId = 0;
        return $"BARS_Light_{stateId}";
    }

    private readonly struct WorkItem
    {
        public WorkItem(string id, double distance, bool outside)
        {
            Id = id;
            Distance = distance;
            Outside = outside;
        }

        public string Id { get; }
        public double Distance { get; }
        public bool Outside { get; }
    }

    private sealed class WorkItemComparer : IComparer<WorkItem>
    {
        public static readonly WorkItemComparer Instance = new();

        public int Compare(WorkItem x, WorkItem y)
        {
            if (x.Outside != y.Outside)
            {
                return x.Outside ? -1 : 1;
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
}

internal sealed class MsfsPointControllerOptions
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
}
