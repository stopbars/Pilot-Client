using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BARS_Client_V2.Application;
using BARS_Client_V2.Services;

namespace BARS_Client_V2.Infrastructure.Networking;

internal sealed class AirportWebSocketManager : BackgroundService
{
    private readonly SimulatorManager _simManager;
    private readonly INearestAirportService _nearestAirportService;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<AirportWebSocketManager> _logger;
    private readonly object _sync = new();

    private ClientWebSocket? _ws;
    private string? _connectedAirport;
    private string? _apiToken; // cached
    private DateTime _lastTokenLoadUtc = DateTime.MinValue;
    private Task? _receiveLoopTask;
    private CancellationTokenSource? _receiveCts;
    private DateTime _nextConnectAttemptUtc = DateTime.MinValue; // backoff gate
    private Task? _heartbeatTask;
    private string? _tokenUsedForConnection;

    public string? ConnectedAirport { get { lock (_sync) return _connectedAirport; } }
    public bool IsConnected { get { lock (_sync) return _ws?.State == WebSocketState.Open; } }
    public event Action<string>? MessageReceived;
    public event Action? Connected;
    public event Action<int>? ConnectionError; // status code (e.g. 401, 403)
    public event Action<string>? Disconnected; // reason

    public AirportWebSocketManager(
        SimulatorManager simManager,
        INearestAirportService nearestAirportService,
        ISettingsStore settingsStore,
        ILogger<AirportWebSocketManager> logger)
    {
        _simManager = simManager;
        _nearestAirportService = nearestAirportService;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in airport WebSocket manager loop");
            }
            await Task.Delay(2000, stoppingToken);
        }
        await DisconnectAsync("Service stopping");
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        var flight = _simManager.LatestState;
        var connector = _simManager.ActiveConnector;
        if (flight == null || connector == null || !connector.IsConnected)
        {
            await DisconnectAsync("No active simulator");
            return;
        }

        if (!flight.OnGround)
        {
            await DisconnectAsync("Airborne");
            return;
        }

        string? icao = _nearestAirportService.GetCachedNearest(flight.Latitude, flight.Longitude);
        if (icao == null)
        {
            try { icao = await _nearestAirportService.ResolveAndCacheAsync(flight.Latitude, flight.Longitude, ct); } catch { }
        }

        if (string.IsNullOrWhiteSpace(icao) || icao.Length != 4)
        {
            await DisconnectAsync("No nearby airport");
            return;
        }

        var token = await GetApiTokenAsync(ct);
        if (!IsValidToken(token))
        {
            await DisconnectAsync("Missing/invalid API token");
            return;
        }

        lock (_sync)
        {
            if (_ws != null && _ws.State == WebSocketState.Open &&
                string.Equals(_connectedAirport, icao, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_tokenUsedForConnection, token, StringComparison.Ordinal))
            {
                return; // already connected to desired airport with same token
            }
        }

        // Respect backoff window after failures (e.g. 403 when user not authorized/connected)
        if (DateTime.UtcNow < _nextConnectAttemptUtc)
        {
            return;
        }

        await ConnectAsync(icao, token!, ct);
    }

    private async Task<string?> GetApiTokenAsync(CancellationToken ct)
    {
        // Always reload to react quickly to user changes (cheap IO)
        try
        {
            var settings = await _settingsStore.LoadAsync();
            _apiToken = settings.ApiToken;
            _lastTokenLoadUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings for API token");
        }
        return _apiToken;
    }

    private static bool IsValidToken(string? token) => !string.IsNullOrWhiteSpace(token) && token.StartsWith("BARS_", StringComparison.Ordinal);

    private async Task ConnectAsync(string icao, string token, CancellationToken ct)
    {
        await DisconnectAsync("Switching airport/token");
        var uri = new Uri($"wss://v2.stopbars.com/connect?airport={icao.ToUpperInvariant()}&key={token}");
        var ws = new ClientWebSocket();
        try
        {
            _logger.LogInformation("Connecting airport WebSocket for {icao}", icao);
            await ws.ConnectAsync(uri, ct);
            if (ws.State != WebSocketState.Open)
            {
                _logger.LogWarning("Airport WebSocket not open after connect attempt (state {state})", ws.State);
                ws.Dispose();
                _nextConnectAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(10); // generic backoff
                return;
            }
            lock (_sync)
            {
                _ws = ws;
                _connectedAirport = icao;
                _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
                _tokenUsedForConnection = token;
                _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_receiveCts.Token));
            }
            _logger.LogInformation("Airport WebSocket connected for {icao}", icao);
            _nextConnectAttemptUtc = DateTime.MinValue; // reset on success
            try { Connected?.Invoke(); } catch { }
        }
        catch (OperationCanceledException)
        {
            ws.Dispose();
        }
        catch (WebSocketException wex)
        {
            _logger.LogWarning(wex, "Airport WebSocket connect failed for {icao}: {msg}", icao, wex.Message);
            ws.Dispose();
            // If 403 (user not connected to VATSIM / not authorized) apply longer backoff to avoid spam
            if (wex.Message.Contains("403"))
            {
                _nextConnectAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                try { ConnectionError?.Invoke(403); } catch { }
            }
            else
            {
                _nextConnectAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                if (wex.Message.Contains("401"))
                {
                    try { ConnectionError?.Invoke(401); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error connecting airport WebSocket for {icao}", icao);
            ws.Dispose();
            _nextConnectAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            try { ConnectionError?.Invoke(0); } catch { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var localWs = _ws;
        if (localWs == null) return;
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested && localWs.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult? result;
                do
                {
                    result = await localWs.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Airport WebSocket closed by server: {status} {desc}", result.CloseStatus, result.CloseStatusDescription);
                        await DisconnectAsync("Server closed");
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                } while (!result.EndOfMessage);

                if (sb.Length > 0)
                {
                    var msg = sb.ToString();
                    try { MessageReceived?.Invoke(msg); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException wex)
        {
            _logger.LogWarning(wex, "Airport WebSocket receive error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in Airport WebSocket receive loop");
        }
        finally
        {
            await DisconnectAsync("Receive loop ended");
        }
    }

    private async Task DisconnectAsync(string reason)
    {
        ClientWebSocket? ws;
        CancellationTokenSource? rcts;
        lock (_sync)
        {
            ws = _ws;
            rcts = _receiveCts;
            _ws = null;
            _receiveCts = null;
            _receiveLoopTask = null;
            _heartbeatTask = null;
            if (_connectedAirport != null)
            {
                _logger.LogInformation("Disconnecting airport WebSocket ({airport}) - {reason}", _connectedAirport, reason);
            }
            _connectedAirport = null;
            _tokenUsedForConnection = null;
        }
        try { rcts?.Cancel(); } catch { }
        if (ws != null)
        {
            try
            {
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                {
                    // Attempt to send CLOSE message before closing websocket
                    try
                    {
                        var payload = Encoding.UTF8.GetBytes("{ \"type\": \"CLOSE\" }");
                        using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await ws.SendAsync(payload, WebSocketMessageType.Text, true, sendCts.Token);
                    }
                    catch { }
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token);
                }
            }
            catch { }
            finally { ws.Dispose(); }
        }
        try { Disconnected?.Invoke(reason); } catch { }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { break; }
            if (ct.IsCancellationRequested) break;
            ClientWebSocket? ws;
            lock (_sync) ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) continue;
            try
            {
                var hb = Encoding.UTF8.GetBytes("{ \"type\": \"HEARTBEAT\" }");
                using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws.SendAsync(hb, WebSocketMessageType.Text, true, sendCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Heartbeat send failed");
            }
        }
    }
}
