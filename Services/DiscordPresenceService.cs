using System;
using System.Threading;
using System.Threading.Tasks;
using DiscordRPC;
using DiscordRPC.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BARS_Client_V2.Application;
using BARS_Client_V2.Infrastructure.Networking;

namespace BARS_Client_V2.Services;

/// <summary>
/// Background hosted service that manages Discord Rich Presence for the BARS Pilot Client.
/// </summary>
internal sealed class DiscordPresenceService : BackgroundService
{
    private const string DiscordAppId = "1396344027560804373";
    private readonly ILogger<DiscordPresenceService> _logger;
    private readonly SimulatorManager _simManager;
    private readonly AirportWebSocketManager _wsManager;
    private readonly INearestAirportService _nearestAirportService;
    private DiscordRpcClient? _client;
    private DateTime _appStartUtc = DateTime.UtcNow;
    private readonly object _stateLock = new();
    private string? _lastDetails;
    private string? _lastState;
    private string? _lastSmallKey;
    private string? _lastSmallText;
    private string? _lastLargeText;
    private string _serverStatus = "Disconnected";
    private DateTime _lastSendUtc = DateTime.MinValue;
    private bool _forceUpdate;

    // Rate limiting thresholds
    private static readonly TimeSpan MinUpdateInterval = TimeSpan.FromSeconds(12);

    public DiscordPresenceService(ILogger<DiscordPresenceService> logger,
        SimulatorManager simManager,
        AirportWebSocketManager wsManager,
        INearestAirportService nearestAirportService)
    {
        _logger = logger;
        _simManager = simManager;
        _wsManager = wsManager;
        _nearestAirportService = nearestAirportService;
        HookWsEvents();
    }

    private void HookWsEvents()
    {
        _wsManager.Connected += () => { _serverStatus = "Connected"; QueueImmediate(); };
        _wsManager.Disconnected += reason => { _serverStatus = reason.Contains("Error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Disconnected"; QueueImmediate(); };
        _wsManager.ConnectionError += code => { _serverStatus = code == 0 ? "Error" : code.ToString(); QueueImmediate(); };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _client = new DiscordRpcClient(DiscordAppId, autoEvents: false)
            {
                Logger = new ConsoleLogger() { Level = DiscordRPC.Logging.LogLevel.None } // silence library logs (we log ourselves)
            };
            _client.Initialize();
            _appStartUtc = DateTime.UtcNow;
            _logger.LogInformation("Discord Rich Presence initialized");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Discord Rich Presence client");
            return; // abort background loop to avoid spam
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            if (stoppingToken.IsCancellationRequested) break;
            try { PublishIfChanged(); } catch (Exception ex) { _logger.LogDebug(ex, "PublishIfChanged error"); }
        }
    }

    private void QueueImmediate()
    {
        lock (_stateLock) _forceUpdate = true;
    }

    private void PublishIfChanged()
    {
        var client = _client;
        if (client == null || !client.IsInitialized) return;
        string airport = "";
        var latest = _simManager.LatestState;
        if (latest != null)
        {
            try
            {
                var cached = _nearestAirportService.GetCachedNearest(latest.Latitude, latest.Longitude);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    airport = cached.ToUpperInvariant();
                }
                else
                {
                    // async resolve to populate cache; trigger update when done
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var resolved = await _nearestAirportService.ResolveAndCacheAsync(latest.Latitude, latest.Longitude, CancellationToken.None);
                            if (!string.IsNullOrWhiteSpace(resolved)) QueueImmediate();
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }
        var connector = _simManager.ActiveConnector;
        string simCode = connector?.SimulatorId ?? "None";
        bool simConnected = connector?.IsConnected == true;
        bool? is2024 = null;
        if (connector is BARS_Client_V2.Infrastructure.Simulators.Msfs.MsfsSimulatorConnector msfsConn)
        {
            is2024 = msfsConn.IsMsfs2024;
            if (simConnected)
            {
                simCode = is2024 == true ? "MSFS 2024" : (is2024 == false ? "MSFS 2020" : "MSFS");
            }
        }

        // Choose small image key for MSFS variants (prefer 2024 if ID indicates such in future; using msfs2020 for now)
        string? smallKey = null;
        if (simConnected)
        {
            if (is2024 == true) smallKey = "msfs2024"; else if (is2024 == false) smallKey = "msfs2020"; else smallKey = "msfs2020"; // default/fallback
        }
        string? smallText = simConnected ? simCode : null;

        string serverSegment = _serverStatus switch
        {
            "Connected" => "Server: Connected",
            "Disconnected" => "Server: Disconnected",
            "Error" => "Server: Error",
            "403" => "Server: No VATSIM Connection",
            var other => other.StartsWith("4") || other.StartsWith("5") ? $"Server: {other}" : "Server: Reconnecting"
        };

        // Details now only show airport + simulator; server status moved to state line per request
        string details = $"Airport: {airport}"; // simulator removed (small image already conveys it)
        string state = serverSegment; // server state shown on second line
        string largeText = "BARS Pilot Client";

        bool changed;
        bool force;
        lock (_stateLock)
        {
            force = _forceUpdate;
            changed = force || details != _lastDetails || state != _lastState || smallKey != _lastSmallKey || smallText != _lastSmallText || largeText != _lastLargeText;
            if (changed)
            {
                _lastDetails = details;
                _lastState = state;
                _lastSmallKey = smallKey;
                _lastSmallText = smallText;
                _lastLargeText = largeText;
                _forceUpdate = false;
            }
        }

        var now = DateTime.UtcNow;
        if (!changed && (now - _lastSendUtc) < TimeSpan.FromMinutes(5)) return; // periodic keepalive every 5 min
        if (!force && (now - _lastSendUtc) < MinUpdateInterval) return;

        _lastSendUtc = now;

        try
        {
            var presence = new RichPresence
            {
                Details = details.Length > 128 ? details.Substring(0, 128) : details,
                State = state,
                Assets = new Assets
                {
                    LargeImageKey = "bars_logo",
                    LargeImageText = largeText,
                    SmallImageKey = smallKey,
                    SmallImageText = smallText
                },
                Timestamps = new Timestamps(_appStartUtc),
                Buttons = new[]
                {
                    new Button { Label = "What is BARS?", Url = "https://stopbars.com" },
                    new Button { Label = "Join Discord", Url = "https://stopbars.com/discord" }
                }
            };
            client.SetPresence(presence);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set Discord presence");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _client?.ClearPresence();
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing Discord RPC client");
        }
        return base.StopAsync(cancellationToken);
    }
}
