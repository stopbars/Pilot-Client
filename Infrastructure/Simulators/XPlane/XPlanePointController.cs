using System.Collections.Concurrent;
using System.Threading.Channels;
using BARS_Client_V2.Application;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Infrastructure.Networking;
using BARS_Client_V2.Infrastructure.Simulators.Msfs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

public sealed class XPlanePointController : BackgroundService, IPointStateListener
{
    private readonly LightDrawDistanceSettings _drawDistance;
    private int _appliedDrawDistanceMeters;
    private const double VisibilityRecenterMeters = 50.0;
    private readonly XPlaneSimulatorConnector _connector;
    private readonly SimulatorManager _simulatorManager;
    private readonly AirportStateHub _hub;
    private readonly ILogger<XPlanePointController> _logger;
    private readonly ConcurrentDictionary<string, PointState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<AirportStateHub.LightLayout>> _layouts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queuedPoints = new(StringComparer.Ordinal);
    private readonly Channel<string> _pendingPoints = Channel.CreateBounded<string>(
        new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly ConcurrentDictionary<string, List<XPlaneBridgeLight>> _activeByPoint = new(StringComparer.Ordinal);
    private volatile bool _snapshotRequired = true;
    private volatile string _airport = string.Empty;
    private (double Latitude, double Longitude)? _lastCenter;
    private int _lastConnectionGeneration;
    private long _sceneGeneration;
    private long _mapGeneration;
    private static readonly int[] DebugStates = [0, 1, 2, 3, 4, 5, 6, 7, 20, 21, 22, 23, 24, 25, 26, 27];
    private readonly object _debugGate = new();
    private readonly Dictionary<string, PointState> _frozenStates = new(StringComparer.Ordinal);
    private volatile bool _debugMode;
    private string? _debugPointId;
    private int _debugStateIndex;
    private DateTime _debugNextCycleUtc;

    public bool IsDebugMode => _debugMode;
    public event EventHandler<DebugModeChangedEventArgs>? DebugModeChanged;

    public XPlanePointController(
        XPlaneSimulatorConnector connector,
        SimulatorManager simulatorManager,
        AirportStateHub hub,
        ILogger<XPlanePointController> logger,
        LightDrawDistanceSettings? drawDistance = null)
    {
        _connector = connector;
        _simulatorManager = simulatorManager;
        _hub = hub;
        _logger = logger;
        _drawDistance = drawDistance ?? new LightDrawDistanceSettings();
        _hub.PointStateChanged += OnPointStateChanged;
        _hub.MapLoaded += OnMapLoaded;
        _hub.TestingModeChanged += OnTestingModeChanged;
    }

    public void OnPointStateChanged(PointState state)
    {
        lock (_debugGate)
        {
            if (_debugMode)
            {
                _frozenStates[state.Metadata.Id] = state;
                return;
            }
            _states[state.Metadata.Id] = state;
            _layouts.TryRemove(state.Metadata.Id, out _);
        }
        if (_simulatorManager.ActiveConnector != _connector)
        {
            _snapshotRequired = true;
            return;
        }
        QueuePoint(state.Metadata.Id);
    }

    public void ToggleDebugMode()
    {
        DebugModeChangedEventArgs change;
        lock (_debugGate)
        {
            if (_debugMode)
            {
                ResetDebugMode();
            }
            else
            {
                if (_hub.IsTestingMode || _simulatorManager.ActiveConnector != _connector || !_connector.IsConnected) return;
                _debugMode = true;
                _debugStateIndex = 0;
                _debugPointId = FindDebugPoint();
                _debugNextCycleUtc = DateTime.UtcNow.AddSeconds(1);
                Interlocked.Increment(ref _mapGeneration);
                _snapshotRequired = true;
                _logger.LogInformation("[XPlaneDebug] Enabled; point={point}; state=0", _debugPointId ?? "(waiting for nearby lights)");
            }
            change = DebugChange();
        }
        RaiseDebugModeChanged(change);
    }

    private string? FindDebugPoint()
    {
        var flight = _simulatorManager.LatestState;
        if (flight == null) return null;
        return _states.Values.Select(state => new
            {
                state.Metadata.Id,
                Distance = DistanceMeters(flight.Latitude, flight.Longitude, state.Metadata.Latitude, state.Metadata.Longitude)
            })
            .Where(point => point.Distance <= _drawDistance.Meters)
            .OrderBy(point => point.Distance).Select(point => point.Id).FirstOrDefault();
    }

    private void ProcessDebugCycle(DateTime now)
    {
        DebugModeChangedEventArgs change;
        lock (_debugGate)
        {
            if (!_debugMode) return;
            if (_debugPointId == null)
            {
                ApplyFrozenStates();
                _debugPointId = FindDebugPoint();
                if (_debugPointId == null) return;
                _debugNextCycleUtc = now.AddSeconds(1);
                Interlocked.Increment(ref _mapGeneration);
                _snapshotRequired = true;
            }
            else
            {
                if (now < _debugNextCycleUtc) return;
                _debugStateIndex = (_debugStateIndex + 1) % DebugStates.Length;
                _debugNextCycleUtc = now.AddSeconds(1);
                QueuePoint(_debugPointId);
                _logger.LogInformation("[XPlaneDebug] Cycling point={point}; state={state}", _debugPointId, DebugStates[_debugStateIndex]);
            }
            change = DebugChange();
        }
        RaiseDebugModeChanged(change);
    }

    private void StopDebugMode()
    {
        lock (_debugGate)
        {
            if (!_debugMode) return;
            ResetDebugMode();
        }
        RaiseDebugModeChanged(new(false, 0, null));
    }

    private void ResetDebugMode()
    {
        _debugMode = false;
        _debugPointId = null;
        ApplyFrozenStates();
        Interlocked.Increment(ref _mapGeneration);
        _snapshotRequired = true;
        _logger.LogInformation("[XPlaneDebug] Disabled; restoring latest network states");
    }

    private void ApplyFrozenStates()
    {
        foreach (var pair in _frozenStates)
        {
            _states[pair.Key] = pair.Value;
            _layouts.TryRemove(pair.Key, out _);
        }
        _frozenStates.Clear();
    }

    private DebugModeChangedEventArgs DebugChange() =>
        new(_debugMode, _debugMode ? DebugStates[_debugStateIndex] : 0, _debugPointId);

    private void RaiseDebugModeChanged(DebugModeChangedEventArgs change)
    {
        try { DebugModeChanged?.Invoke(this, change); }
        catch (Exception ex) { _logger.LogDebug(ex, "X-Plane debug event handler failed"); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "XPlanePointController started radius={radius}m recenter={recenter}m",
            _drawDistance.Meters,
            VisibilityRecenterMeters);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_simulatorManager.ActiveConnector != _connector || !_connector.IsConnected)
                {
                    StopDebugMode();
                    _snapshotRequired = true;
                    await Task.Delay(250, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var flight = _simulatorManager.LatestState;
                if (flight == null)
                {
                    await Task.Delay(100, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                ProcessDebugCycle(DateTime.UtcNow);

                if (_lastConnectionGeneration != _connector.ConnectionGeneration)
                {
                    _lastConnectionGeneration = _connector.ConnectionGeneration;
                    _snapshotRequired = true;
                }

                var center = (flight.Latitude, flight.Longitude);
                if (!_lastCenter.HasValue ||
                    DistanceMeters(_lastCenter.Value.Latitude, _lastCenter.Value.Longitude,
                        center.Latitude, center.Longitude) >= VisibilityRecenterMeters)
                {
                    _lastCenter = center;
                    _snapshotRequired = true;
                }

                var drawDistanceMeters = _drawDistance.Meters;
                if (_snapshotRequired || drawDistanceMeters != _appliedDrawDistanceMeters)
                {
                    var mapGeneration = Volatile.Read(ref _mapGeneration);
                    await ReplaceSnapshotAsync(center, drawDistanceMeters, stoppingToken).ConfigureAwait(false);
                    _appliedDrawDistanceMeters = drawDistanceMeters;
                    _snapshotRequired = mapGeneration != Volatile.Read(ref _mapGeneration);
                }
                else
                {
                    await PatchChangedPointsAsync(stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(50, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _snapshotRequired = true;
                _logger.LogDebug(ex, "X-Plane light synchronization failed; full snapshot will retry");
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReplaceSnapshotAsync(
        (double Latitude, double Longitude) center,
        int drawDistanceMeters,
        CancellationToken ct)
    {
        var (lights, byPoint) = BuildSnapshot(center, drawDistanceMeters);
        var generation = Interlocked.Increment(ref _sceneGeneration);
        await _connector.ReplaceSceneAsync(_airport, generation, lights, ct).ConfigureAwait(false);
        _activeByPoint.Clear();
        foreach (var pair in byPoint) _activeByPoint[pair.Key] = pair.Value;
        _logger.LogDebug(
            "Sent X-Plane scene generation={generation} airport={airport} lights={count}",
            generation,
            _airport,
            lights.Count);
    }

    private (List<XPlaneBridgeLight> Lights, Dictionary<string, List<XPlaneBridgeLight>> ByPoint) BuildSnapshot(
        (double Latitude, double Longitude) center, int drawDistanceMeters)
    {
        var lights = new List<XPlaneBridgeLight>();
        var byPoint = new Dictionary<string, List<XPlaneBridgeLight>>(StringComparer.Ordinal);
        foreach (var pair in _states)
        {
            var state = pair.Value;
            if (DistanceMeters(
                    center.Latitude,
                    center.Longitude,
                    state.Metadata.Latitude,
                    state.Metadata.Longitude) > drawDistanceMeters)
            {
                continue;
            }

            var pointLights = BuildLights(pair.Key, state);
            if (pointLights.Count == 0) continue;
            byPoint[pair.Key] = pointLights;
            lights.AddRange(pointLights);
        }

        return (lights, byPoint);
    }

    private async Task PatchChangedPointsAsync(CancellationToken ct)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        while (_pendingPoints.Reader.TryRead(out var pointId))
        {
            _queuedPoints.TryRemove(pointId, out _);
            changed.Add(pointId);
        }
        if (changed.Count == 0) return;

        var patches = new List<XPlaneBridgeLightPatch>();
        foreach (var pointId in changed)
        {
            if (!_activeByPoint.TryGetValue(pointId, out var active) ||
                !_states.TryGetValue(pointId, out var state))
            {
                _snapshotRequired = true;
                continue;
            }
            var layouts = GetLayouts(pointId, state);
            if (layouts.Count != active.Count)
            {
                _snapshotRequired = true;
                continue;
            }
            for (var slot = 0; slot < active.Count; slot++)
            {
                var stateId = ResolveDisplayedStateId(pointId, layouts[slot], state.IsOn);
                if (active[slot].StateId == stateId) continue;
                patches.Add(new XPlaneBridgeLightPatch(active[slot].Id, stateId));
                active[slot] = active[slot] with { StateId = stateId };
            }
        }
        if (patches.Count > 0)
        {
            await _connector.PatchLightsAsync(patches, ct).ConfigureAwait(false);
        }
    }

    private List<XPlaneBridgeLight> BuildLights(string pointId, PointState state)
    {
        var layouts = GetLayouts(pointId, state);
        var result = new List<XPlaneBridgeLight>(layouts.Count);
        for (var slot = 0; slot < layouts.Count; slot++)
        {
            var layout = layouts[slot];
            result.Add(new XPlaneBridgeLight(
                $"{pointId}|{slot}",
                layout.Latitude,
                layout.Longitude,
                layout.Heading ?? 0.0,
                ResolveDisplayedStateId(pointId, layout, state.IsOn)));
        }
        return result;
    }

    private IReadOnlyList<AirportStateHub.LightLayout> GetLayouts(string pointId, PointState state)
    {
        return _layouts.GetOrAdd(pointId, _ =>
        {
            if (_hub.TryGetLightLayout(pointId, out var layouts) && layouts.Count > 0)
            {
                return layouts.ToList();
            }
            return new[]
            {
                new AirportStateHub.LightLayout(
                    state.Metadata.Latitude,
                    state.Metadata.Longitude,
                    null,
                    state.Metadata.Color,
                    null,
                    null)
            };
        });
    }

    private static int ResolveStateId(AirportStateHub.LightLayout layout, bool isOn)
    {
        if (isOn) return layout.StateId is > 0 ? layout.StateId.Value : 1;
        // Keep the elevated fixture visible when the map omits its off state.
        return layout.OffStateId ?? (layout.StateId is 6 or 7 ? 7 : 0);
    }

    private int ResolveDisplayedStateId(string pointId, AirportStateHub.LightLayout layout, bool isOn)
    {
        lock (_debugGate)
            return _debugMode && pointId == _debugPointId ? DebugStates[_debugStateIndex] : ResolveStateId(layout, isOn);
    }

    private void OnTestingModeChanged(AirportStateHub.TestingModeChangedEventArgs mode)
    {
        if (mode.IsTestingMode) StopDebugMode();
    }

    private void OnMapLoaded(string airport)
    {
        bool stopped;
        lock (_debugGate)
        {
            stopped = _debugMode;
            if (stopped) ResetDebugMode();
            Interlocked.Increment(ref _mapGeneration);
            _airport = airport?.Trim().ToUpperInvariant() ?? string.Empty;
            _states.Clear();
            _layouts.Clear();
            _activeByPoint.Clear();
            _lastCenter = null;
            _snapshotRequired = true;
            DrainPendingPoints();
        }
        if (stopped) RaiseDebugModeChanged(new(false, 0, null));
    }

    private void DrainPendingPoints()
    {
        while (_pendingPoints.Reader.TryRead(out var pointId))
        {
            _queuedPoints.TryRemove(pointId, out _);
        }
    }

    private void QueuePoint(string pointId)
    {
        if (!_queuedPoints.TryAdd(pointId, 0)) return;
        if (!_pendingPoints.Writer.TryWrite(pointId))
        {
            _queuedPoints.TryRemove(pointId, out _);
            _snapshotRequired = true;
        }
    }

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6371000.0;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
