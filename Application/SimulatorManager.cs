using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Services;
using BARS_Client_V2.Infrastructure.Simulators.Msfs;
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

        if (_active != null && _active.IsConnected)
        {
            try { await _active.DisconnectAsync(ct); } catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting previous simulator"); }
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
                // Non-MSFS simulators default to msfs2020 for now
                SceneryService.Instance.CurrentSimulator = "msfs2020";
                _logger.LogInformation("Non-MSFS simulator detected - defaulting CurrentSimulator to msfs2020");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update CurrentSimulator");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = _connectors.FirstOrDefault();
        if (first != null)
        {
            await ActivateAsync(first.SimulatorId, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var active = ActiveConnector;
            if (active == null || !active.IsConnected)
            {
                ClearLatestState();
                // Attempt reconnect periodically when disconnected
                if (first != null)
                {
                    try
                    {
                        await ActivateAsync(first.SimulatorId, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Reconnect attempt failed");
                    }
                }
                await Task.Delay(2000, stoppingToken);
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

    private void ClearLatestState()
    {
        lock (_lock)
        {
            _latest = null;
        }
    }
}
