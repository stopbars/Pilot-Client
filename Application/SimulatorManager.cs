using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Domain;
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

        if (await connector.ConnectAsync(ct))
        {
            lock (_lock) _active = connector;
            _logger.LogInformation("Activated simulator {sim}", connector.DisplayName);
            return true;
        }
        return false;
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
                await Task.Delay(1000, stoppingToken);
                continue;
            }
            try
            {
                await foreach (var raw in active.StreamRawAsync(stoppingToken))
                {
                    lock (_lock) _latest = new FlightState(raw.Latitude, raw.Longitude, raw.OnGround);
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
}
