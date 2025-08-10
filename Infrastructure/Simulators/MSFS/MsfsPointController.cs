using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly Dictionary<string, object> _spawned = new(); // pointId -> placeholder handle
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly int _maxObjects;
    private readonly int _spawnPerSecond;
    private DateTime _nextSpawnWindow = DateTime.UtcNow;
    private int _spawnedThisWindow;

    // Stats
    private long _totalReceived;
    private long _totalSpawnAttempts;
    private long _totalSpawned;
    private long _totalDespawned;
    private long _totalDeferredRate;
    private long _totalSkippedCap;
    private DateTime _lastSummary = DateTime.UtcNow;

    public MsfsPointController(IEnumerable<ISimulatorConnector> connectors, ILogger<MsfsPointController> logger, MsfsPointControllerOptions? options = null)
    {
        // Select the MSFS connector (first one with SimulatorId == MSFS or just first)
        _connector = connectors.FirstOrDefault(c => string.Equals(c.SimulatorId, "MSFS", StringComparison.OrdinalIgnoreCase))
                      ?? connectors.First();
        _logger = logger;
        options ??= new MsfsPointControllerOptions();
        _maxObjects = options.MaxObjects;
        _spawnPerSecond = options.SpawnPerSecond;
    }

    public void OnPointStateChanged(PointState state)
    {
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
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
                if (_queue.TryDequeue(out var ps))
                {
                    _logger.LogTrace("Dequeued {id} on={on}", ps.Metadata.Id, ps.IsOn);
                    await ProcessAsync(ps, stoppingToken);
                }
                else
                {
                    await Task.Delay(50, stoppingToken); // idle backoff
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
                await Task.Delay(500, stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(PointState ps, CancellationToken ct)
    {
        var id = ps.Metadata.Id;
        var exists = _spawned.ContainsKey(id);
        if (ps.IsOn)
        {
            if (!exists)
            {
                if (_spawned.Count >= _maxObjects)
                {
                    // Skip spawning to respect cap
                    Interlocked.Increment(ref _totalSkippedCap);
                    _logger.LogWarning("[SpawnSkip:Cap] {id} cap={cap} active={active}", id, _maxObjects, _spawned.Count);
                    return;
                }
                if (!CanSpawnNow())
                {
                    // requeue later to respect rate limit
                    Interlocked.Increment(ref _totalDeferredRate);
                    _logger.LogDebug("[SpawnDefer:Rate] {id} windowRemaining={rem}", id, Math.Max(0, _spawnPerSecond - _spawnedThisWindow));
                    _queue.Enqueue(ps);
                    return;
                }
                var handle = await SpawnStubAsync(ps, ct);
                if (handle != null)
                {
                    _spawned[id] = handle;
                    Interlocked.Increment(ref _totalSpawned);
                    _logger.LogInformation("[Spawn] {id} name={name} type={type} active={count}", id, ps.Metadata.Name, ps.Metadata.Type, _spawned.Count);
                }
            }
            else
            {
                // Future: update existing object light state (stub)
                _logger.LogDebug("[Update] {id} state={state}", id, ps.IsOn);
            }
        }
        else
        {
            if (exists)
            {
                if (_spawned.TryGetValue(id, out var handle))
                {
                    await DespawnStubAsync(handle, ct);
                    _spawned.Remove(id);
                    Interlocked.Increment(ref _totalDespawned);
                    _logger.LogInformation("[Despawn] {id} remaining={count}", id, _spawned.Count);
                }
            }
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

    private Task<object?> SpawnStubAsync(PointState ps, CancellationToken ct)
    {
        // Placeholder: integrate SimConnect AICreateSimulatedObject with custom SimObject title.
        _logger.LogTrace("Spawn stub for {id} at {lat}/{lon} state={state}", ps.Metadata.Id, ps.Metadata.Latitude, ps.Metadata.Longitude, ps.IsOn);
        return Task.FromResult<object?>(new { id = ps.Metadata.Id });
    }

    private Task DespawnStubAsync(object handle, CancellationToken ct)
    {
        // Placeholder: integrate SimConnect AIDeleteObject.
        _logger.LogTrace("Despawn stub for {handle}", handle);
        return Task.CompletedTask;
    }
}

internal sealed class MsfsPointControllerOptions
{
    public int MaxObjects { get; init; } = 1000;
    public int SpawnPerSecond { get; init; } = 15;
}
