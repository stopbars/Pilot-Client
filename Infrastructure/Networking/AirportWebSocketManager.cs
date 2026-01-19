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
    private string? _desiredAirport;
    private int _consecutiveForbiddenFailures;
    private bool _offlineMode;
    private Task? _offlineSnapshotTask;
    private const int ForbiddenThresholdForOffline = 3;
    private AirportStateHub? _stateHub;
    private static readonly TimeSpan OfflineReconnectInterval = TimeSpan.FromSeconds(45);

    public string? ConnectedAirport { get { lock (_sync) return _connectedAirport; } }
    public bool IsConnected { get { lock (_sync) return _ws?.State == WebSocketState.Open; } }
    public bool IsOfflineMode { get { lock (_sync) return _offlineMode; } }
    public event Action<string>? MessageReceived;
    public event Action? Connected;
    public event Action<int>? ConnectionError; // status code (e.g. 401, 403)
    public event Action<string>? Disconnected; // reason
    public event Action<bool>? OfflineModeChanged;
    public event Action<string>? OfflineSnapshotReceived;

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

    public void AttachHub(AirportStateHub hub)
    {
        hub.OutboundPacketRequested += (airport, rawJson) =>
        {
            try { _ = SendRawAsync(rawJson); } catch { }
        };
        _stateHub = hub;
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
        DeactivateOfflineMode();
        await DisconnectAsync("Service stopping");
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        var flight = _simManager.LatestState;
        var connector = _simManager.ActiveConnector;
        if (flight == null || connector == null || !connector.IsConnected)
        {
            SetDesiredAirport(null);
            DeactivateOfflineMode();
            await DisconnectAsync("No active simulator");
            return;
        }

        if (!flight.OnGround)
        {
            SetDesiredAirport(null);
            DeactivateOfflineMode();
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
            SetDesiredAirport(null);
            DeactivateOfflineMode();
            await DisconnectAsync("No nearby airport");
            return;
        }

        SetDesiredAirport(icao);

        var token = await GetApiTokenAsync(ct);
        if (!IsValidToken(token))
        {
            DeactivateOfflineMode();
            await DisconnectAsync("Missing/invalid API token");
            return;
        }

        lock (_sync)
        {
            if (_ws != null && _ws.State == WebSocketState.Open &&
                string.Equals(_connectedAirport, icao, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_tokenUsedForConnection, token, StringComparison.Ordinal))
            {
                DeactivateOfflineMode();
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
        await DisconnectAsync("Processing");
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
            ResetForbiddenFailures();
            DeactivateOfflineMode();
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
                HandleForbiddenFailure();
            }
            else
            {
                _nextConnectAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                ResetForbiddenFailures();
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
            ResetForbiddenFailures();
            try { ConnectionError?.Invoke(0); } catch { }
        }
    }

    private void SetDesiredAirport(string? icao)
    {
        var normalized = string.IsNullOrWhiteSpace(icao) ? null : icao.ToUpperInvariant();
        bool changed;
        bool offline;
        lock (_sync)
        {
            offline = _offlineMode;
            changed = !string.Equals(_desiredAirport, normalized, StringComparison.Ordinal);
            _desiredAirport = normalized;
        }
        if (changed && offline)
        {
            BeginOfflineSnapshotWorkflow();
        }
    }

    private void HandleForbiddenFailure()
    {
        var count = Interlocked.Increment(ref _consecutiveForbiddenFailures);
        var delay = count >= ForbiddenThresholdForOffline ? OfflineReconnectInterval : TimeSpan.FromSeconds(10);
        _nextConnectAttemptUtc = DateTime.UtcNow + delay;
        try { ConnectionError?.Invoke(403); } catch { }
        if (count >= ForbiddenThresholdForOffline)
        {
            ActivateOfflineMode();
        }
    }

    private void ResetForbiddenFailures() => Interlocked.Exchange(ref _consecutiveForbiddenFailures, 0);

    private void ActivateOfflineMode()
    {
        bool shouldNotify;
        lock (_sync)
        {
            if (_offlineMode)
            {
                return;
            }
            _offlineMode = true;
            shouldNotify = true;
        }
        BeginOfflineSnapshotWorkflow();
        if (shouldNotify)
        {
            try { OfflineModeChanged?.Invoke(true); } catch { }
        }
    }

    private void DeactivateOfflineMode()
    {
        bool wasActive;
        lock (_sync)
        {
            wasActive = _offlineMode;
            _offlineMode = false;
        }
        ResetForbiddenFailures();
        if (!wasActive) return;
        try { OfflineModeChanged?.Invoke(false); } catch { }
    }

    private void BeginOfflineSnapshotWorkflow()
    {
        lock (_sync)
        {
            if (!_offlineMode)
            {
                return;
            }
            if (_offlineSnapshotTask != null && !_offlineSnapshotTask.IsCompleted)
            {
                return;
            }
            _offlineSnapshotTask = Task.Run(EmitOfflineSnapshotAsync);
        }
    }

    private async Task EmitOfflineSnapshotAsync()
    {
        try
        {
            var airport = await DetermineOfflineAirportAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(airport))
            {
                _logger.LogDebug("Offline snapshot skipped (no airport available)");
                return;
            }

            var hub = _stateHub;
            if (hub == null)
            {
                _logger.LogDebug("Offline snapshot skipped (state hub missing)");
                return;
            }

            try
            {
                await hub.EnsureMapLoadedAsync(airport, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EnsureMapLoadedAsync failed during offline fallback");
            }

            var snapshot = hub.CreateOfflineSnapshot();
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                _logger.LogDebug("Offline snapshot empty for {airport}", airport);
                return;
            }

            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(_connectedAirport))
                {
                    _connectedAirport = airport;
                }
            }

            try { OfflineSnapshotReceived?.Invoke(snapshot); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit offline snapshot");
        }
    }

    private async Task<string?> DetermineOfflineAirportAsync()
    {
        string? current;
        lock (_sync)
        {
            current = _desiredAirport;
        }
        if (!string.IsNullOrWhiteSpace(current))
        {
            return current;
        }

        return await ResolveTargetAirportAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<string?> ResolveTargetAirportAsync(CancellationToken ct)
    {
        var flight = _simManager.LatestState;
        if (flight == null || !flight.OnGround)
        {
            return null;
        }

        var cached = _nearestAirportService.GetCachedNearest(flight.Latitude, flight.Longitude);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        try
        {
            return await _nearestAirportService.ResolveAndCacheAsync(flight.Latitude, flight.Longitude, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve nearest airport for offline snapshot");
            return null;
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

    private Task SendRawAsync(string raw)
    {
        ClientWebSocket? ws;
        lock (_sync) ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return Task.CompletedTask;
        var payload = System.Text.Encoding.UTF8.GetBytes(raw);
        return ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
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
