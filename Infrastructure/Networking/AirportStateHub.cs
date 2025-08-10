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

    public AirportStateHub(IHttpClientFactory httpFactory, ILogger<AirportStateHub> logger)
    {
        _httpClient = httpFactory.CreateClient();
        _logger = logger;
    }

    public event Action<string>? MapLoaded; // airport
    public event Action<PointState>? PointStateChanged; // fired for initial + updates

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

    private async Task HandleInitialStateAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("airport", out var aProp) || aProp.ValueKind != JsonValueKind.String) return;
        var airport = aProp.GetString();
        if (string.IsNullOrWhiteSpace(airport)) return;
        await EnsureMapLoadedAsync(airport!, ct);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        if (!data.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array) return;
        int count = 0;
        foreach (var obj in objects.EnumerateArray())
        {
            if (obj.ValueKind != JsonValueKind.Object) continue;
            var id = obj.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var on = obj.TryGetProperty("state", out var stp) && stp.ValueKind == JsonValueKind.True;
            var ts = obj.TryGetProperty("timestamp", out var tsp) && tsp.TryGetInt64(out var lts) ? lts : 0L;
            if (!_metadata.TryGetValue(id!, out var meta))
            {
                // Create placeholder if not in map (should be rare)
                meta = new PointMetadata(id!, airport!, "", id!, 0, 0, null, null, null, false, false);
                _metadata[id!] = meta;
            }
            var ps = new PointState(meta, on, ts);
            _states[id!] = ps;
            count++;
            try { PointStateChanged?.Invoke(ps); } catch { }
        }
        _logger.LogInformation("INITIAL_STATE processed {count} points for {apt}", count, airport);
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
            meta = new PointMetadata(id!, _mapAirport ?? string.Empty, "", id!, 0, 0, null, null, null, false, false);
            _metadata[id!] = meta;
        }
        var ps = new PointState(meta, on, ts);
        _states[id!] = ps;
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
            var url = $"https://v2.stopbars.com/maps/{airport}/latest";
            _logger.LogInformation("Fetching airport XML map {url}", url);
            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Airport map fetch failed {status}", resp.StatusCode);
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
                _logger.LogWarning(ex, "Error parsing airport map {apt}", airport);
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
}
