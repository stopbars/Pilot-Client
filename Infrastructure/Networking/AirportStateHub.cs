using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BARS_Client_V2.Domain;
using BARS_Client_V2.Infrastructure.Simulators.Msfs;
using Microsoft.Extensions.Logging;
using BARS_Client_V2.Services;
using BARS_Client_V2.Application;

namespace BARS_Client_V2.Infrastructure.Networking;

public sealed class AirportStateHub
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AirportStateHub> _logger;
    private readonly SimulatorManager _simManager;
    private readonly ConcurrentDictionary<string, PointMetadata> _metadata = new(); // pointId -> metadata
    private readonly ConcurrentDictionary<string, PointState> _states = new(); // pointId -> current state
    private readonly ConcurrentDictionary<string, List<LightLayout>> _layouts = new(); // pointId -> lights
    private readonly SemaphoreSlim _mapLock = new(1, 1);
    private string? _mapAirport; // airport code currently loaded
    private DateTime _lastSnapshotUtc = DateTime.MinValue;
    private readonly TimeSpan _snapshotStaleAfter = TimeSpan.FromSeconds(25); // if no snapshot / updates for this long, re-request
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private readonly Timer _reconcileTimer;
    private volatile bool _requestInFlight;
    private DateTime _lastSnapshotRequestUtc = DateTime.MinValue;
    private readonly TimeSpan _snapshotRequestMinInterval = TimeSpan.FromSeconds(20);

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
        try
        {
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
    }

    /// <summary>
    /// Sends a STOPBAR_CROSSING packet over the airport websocket for the currently loaded airport.
    /// Server expects the objectId (BarsId) of the stopbar line being crossed.
    /// </summary>
    /// <param name="objectId">Bars object id of the stopbar line that was crossed.</param>
    public void SendStopbarCrossing(string objectId)
    {
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
        await EnsureMapLoadedAsync(airport!, ct);
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
                meta = new PointMetadata(id!, airport!, "", id!, 0, 0, null, null, null, false, false);
                _metadata[id!] = meta;
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
        await EnsureMapLoadedAsync(airport!, ct);
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

    public async Task EnsureMapLoadedAsync(string airport, CancellationToken ct = default)
    {
        if (string.Equals(_mapAirport, airport, StringComparison.OrdinalIgnoreCase)) return;
        await _mapLock.WaitAsync(ct);
        try
        {
            if (string.Equals(_mapAirport, airport, StringComparison.OrdinalIgnoreCase)) return;
            await LoadMapInternalAsync(airport, ct);
        }
        finally
        {
            _mapLock.Release();
        }
    }

    /// <summary>
    /// Force reload current airport map after scenery package change.
    /// </summary>
    private async void OnSceneryPackageChanged(string icao, string simulator, string newPackage)
    {
        try
        {
            // Only reload if we're currently on that airport AND the changed simulator matches the current one
            if (!string.Equals(_mapAirport, icao, StringComparison.OrdinalIgnoreCase)) return;
            var runningSim = GetRunningSimulatorId();
            if (!string.Equals(runningSim, simulator, StringComparison.OrdinalIgnoreCase)) return;

            _logger.LogInformation("Scenery package changed for {apt} ({sim}) -> {pkg}; reloading map", icao, simulator, newPackage);
            await _mapLock.WaitAsync();
            try
            {
                // Clear current map caches and state, then load again using the new selection
                _metadata.Clear();
                _layouts.Clear();
                _states.Clear();
                _lastSnapshotUtc = DateTime.MinValue;
                _lastUpdateUtc = DateTime.MinValue;
                await LoadMapInternalAsync(icao, CancellationToken.None);
                // Immediately request a fresh snapshot so clients rebuild using the new layout
                _ = RequestSnapshotAsync(icao);
            }
            finally { _mapLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hot-reload map for {apt} after package change", icao);
        }
    }

    private async Task LoadMapInternalAsync(string airport, CancellationToken ct)
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
                    return;
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
            return;
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
            var xmlInner = await respInner.Content.ReadAsStringAsync(ct);
            try
            {
                var docInner = XDocument.Parse(xmlInner);
                ParseMap(docInner, airport);
                _mapAirport = airport;
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

        await TryFetchAsync(package, false);
    }

    public string? CreateOfflineSnapshot()
    {
        string? airport;
        PointMetadata[] metas;
        lock (_mapLock)
        {
            airport = _mapAirport;
        }
        if (string.IsNullOrWhiteSpace(airport)) return null;
        metas = _metadata.Values.ToArray();
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

    private static bool IsStopbar(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return type.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0 &&
               type.IndexOf("BAR", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void ParseMap(XDocument doc, string airport)
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

            if (_layouts.TryGetValue(id!, out var existingLights))
            {
                // Merge duplicate definition: append lights
                existingLights.AddRange(newLights);
                duplicateMerged++;
                // Recompute representative lat/lon across ALL lights now associated with this id
                if (existingLights.Count > 0)
                {
                    var avgLat = existingLights.Average(l => l.Latitude);
                    var avgLon = existingLights.Average(l => l.Longitude);
                    if (_metadata.TryGetValue(id!, out var existingMeta))
                    {
                        _metadata[id!] = existingMeta with { Latitude = avgLat, Longitude = avgLon, Type = type, Orientation = orientation, Color = color };
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
                    _layouts[id!] = newLights;
                }
                double repLat = 0, repLon = 0;
                if (newLights.Count > 0)
                {
                    repLat = newLights.Average(l => l.Latitude);
                    repLon = newLights.Average(l => l.Longitude);
                }
                var meta = new PointMetadata(id!, airport, type, id!, repLat, repLon, null, orientation, color, false, false);
                _metadata[id!] = meta;
            }

            lightCount += newLights.Count;
        }

        _logger.LogInformation("Parsed map {apt} BarsObjects={raw} uniquePoints={uniq} duplicatesMerged={dups} lights={lights}", airport, barsObjectElements, uniquePointIds, duplicateMerged, lightCount);
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

    private Task RequestSnapshotAsync(string airport)
    {
        if (_requestInFlight) return Task.CompletedTask;
        if ((DateTime.UtcNow - _lastSnapshotRequestUtc) < _snapshotRequestMinInterval) return Task.CompletedTask;
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
}
