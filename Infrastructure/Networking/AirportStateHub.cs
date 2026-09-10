using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Infrastructure.Simulators.Msfs;
using Microsoft.Extensions.Logging;
using BARS_Client_V2.Services;
using BARS_Client_V2.Application;

namespace BARS_Client_V2.Infrastructure.Networking;

public sealed class AirportStateHub
{
    private const int MaxMapBytes = 16 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AirportStateHub> _logger;
    private readonly SimulatorManager _simManager;
    private readonly ConcurrentDictionary<string, PointMetadata> _metadata = new(); // pointId -> metadata
    private readonly ConcurrentDictionary<string, PointState> _states = new(); // pointId -> current state
    private readonly ConcurrentDictionary<string, List<LightLayout>> _layouts = new(); // pointId -> lights
    private readonly SemaphoreSlim _mapLock = new(1, 1);
    private readonly SemaphoreSlim _messageLock = new(1, 1);
    private readonly object _mapStateSync = new();
    private string? _mapAirport; // airport code currently loaded
    private DateTime _lastSnapshotUtc = DateTime.MinValue;
    private readonly TimeSpan _snapshotStaleAfter = TimeSpan.FromSeconds(25); // if no snapshot / updates for this long, re-request
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private readonly Timer _reconcileTimer;
    private volatile bool _requestInFlight;
    private DateTime _lastSnapshotRequestUtc = DateTime.MinValue;
    private readonly TimeSpan _snapshotRequestMinInterval = TimeSpan.FromSeconds(20);
    private volatile bool _testingMode;
    private string? _testingAirport;
    private long _packageReloadVersion;

    public AirportStateHub(IHttpClientFactory httpFactory, ILogger<AirportStateHub> logger, SimulatorManager simManager)
    {
        _httpClient = httpFactory.CreateClient();
        _logger = logger;
        _simManager = simManager;
        _reconcileTimer = new Timer(_ => ReconcileLoop(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        // React to scenery package changes while connected so users don't need to restart the client.
        try { SceneryService.Instance.PackageChanged += OnSceneryPackageChanged; } catch { }
    }

    public event Action<string>? MapLoaded; // airport
    public event Action<PointState>? PointStateChanged; // fired for initial + updates
    public event Action<IReadOnlyList<PointState>>? MultiPointStateChanged; // fired once per MULTI_STATE_UPDATE packet
    public event Action<string, string>? OutboundPacketRequested; // (airport, rawJson)
    public event Action<TestingModeChangedEventArgs>? TestingModeChanged;

    public bool IsTestingMode => _testingMode;
    public string? TestingAirport => _testingAirport;

    public bool TryGetPoint(string id, out PointState state) => _states.TryGetValue(id, out state!);
    public bool TryGetLightLayout(string id, out IReadOnlyList<LightLayout> lights)
    {
        if (_layouts.TryGetValue(id, out var list)) { lights = list; return true; }
        lights = Array.Empty<LightLayout>();
        return false;
    }

    private string GetRunningSimulatorId()
    {
        var active = _simManager.ActiveConnector;
        if (active is MsfsSimulatorConnector msfsConnector)
        {
            var is2024 = msfsConnector.IsMsfs2024;
            if (is2024 == true) return "msfs2024";
            if (is2024 == false) return "msfs2020";
        }

        return SceneryService.Instance.CurrentSimulator;
    }

    public async Task ProcessAsync(string json, CancellationToken ct = default)
    {
        await _messageLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_testingMode)
            {
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();
            switch (type)
            {
                case "INITIAL_STATE":
                    await HandleInitialStateAsync(root, ct);
                    break;
                case "STATE_SNAPSHOT":
                    await HandleSnapshotAsync(root, ct);
                    break;
                case "STATE_UPDATE":
                    HandleStateUpdate(root);
                    break;
                case "MULTI_STATE_UPDATE":
                    HandleMultiStateUpdate(root);
                    break;
                case "HEARTBEAT_ACK":
                    break;
                default:
                    _logger.LogTrace("Unhandled message type {type}", type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AirportStateHub message parse failed");
        }
        finally
        {
            _messageLock.Release();
        }
    }

    /// <summary>
    /// Sends a STOPBAR_CROSSING packet over the airport websocket for the currently loaded airport.
    /// Server expects the objectId (BarsId) of the stopbar line being crossed.
    /// </summary>
    /// <param name="objectId">Bars object id of the stopbar line that was crossed.</param>
    public void SendStopbarCrossing(string objectId)
    {
        if (_testingMode) return;
        if (string.IsNullOrWhiteSpace(objectId)) return;
        var packet = JsonSerializer.Serialize(new { type = "STOPBAR_CROSSING", data = new { objectId = objectId } });
        try { OutboundPacketRequested?.Invoke(_mapAirport ?? string.Empty, packet); } catch { }
        _logger.LogInformation("Sent STOPBAR_CROSSING objectId={id}", objectId);
    }

    private async Task HandleSnapshotAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("airport", out var aProp) || aProp.ValueKind != JsonValueKind.String) return;
        var airport = aProp.GetString();
        if (string.IsNullOrWhiteSpace(airport)) return;
        if (!await EnsureMapLoadedAsync(airport!, ct).ConfigureAwait(false)) return;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        if (!data.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array) return;
        int applied = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in objects.EnumerateArray())
        {
            if (obj.ValueKind != JsonValueKind.Object) continue;
            var id = obj.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            seen.Add(id!);
            var on = obj.TryGetProperty("state", out var stp) && stp.ValueKind == JsonValueKind.True;
            var ts = obj.TryGetProperty("timestamp", out var tsp) && tsp.TryGetInt64(out var lts) ? lts : 0L;
            if (!_metadata.TryGetValue(id!, out var meta))
            {
                _logger.LogTrace("Skipping snapshot state for unknown object {id}", id);
                continue;
            }
            if (_states.TryGetValue(id!, out var current) && IsOlder(ts, current.TimestampMs))
            {
                continue;
            }
            var ps = new PointState(meta, on, ts);
            _states[id!] = ps;
            applied++;
            try { PointStateChanged?.Invoke(ps); } catch { }
        }
        _lastSnapshotUtc = DateTime.UtcNow;
        _lastUpdateUtc = _lastSnapshotUtc;
        var removed = RemoveUnknownStates(seen);
        var revertedOffline = ApplyOfflineFallbackStates(airport!, seen);
        if (removed > 0)
        {
            _logger.LogInformation("Snapshot removed {removed} stale objects for {apt}", removed, airport);
        }
        if (revertedOffline > 0)
        {
            _logger.LogInformation("Snapshot reverted {offline} map-only objects to offline default for {apt}", revertedOffline, airport);
        }
        _logger.LogInformation("STATE_SNAPSHOT applied objects={applied} removed={removed} offlineFallback={offline} airport={apt}",
            applied, removed, revertedOffline, airport);
    }

