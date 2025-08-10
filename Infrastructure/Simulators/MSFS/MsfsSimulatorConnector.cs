using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Domain;
using Microsoft.Extensions.Logging;
using SimConnect.NET;

namespace BARS_Client_V2.Infrastructure.Simulators.Msfs;

/// <summary>
/// MSFS Connection implementation using SimConnect.NET.
/// </summary>
public sealed class MsfsSimulatorConnector : ISimulatorConnector, IDisposable
{
    private readonly ILogger<MsfsSimulatorConnector> _logger;
    private SimConnectClient? _client;
    private const int PollDelayMs = 15_000;

    public MsfsSimulatorConnector(ILogger<MsfsSimulatorConnector> logger)
    {
        _logger = logger;
    }

    public string SimulatorId => "MSFS";
    public string DisplayName => "Microsoft Flight Simulator";
    public bool IsConnected => _client?.IsConnected == true;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;
        try
        {
            var client = new SimConnectClient("BARS Client");
            await client.ConnectAsync();
            if (client.IsConnected)
            {
                _client = client;
                _logger.LogInformation("Connected to MSFS via SimConnect.NET");
            }
            else
            {
                client.Dispose();
                _logger.LogWarning("Failed to connect to MSFS (client not connected after ConnectAsync)");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MSFS connection attempt failed");
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
        if (!IsConnected) yield break;
        while (!ct.IsCancellationRequested && IsConnected)
        {
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
}
