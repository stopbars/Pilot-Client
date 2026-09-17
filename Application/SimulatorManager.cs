using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Services;
using BARS_Client_V2.Infrastructure.Simulators.Msfs;
using BARS_Client_V2.Infrastructure.Simulators.XPlane;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BARS_Client_V2.Application;

public sealed class SimulatorManager : BackgroundService
{
    private readonly IEnumerable<ISimulatorConnector> _connectors;
    private readonly ILogger<SimulatorManager> _logger;
    private readonly object _lock = new();
    private ISimulatorConnector? _active;
    private FlightState? _latest;

    public SimulatorManager(IEnumerable<ISimulatorConnector> connectors, ILogger<SimulatorManager> logger)
    {
        _connectors = connectors;
        _logger = logger;
    }

    public FlightState? LatestState { get { lock (_lock) return _latest; } }
    public ISimulatorConnector? ActiveConnector { get { lock (_lock) return _active; } }

    public async Task<bool> ActivateAsync(string simulatorId, CancellationToken ct = default)
    {
        var connector = _connectors.FirstOrDefault(c => string.Equals(c.SimulatorId, simulatorId, StringComparison.OrdinalIgnoreCase));
        if (connector == null) return false;
        if (connector == _active && connector.IsConnected) return true;

        if (_active != null)
        {
            try { await _active.DisconnectAsync(ct); } catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting previous simulator"); }
            lock (_lock) _active = null;
        }

        ClearLatestState();

        if (await connector.ConnectAsync(ct))
        {
            lock (_lock) _active = connector;
            _logger.LogInformation("Activated simulator {sim}", connector.DisplayName);

            // Update SceneryService with the detected simulator version
            UpdateCurrentSimulator(connector);

            return true;
        }
        return false;
    }

    /// <summary>
    /// Updates the SceneryService.CurrentSimulator based on the connected simulator.
    /// </summary>
    private void UpdateCurrentSimulator(ISimulatorConnector connector)
    {
        try
        {
            if (connector is MsfsSimulatorConnector msfsConnector)
            {
                var is2024 = msfsConnector.IsMsfs2024;
                if (is2024 == true)
                {
                    SceneryService.Instance.CurrentSimulator = "msfs2024";
                    _logger.LogInformation("Detected MSFS 2024 - setting CurrentSimulator to msfs2024");
                }
                else if (is2024 == false)
                {
                    SceneryService.Instance.CurrentSimulator = "msfs2020";
                    _logger.LogInformation("Detected MSFS 2020 - setting CurrentSimulator to msfs2020");
                }
                else
                {
                    // Unknown - default to 2020
                    SceneryService.Instance.CurrentSimulator = "msfs2020";
                    _logger.LogInformation("MSFS version unknown - defaulting CurrentSimulator to msfs2020");
                }
            }
            else
            {
                SceneryService.Instance.CurrentSimulator = connector is XPlaneSimulatorConnector
                    ? "xplane"
                    : "msfs2020";
                _logger.LogInformation("Detected simulator {simulatorId}", SceneryService.Instance.CurrentSimulator);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update CurrentSimulator");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var active = ActiveConnector;
            if (active == null || !active.IsConnected)
            {
                ClearLatestState();
                if (!await TryActivateAvailableAsync(stoppingToken))
                {
                    await Task.Delay(2000, stoppingToken);
                }
                continue;
            }
            try
            {
                await foreach (var raw in active.StreamRawAsync(stoppingToken))
                {
                    lock (_lock) _latest = new FlightState(raw.Latitude, raw.Longitude, raw.OnGround, raw.HeadingDeg);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming flight state");
                // small backoff
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task<bool> TryActivateAvailableAsync(CancellationToken ct)
    {
        var candidates = _connectors
            .Where(IsSimulatorProcessRunning)
            .OrderBy(c => c.SimulatorId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var connector in candidates)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                if (await ActivateAsync(connector.SimulatorId, attempt.Token))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("Simulator connection attempt timed out for {sim}", connector.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Simulator connection attempt failed for {sim}", connector.DisplayName);
            }
        }
        return false;
    }

    private static bool IsSimulatorProcessRunning(ISimulatorConnector connector)
    {
        try
        {
            if (connector is XPlaneSimulatorConnector)
            {
                return IsProcessRunning("X-Plane");
            }
            if (connector is MsfsSimulatorConnector)
            {
                return IsProcessRunning("FlightSimulator2024") ||
                       IsProcessRunning("FlightSimulator");
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private void ClearLatestState()
    {
        lock (_lock)
        {
            _latest = null;
        }
    }
}