    private async Task HandleInitialStateAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("airport", out var aProp) || aProp.ValueKind != JsonValueKind.String) return;
        var airport = aProp.GetString();
        if (string.IsNullOrWhiteSpace(airport)) return;
        if (!await EnsureMapLoadedAsync(airport!, ct).ConfigureAwait(false)) return;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        if (!data.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array) return;
        int count = 0;
        int ignoredUnknown = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in objects.EnumerateArray())
        {
            if (obj.ValueKind != JsonValueKind.Object) continue;
            var id = obj.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            seen.Add(id!);
            var on = obj.TryGetProperty("state", out var stp) && stp.ValueKind == JsonValueKind.True;
            var ts = obj.TryGetProperty("timestamp", out var tsp) && tsp.TryGetInt64(out var lts) ? lts : 0L;
            if (!_metadata.TryGetValue(id!, out var meta))
            {
                // Ignore objects not present in map to avoid spawning at (0,0). We'll request a snapshot soon if map is outdated.
                ignoredUnknown++;
                continue;
            }
            if (_states.TryGetValue(id!, out var current) && IsOlder(ts, current.TimestampMs))
            {
                continue;
            }
            var ps = new PointState(meta, on, ts);
            _states[id!] = ps;
            count++;
            try { PointStateChanged?.Invoke(ps); } catch { }
        }
        _lastUpdateUtc = DateTime.UtcNow;
        var removed = RemoveUnknownStates(seen);
        var revertedOffline = ApplyOfflineFallbackStates(airport!, seen);
        _logger.LogInformation("INITIAL_STATE processed {count} points (ignoredUnknown={ignored}) for {apt}", count, ignoredUnknown, airport);
        if (revertedOffline > 0)
        {
            _logger.LogInformation("INITIAL_STATE reverted {offline} map-only objects to offline default for {apt}", revertedOffline, airport);
        }
        if (ignoredUnknown > 0)
        {
            // Force snapshot sooner (maybe map changed). Bump lastSnapshot to trigger reconcile check.
            _lastSnapshotUtc = DateTime.MinValue;
        }
    }

    private void HandleStateUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        var id = data.TryGetProperty("objectId", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) return;
        var on = data.TryGetProperty("state", out var stp) && stp.ValueKind == JsonValueKind.True;
        var ts = root.TryGetProperty("timestamp", out var tsp) && tsp.TryGetInt64(out var lts) ? lts : 0L;
        if (!_metadata.TryGetValue(id!, out var meta))
        {
            // Skip updates for unknown objects rather than creating placeholder at (0,0)
            _logger.LogTrace("Skipping update for unknown object {id}", id);
            return;
        }
        if (_states.TryGetValue(id!, out var current) && IsOlder(ts, current.TimestampMs)) return;
        var ps = new PointState(meta, on, ts);
        _states[id!] = ps;
        _lastUpdateUtc = DateTime.UtcNow;
        try { PointStateChanged?.Invoke(ps); } catch { }
    }

    private void HandleMultiStateUpdate(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        if (!data.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array) return;
        var ts = root.TryGetProperty("timestamp", out var tsp) && tsp.TryGetInt64(out var lts) ? lts : 0L;
        var anyApplied = false;
        List<PointState>? appliedStates = null;
        foreach (var update in updates.EnumerateArray())
        {
            if (update.ValueKind != JsonValueKind.Object) continue;
            var id = update.TryGetProperty("objectId", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var on = update.TryGetProperty("state", out var stp) && stp.ValueKind == JsonValueKind.True;
            if (!_metadata.TryGetValue(id!, out var meta))
            {
                // Skip updates for unknown objects rather than creating placeholder at (0,0)
                _logger.LogTrace("Skipping update for unknown object {id}", id);
                continue;
            }
            if (_states.TryGetValue(id!, out var current) && IsOlder(ts, current.TimestampMs))
            {
                continue;
            }
            var ps = new PointState(meta, on, ts);
            _states[id!] = ps;
            anyApplied = true;
            appliedStates ??= new List<PointState>();
            appliedStates.Add(ps);
            try { PointStateChanged?.Invoke(ps); } catch { }
        }
        if (anyApplied)
        {
            _lastUpdateUtc = DateTime.UtcNow;
            if (appliedStates != null && appliedStates.Count > 0)
            {
                try { MultiPointStateChanged?.Invoke(appliedStates); } catch { }
            }
        }
    }

    public async Task<bool> EnsureMapLoadedAsync(string airport, CancellationToken ct = default)
    {
        if (_testingMode)
        {
            return string.Equals(_mapAirport, airport, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(_mapAirport, airport, StringComparison.OrdinalIgnoreCase)) return true;
        await _mapLock.WaitAsync(ct);
        try
        {
            if (string.Equals(_mapAirport, airport, StringComparison.OrdinalIgnoreCase)) return true;
            return await LoadMapInternalAsync(airport, ct).ConfigureAwait(false);
        }
        finally
        {
            _mapLock.Release();
        }
    }

    public async Task LoadTestingMapAsync(string airport, string barsXml, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(airport) || airport.Length != 4 || !airport.All(char.IsLetterOrDigit))
        {
            throw new ArgumentException("Testing map response did not include a valid ICAO.", nameof(airport));
        }

        if (string.IsNullOrWhiteSpace(barsXml))
        {
            throw new ArgumentException("Testing map response did not include BARS XML.", nameof(barsXml));
        }

        var normalizedAirport = airport.Trim().ToUpperInvariant();
        var document = ParseBarsXmlSecure(barsXml);
        if (document.Root == null || document.Root.Name.LocalName != "BarsLights")
        {
            throw new InvalidDataException("Testing map XML did not contain a BarsLights root element.");
        }
        var parsedMetadata = new Dictionary<string, PointMetadata>(StringComparer.Ordinal);
        var parsedLayouts = new Dictionary<string, List<LightLayout>>(StringComparer.Ordinal);
        ParseMap(document, normalizedAirport, parsedMetadata, parsedLayouts);

        await _mapLock.WaitAsync(ct);
        try
        {
            _testingMode = true;
            _testingAirport = normalizedAirport;
            CommitMap(normalizedAirport, parsedMetadata, parsedLayouts);
            _lastSnapshotUtc = DateTime.MaxValue;
            _lastUpdateUtc = DateTime.UtcNow;
            _lastSnapshotRequestUtc = DateTime.MaxValue;

            try { MapLoaded?.Invoke(normalizedAirport); } catch { }

            var applied = ApplyTestingModeStates(normalizedAirport);
            _logger.LogInformation("Testing mode loaded map {apt}; seeded testing states for {count} objects", normalizedAirport, applied);
        }
        finally
        {
            _mapLock.Release();
        }

        try { TestingModeChanged?.Invoke(new TestingModeChangedEventArgs(true, normalizedAirport)); } catch { }
    }

    public async Task EndTestingModeAsync(CancellationToken ct = default)
    {
        await _mapLock.WaitAsync(ct);
        try
        {
            if (!_testingMode)
            {
                return;
            }

            _testingMode = false;
            _testingAirport = null;
            lock (_mapStateSync)
            {
                _mapAirport = null;
                _metadata.Clear();
                _layouts.Clear();
                _states.Clear();
            }
            _lastSnapshotUtc = DateTime.MinValue;
            _lastUpdateUtc = DateTime.MinValue;
            _lastSnapshotRequestUtc = DateTime.MinValue;
        }
        finally
        {
            _mapLock.Release();
        }

        try { TestingModeChanged?.Invoke(new TestingModeChangedEventArgs(false, null)); } catch { }
        try { MapLoaded?.Invoke(string.Empty); } catch { }
        _logger.LogInformation("Testing mode ended");
    }

    /// <summary>
    /// Force reload current airport map after scenery package change.
    /// </summary>
    private async void OnSceneryPackageChanged(string icao, string simulator, string newPackage)
    {
        try
        {
            if (_testingMode) return;
            // Only reload if we're currently on that airport AND the changed simulator matches the current one
            if (!string.Equals(_mapAirport, icao, StringComparison.OrdinalIgnoreCase)) return;
            var runningSim = GetRunningSimulatorId();
            if (!string.Equals(runningSim, simulator, StringComparison.OrdinalIgnoreCase)) return;

            var reloadVersion = Interlocked.Increment(ref _packageReloadVersion);
            _logger.LogInformation("Scenery package changed for {apt} ({sim}) -> {pkg}; reloading map", icao, simulator, newPackage);
            await _messageLock.WaitAsync();
            try
            {
                await _mapLock.WaitAsync();
                try
                {
                    if (reloadVersion != Volatile.Read(ref _packageReloadVersion)) return;
                    if (!string.Equals(
                            SceneryService.Instance.GetSelectedPackage(icao, simulator),
                            newPackage,
                            StringComparison.Ordinal)) return;

                    var loaded = await LoadMapInternalAsync(icao, CancellationToken.None, reloadVersion);
                    if (loaded && reloadVersion == Volatile.Read(ref _packageReloadVersion))
                    {
                        // Package changes need a new snapshot now. The normal 20-second
                        // throttle would otherwise leave the freshly loaded map empty.
                        await RequestSnapshotAsync(icao, force: true);
                    }
                }
                finally { _mapLock.Release(); }
            }
            finally { _messageLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hot-reload map for {apt} after package change", icao);
        }
    }

    private async Task<bool> LoadMapInternalAsync(string airport, CancellationToken ct, long? reloadVersion = null)
    {
        // Determine currently selected scenery package for this airport (if any). If none selected yet, auto-select first available.
        string package = string.Empty;
        List<string>? airportPackages = null; // cache list for fallback retry
        try
        {
            var runningSim = GetRunningSimulatorId();
            package = SceneryService.Instance.GetSelectedPackage(airport, runningSim);
            var all = await SceneryService.Instance.GetAvailablePackagesAsync();
            if (all.TryGetValue(runningSim, out var packagesForSim) &&
                packagesForSim.TryGetValue(airport, out var pkgList) &&
                pkgList.Count > 0)
            {
                airportPackages = pkgList.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            }
            if (string.IsNullOrWhiteSpace(package))
            {
                if (airportPackages == null || airportPackages.Count == 0)
                {
                    _logger.LogWarning("No packages found for airport {apt} ({sim}) when attempting to auto-select; aborting map load", airport, runningSim);
                    return false;
                }
                package = airportPackages.First();
                SceneryService.Instance.SetSelectedPackage(airport, runningSim, package);
                _logger.LogInformation("Auto-selected first package '{pkg}' for airport {apt} ({sim})", package, airport, runningSim);
            }
            else
            {
                // Resolve selection to one of the available package names (case-insensitive, supports substring like "2024").
                if (airportPackages != null && airportPackages.Count > 0)
                {
                    var originalSelection = package;
                    var exact = airportPackages.FirstOrDefault(p => string.Equals(p, originalSelection, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(exact))
                    {
                        package = exact; // normalize casing
                    }
                    else
                    {
                        var partial = airportPackages.FirstOrDefault(p => p.IndexOf(originalSelection, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!string.IsNullOrEmpty(partial)) package = partial;
                    }
                    // If still not matched, fall back to first available.
                    if (!airportPackages.Contains(package, StringComparer.OrdinalIgnoreCase))
                    {
                        var fallback = airportPackages.First();
                        _logger.LogWarning("Previously selected package '{old}' for {apt} no longer available; falling back to '{fb}'", originalSelection, airport, fallback);
                        package = fallback;
                        try { SceneryService.Instance.SetSelectedPackage(airport, runningSim, package); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed determining package for airport {apt}", airport);
            return false;
        }

        async Task<bool> TryFetchAsync(string pkg, bool isRetry)
        {
            var safePkgInner = Uri.EscapeDataString(pkg);
            var currentSim = GetRunningSimulatorId();
            var urlInner = $"https://v2.stopbars.com/maps/{airport}/packages/{safePkgInner}/latest?simulator={currentSim}";
            _logger.LogInformation("Fetching airport XML map {apt} package={pkg} simulator={sim} url={url} retry={retry}", airport, pkg, currentSim, urlInner, isRetry);
            using var respInner = await _httpClient.GetAsync(urlInner, ct);
            if (!respInner.IsSuccessStatusCode)
            {
                _logger.LogWarning("Airport map fetch failed {status} apt={apt} package={pkg} retry={retry}", respInner.StatusCode, airport, pkg, isRetry);
                if (!isRetry && respInner.StatusCode == HttpStatusCode.NotFound && airportPackages != null && airportPackages.Count > 0)
                {
                    var first = airportPackages.First();
                    if (!string.Equals(first, pkg, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Retrying map fetch with fallback first package '{fb}' for {apt}", first, airport);
                        try { SceneryService.Instance.SetSelectedPackage(airport, GetRunningSimulatorId(), first); } catch { }
                        package = first;
                        return await TryFetchAsync(first, true);
                    }
                }
                return false;
            }
            var xmlInner = await ReadBoundedUtf8Async(respInner.Content, MaxMapBytes, ct).ConfigureAwait(false);
            try
            {
                var docInner = ParseBarsXmlSecure(xmlInner);
                if (docInner.Root == null || docInner.Root.Name.LocalName != "BarsLights")
                {
                    _logger.LogWarning("Airport map has an invalid root apt={apt} package={pkg}", airport, pkg);
                    return false;
                }
                if (reloadVersion.HasValue &&
                    reloadVersion.Value != Volatile.Read(ref _packageReloadVersion))
                {
                    return false;
                }

                var parsedMetadata = new Dictionary<string, PointMetadata>(StringComparer.Ordinal);
                var parsedLayouts = new Dictionary<string, List<LightLayout>>(StringComparer.Ordinal);
                ParseMap(docInner, airport, parsedMetadata, parsedLayouts);
                CommitMap(airport, parsedMetadata, parsedLayouts);
                _lastSnapshotUtc = DateTime.MinValue; // force fresh snapshot soon
                try { MapLoaded?.Invoke(airport); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing airport map {apt} package={pkg}", airport, pkg);
                return false;
            }
        }

        return await TryFetchAsync(package, false);
    }

    public string? CreateOfflineSnapshot()
    {
        string? airport;
        PointMetadata[] metas;
        lock (_mapStateSync)
        {
            airport = _mapAirport;
            metas = _metadata.Values.ToArray();
        }
        if (string.IsNullOrWhiteSpace(airport)) return null;
        if (metas.Length == 0) return null;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "STATE_SNAPSHOT");
            writer.WriteString("airport", airport);
            writer.WriteBoolean("offline", true);
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writer.WriteBoolean("offline", true);
            writer.WritePropertyName("objects");
            writer.WriteStartArray();
            foreach (var meta in metas)
            {
                writer.WriteStartObject();
                writer.WriteString("id", meta.Id);
                writer.WriteBoolean("state", !IsStopbar(meta.Type));
                writer.WriteNumber("timestamp", nowMs);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private int RemoveUnknownStates(ISet<string> serverKnown)
    {
        var removed = 0;
        foreach (var existing in _states.Keys.ToList())
        {
            if (serverKnown != null && serverKnown.Contains(existing)) continue;
            if (_metadata.ContainsKey(existing)) continue;
            if (_states.TryRemove(existing, out _)) removed++;
        }
        return removed;
    }

    private int ApplyOfflineFallbackStates(string airport, ISet<string> serverKnown)
    {
        if (string.IsNullOrWhiteSpace(airport)) return 0;
        var reverted = 0;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kvp in _metadata)
        {
            var id = kvp.Key;
            var meta = kvp.Value;
            if (!string.Equals(meta.AirportId, airport, StringComparison.OrdinalIgnoreCase)) continue;
            if (serverKnown != null && serverKnown.Contains(id)) continue;
            var offlineOn = !IsStopbar(meta.Type);
            if (_states.TryGetValue(id, out var existing) && existing.IsOn == offlineOn)
            {
                continue;
            }
            var state = new PointState(meta, offlineOn, timestamp);
            _states[id] = state;
            reverted++;
            try { PointStateChanged?.Invoke(state); } catch { }
        }
        return reverted;
    }

    private int ApplyTestingModeStates(string airport)
    {
        if (string.IsNullOrWhiteSpace(airport)) return 0;
        var applied = 0;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kvp in _metadata)
        {
            var id = kvp.Key;
            var meta = kvp.Value;
            if (!string.Equals(meta.AirportId, airport, StringComparison.OrdinalIgnoreCase)) continue;
            var state = new PointState(meta, true, timestamp);
            _states[id] = state;
            applied++;
            try { PointStateChanged?.Invoke(state); } catch { }
        }

        return applied;
    }

    private static bool IsStopbar(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return type.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0 &&
               type.IndexOf("BAR", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void ParseMap(
        XDocument doc,
        string airport,
        IDictionary<string, PointMetadata> metadata,
        IDictionary<string, List<LightLayout>> layouts)
    {
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "BarsLights") return;
        int barsObjectElements = 0; // raw BarsObject element count (including duplicates)
        int uniquePointIds = 0;     // unique ids encountered
        int duplicateMerged = 0;    // number of BarsObject elements that were merged into an existing id
        int lightCount = 0;         // total lights (after merge, counting every <Light> processed)

        foreach (var obj in root.Elements("BarsObject"))
        {
            barsObjectElements++;
            var id = obj.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var type = obj.Attribute("type")?.Value ?? string.Empty;
            var objProps = obj.Element("Properties");
            var color = objProps?.Element("Color")?.Value;
            var orientation = objProps?.Element("Orientation")?.Value;

            // Parse lights for this element
            var newLights = new List<LightLayout>();
            foreach (var le in obj.Elements("Light"))
            {
                var posText = le.Element("Position")?.Value;
                if (!TryParseLatLon(posText, out var lat, out var lon)) continue;
                double? hdg = null;
                var headingStr = le.Element("Heading")?.Value;
                if (double.TryParse(headingStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hdgVal)) hdg = hdgVal;
                var lColor = le.Element("Properties")?.Element("Color")?.Value ?? color;
                int? stateId = null; if (int.TryParse(le.Attribute("stateId")?.Value, out var sidVal)) stateId = sidVal;
                int? offStateId = null; if (int.TryParse(le.Attribute("offStateId")?.Value, out var offSidVal)) offStateId = offSidVal;
                newLights.Add(new LightLayout(lat, lon, hdg, lColor, stateId, offStateId));
            }

            if (layouts.TryGetValue(id!, out var existingLights))
            {
                // Merge duplicate definition: append lights
                existingLights.AddRange(newLights);
                duplicateMerged++;
                // Recompute representative lat/lon across ALL lights now associated with this id
                if (existingLights.Count > 0)
                {
                    var avgLat = existingLights.Average(l => l.Latitude);
                    var avgLon = existingLights.Average(l => l.Longitude);
                    if (metadata.TryGetValue(id!, out var existingMeta))
                    {
                        metadata[id!] = existingMeta with { Latitude = avgLat, Longitude = avgLon, Type = type, Orientation = orientation, Color = color };
                    }
                }
                _logger.LogDebug("Merged duplicate BarsObject id={id} totalLights={cnt}", id, existingLights.Count);
            }
            else
            {
                // First time we see this id
                uniquePointIds++;
                if (newLights.Count > 0)
                {
                    layouts[id!] = newLights;
                }
                double repLat = 0, repLon = 0;
                if (newLights.Count > 0)
                {
                    repLat = newLights.Average(l => l.Latitude);
                    repLon = newLights.Average(l => l.Longitude);
                }
                var meta = new PointMetadata(id!, airport, type, id!, repLat, repLon, null, orientation, color, false, false);
                metadata[id!] = meta;
            }

            lightCount += newLights.Count;
        }

        _logger.LogInformation("Parsed map {apt} BarsObjects={raw} uniquePoints={uniq} duplicatesMerged={dups} lights={lights}", airport, barsObjectElements, uniquePointIds, duplicateMerged, lightCount);
    }

    private void CommitMap(
        string airport,
        IReadOnlyDictionary<string, PointMetadata> metadata,
        IReadOnlyDictionary<string, List<LightLayout>> layouts)
    {
        lock (_mapStateSync)
        {
            _metadata.Clear();
            _layouts.Clear();
            _states.Clear();
            foreach (var item in metadata) _metadata[item.Key] = item.Value;
            foreach (var item in layouts) _layouts[item.Key] = item.Value;
            _mapAirport = airport;
        }
    }

    private static bool IsOlder(long incomingTimestamp, long currentTimestamp) =>
        incomingTimestamp > 0 && currentTimestamp > 0 && incomingTimestamp < currentTimestamp;

    private static XDocument ParseBarsXmlSecure(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10_000_000
        };

        using var stringReader = new System.IO.StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader, LoadOptions.None);
    }

    private static async Task<string> ReadBoundedUtf8Async(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException($"Airport map exceeded the {maxBytes / (1024 * 1024)} MB safety limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > maxBytes)
            {
                throw new InvalidDataException($"Airport map exceeded the {maxBytes / (1024 * 1024)} MB safety limit.");
            }
            destination.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }

    private bool TryParseLatLon(string? csv, out double lat, out double lon)
    {
        lat = lon = 0;
        if (string.IsNullOrWhiteSpace(csv)) return false;
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        var ok1 = double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat);
        var ok2 = double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lon);
        return ok1 && ok2;
    }

    public sealed record LightLayout(double Latitude, double Longitude, double? Heading, string? Color, int? StateId, int? OffStateId);

    private void ReconcileLoop()
    {
        try
        {
            if (_testingMode) return;
            if (_mapAirport == null) return; // not connected yet
            var now = DateTime.UtcNow;
            var sinceUpdate = now - _lastUpdateUtc;
            if (sinceUpdate > _snapshotStaleAfter && !_requestInFlight)
            {
                _ = RequestSnapshotAsync(_mapAirport); // fire and forget
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReconcileLoop failed");
        }
    }

    private Task RequestSnapshotAsync(string airport, bool force = false)
    {
        if (_testingMode) return Task.CompletedTask;
        if (_requestInFlight) return Task.CompletedTask;
        if (!force && (DateTime.UtcNow - _lastSnapshotRequestUtc) < _snapshotRequestMinInterval) return Task.CompletedTask;
        _requestInFlight = true;
        try
        {
            // The websocket layer should allow sending raw text frames. We'll emit a GET_STATE packet.
            var packet = $"{{ \"type\": \"GET_STATE\", \"airport\": \"{airport}\", \"timestamp\": {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} }}";
            _lastSnapshotRequestUtc = DateTime.UtcNow;
            _logger.LogInformation("Requesting state snapshot for {apt}", airport);
            try { OutboundPacketRequested?.Invoke(airport, packet); } catch { }
        }
        finally
        {
            _requestInFlight = false;
        }
        return Task.CompletedTask;
    }

    public sealed record TestingModeChangedEventArgs(bool IsTestingMode, string? Airport);
}
