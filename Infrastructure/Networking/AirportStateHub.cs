using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BARS_Client_V2.Domain;
using Microsoft.Extensions.Logging;
using BARS_Client_V2.Services;

namespace BARS_Client_V2.Infrastructure.Networking;

internal sealed class AirportStateHub
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AirportStateHub> _logger;
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

    public AirportStateHub(IHttpClientFactory httpFactory, ILogger<AirportStateHub> logger)
    {
        _httpClient = httpFactory.CreateClient();
        _logger = logger;
        _reconcileTimer = new Timer(_ => ReconcileLoop(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public event Action<string>? MapLoaded; // airport
    public event Action<PointState>? PointStateChanged; // fired for initial + updates
    public event Action<string, string>? OutboundPacketRequested; // (airport, rawJson)

    public bool TryGetPoint(string id, out PointState state) => _states.TryGetValue(id, out state!);
    public bool TryGetLightLayout(string id, out IReadOnlyList<LightLayout> lights)
    {
        if (_layouts.TryGetValue(id, out var list)) { lights = list; return true; }
        lights = Array.Empty<LightLayout>();
        return false;
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
        // Remove orphan states not present in snapshot (object deleted server-side)
        var removed = 0;
        foreach (var existing in _states.Keys.ToList())
        {
            if (!seen.Contains(existing))
            {
                if (_states.TryRemove(existing, out _)) removed++;
            }
        }
        if (removed > 0)
        {
            _logger.LogInformation("Snapshot removed {removed} stale objects for {apt}", removed, airport);
        }
        _logger.LogInformation("STATE_SNAPSHOT applied objects={applied} removed={removed} airport={apt}", applied, removed, airport);
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
        foreach (var obj in objects.EnumerateArray())
        {
            if (obj.ValueKind != JsonValueKind.Object) continue;
            var id = obj.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
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
        _logger.LogInformation("INITIAL_STATE processed {count} points (ignoredUnknown={ignored}) for {apt}", count, ignoredUnknown, airport);
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

    private async Task EnsureMapLoadedAsync(string airport, CancellationToken ct)
    {
        if (string.Equals(_mapAirport, airport, StringComparison.OrdinalIgnoreCase)) return;
        await _mapLock.WaitAsync(ct);
        try
        {
            if (string.Equals(_mapAirport, airport, StringComparison.OrdinalIgnoreCase)) return;
            _metadata.Clear();
            _layouts.Clear();
            // Determine currently selected scenery package for this airport (if any). If none selected yet, auto-select first available.
            string package = string.Empty;
            try
            {
                package = SceneryService.Instance.GetSelectedPackage(airport);
                if (string.IsNullOrWhiteSpace(package))
                {
                    // Fetch contributions (packages) and choose first for this airport.
                    var all = await SceneryService.Instance.GetAvailablePackagesAsync();
                    if (all.TryGetValue(airport, out var pkgList) && pkgList.Count > 0)
                    {
                        package = pkgList.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First();
                        // Persist selection so UI stays consistent.
                        SceneryService.Instance.SetSelectedPackage(airport, package);
                        _logger.LogInformation("Auto-selected first package '{pkg}' for airport {apt}", package, airport);
                    }
                    else
                    {
                        _logger.LogWarning("No packages found for airport {apt} when attempting to auto-select; aborting map load", airport);
                        return; // Without a package the server cannot return a map.
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed determining package for airport {apt}", airport);
                return; // Can't proceed without a valid package.
            }
            var safePkg = Uri.EscapeDataString(package);
            var url = $"https://v2.stopbars.com/maps/{airport}/packages/{safePkg}/latest";
            _logger.LogInformation("Fetching airport XML map {apt} package={pkg} url={url}", airport, package, url);
            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Airport map fetch failed {status} apt={apt} package={pkg}", resp.StatusCode, airport, package);
                return;
            }
            var xml = await resp.Content.ReadAsStringAsync(ct);
            try
            {
                var doc = XDocument.Parse(xml);
                ParseMap(doc, airport);
                _mapAirport = airport;
                try { MapLoaded?.Invoke(airport); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing airport map {apt} package={pkg}", airport, package);
            }
        }
        finally
        {
            _mapLock.Release();
        }
    }

    private void ParseMap(XDocument doc, string airport)
    {
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "BarsLights") return;
        int pointCount = 0, lightCount = 0;
        foreach (var obj in root.Elements("BarsObject"))
        {
            var id = obj.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var type = obj.Attribute("type")?.Value ?? string.Empty;
            var objProps = obj.Element("Properties");
            var color = objProps?.Element("Color")?.Value;
            var orientation = objProps?.Element("Orientation")?.Value;
            var lightList = new List<LightLayout>();
            double sumLat = 0, sumLon = 0; int cnt = 0;
            foreach (var le in obj.Elements("Light"))
            {
                var posText = le.Element("Position")?.Value;
                if (!TryParseLatLon(posText, out var lat, out var lon)) continue;
                double? hdg = null;
                var headingStr = le.Element("Heading")?.Value;
                if (double.TryParse(headingStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hdgVal)) hdg = hdgVal;
                var lColor = le.Element("Properties")?.Element("Color")?.Value ?? color;
                int? stateId = null;
                var stateAttr = le.Attribute("stateId")?.Value;
                if (int.TryParse(stateAttr, out var sidVal)) stateId = sidVal;
                lightList.Add(new LightLayout(lat, lon, hdg, lColor, stateId));
                sumLat += lat; sumLon += lon; cnt++; lightCount++;
            }
            double repLat = 0, repLon = 0;
            if (cnt > 0) { repLat = sumLat / cnt; repLon = sumLon / cnt; }
            var meta = new PointMetadata(id!, airport, type, id!, repLat, repLon, null, orientation, color, false, false);
            _metadata[id!] = meta;
            if (lightList.Count > 0) _layouts[id!] = lightList;
            pointCount++;
        }
        _logger.LogInformation("Parsed map {apt} points={pts} lights={lights}", airport, pointCount, lightCount);
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

    public sealed record LightLayout(double Latitude, double Longitude, double? Heading, string? Color, int? StateId);

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
