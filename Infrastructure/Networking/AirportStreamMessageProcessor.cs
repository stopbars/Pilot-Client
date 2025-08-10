using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Domain;
using Microsoft.Extensions.Logging;

namespace BARS_Client_V2.Infrastructure.Networking;

/// <summary>
/// Parses messages from <see cref="AirportWebSocketManager"/> and maintains an in-memory cache
/// of point metadata + state. Fires events when point state changes are received.
/// </summary>
internal sealed class AirportStreamMessageProcessor
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AirportStreamMessageProcessor> _logger;
    private readonly ConcurrentDictionary<string, PointState> _points = new(); // key: point id -> last state
    private readonly ConcurrentDictionary<string, PointMetadata> _metadata = new(); // key: point id -> metadata only (for quick lookup)

    public event Action<PointState>? PointStateChanged; // fired for each STATE_UPDATE (after metadata fetch if needed)
    public event Action<string>? InitialStateLoaded; // airport ICAO when initial state processed

    public AirportStreamMessageProcessor(IHttpClientFactory httpFactory, ILogger<AirportStreamMessageProcessor> logger)
    {
        _httpClient = httpFactory.CreateClient();
        _logger = logger;
    }

    public PointState? TryGetPoint(string id) => _points.TryGetValue(id, out var ps) ? ps : null;

    /// <summary>
    /// Entry point invoked by websocket manager for each raw JSON message.
    /// </summary>
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
                    await HandleStateUpdateAsync(root, ct);
                    break;
                case "HEARTBEAT_ACK":
                    // nothing to do;
                    break;
                default:
                    _logger.LogDebug("Unhandled stream message type {type}", type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process stream message");
        }
    }

    private async Task HandleInitialStateAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        if (!data.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array) return;

        // Collect raw entries first
        var tempList = new System.Collections.Generic.List<(string id, bool state, long ts)>();
        int idx = 0;
        foreach (var obj in objects.EnumerateArray())
        {
            if (obj.ValueKind != JsonValueKind.Object) continue;
            var id = obj.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var state = obj.TryGetProperty("state", out var stateProp) && stateProp.ValueKind == JsonValueKind.True;
            var ts = obj.TryGetProperty("timestamp", out var tsProp) && tsProp.TryGetInt64(out var lts) ? lts : 0L;
            tempList.Add((id!, state, ts));
            if (idx++ < 5) // sample a few for visibility
            {
                _logger.LogInformation("Initial object {id} state={state} ts={ts}", id, state, ts);
            }
        }

        if (tempList.Count == 0) return;
        _logger.LogInformation("INITIAL_STATE contained {count} objects", tempList.Count);

        // Determine which IDs lack metadata
        var missing = new System.Collections.Generic.List<string>();
        foreach (var entry in tempList)
        {
            if (!_metadata.ContainsKey(entry.id)) missing.Add(entry.id);
        }
        if (missing.Count > 0)
        {
            _logger.LogInformation("Fetching metadata batch for {missingCount} new points", missing.Count);
            // Batch in chunks of 100 (API limit)
            const int batchSize = 100;
            for (int i = 0; i < missing.Count; i += batchSize)
            {
                var slice = missing.GetRange(i, Math.Min(batchSize, missing.Count - i));
                await FetchMetadataBatchAsync(slice, ct);
                _logger.LogInformation("Fetched metadata batch size {batch}", slice.Count);
            }
        }

        // Now create states
        foreach (var (id, state, ts) in tempList)
        {
            if (_metadata.TryGetValue(id, out var meta))
            {
                var ps = new PointState(meta, state, ts);
                _points[id] = ps;
                try { PointStateChanged?.Invoke(ps); } catch { }
            }
            else
            {
                // Fallback single fetch if batch missed or failed
                var ps = await EnsureMetadataAndCreateStateAsync(id, state, ts, ct);
                if (ps != null)
                {
                    _points[id] = ps;
                    try { PointStateChanged?.Invoke(ps); } catch { }
                }
            }
        }

        var airport = root.TryGetProperty("airport", out var aProp) ? aProp.GetString() : null;
        if (!string.IsNullOrWhiteSpace(airport))
        {
            try { InitialStateLoaded?.Invoke(airport!); } catch { }
        }
    }

    private async Task HandleStateUpdateAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        var id = data.TryGetProperty("objectId", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) return;
        var newState = data.TryGetProperty("state", out var stateProp) && stateProp.ValueKind == JsonValueKind.True;
        var ts = root.TryGetProperty("timestamp", out var tsProp) && tsProp.TryGetInt64(out var lts) ? lts : 0L;
        var ps = await EnsureMetadataAndCreateStateAsync(id!, newState, ts, ct);
        if (ps != null)
        {
            _points[id!] = ps;
            _logger.LogInformation("STATE_UPDATE {id} -> {state} ts={ts}", id, newState, ts);
            try { PointStateChanged?.Invoke(ps); } catch { }
        }
    }

    private async Task<PointState?> EnsureMetadataAndCreateStateAsync(string id, bool isOn, long ts, CancellationToken ct)
    {
        if (_metadata.TryGetValue(id, out var metaFromCache))
        {
            return new PointState(metaFromCache, isOn, ts);
        }
        try
        {
            var url = $"https://v2.stopbars.com/points/{id}";
            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Point metadata fetch failed for {id} status {status}", id, resp.StatusCode);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (TryParsePointMetadata(doc.RootElement, out var meta))
            {
                _metadata[id] = meta!;
                return new PointState(meta!, isOn, ts);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error fetching point metadata for {id}", id);
            return null;
        }
    }

    private async Task FetchMetadataBatchAsync(System.Collections.Generic.IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        try
        {
            var url = $"https://v2.stopbars.com/points?ids={string.Join(",", ids)}"; // API expects comma separated
            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Batch metadata fetch failed status {status}", resp.StatusCode);
                return;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("points", out var pts) || pts.ValueKind != JsonValueKind.Array) return;
            foreach (var p in pts.EnumerateArray())
            {
                if (TryParsePointMetadata(p, out var meta))
                {
                    _metadata[meta!.Id] = meta!;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Batch metadata fetch error");
        }
    }

    private bool TryParsePointMetadata(JsonElement root, out PointMetadata? meta)
    {
        try
        {
            var id = root.TryGetProperty("id", out var idp) ? idp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) { meta = null; return false; }
            string airportId = root.TryGetProperty("airportId", out var ap) ? ap.GetString() ?? string.Empty : string.Empty;
            string type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? string.Empty : string.Empty;
            string name = root.TryGetProperty("name", out var np) ? np.GetString() ?? string.Empty : string.Empty;
            double lat = 0, lon = 0;
            if (root.TryGetProperty("coordinates", out var coord) && coord.ValueKind == JsonValueKind.Object)
            {
                lat = coord.TryGetProperty("lat", out var latProp) && latProp.TryGetDouble(out var dlat) ? dlat : 0;
                lon = coord.TryGetProperty("lng", out var lonProp) && lonProp.TryGetDouble(out var dlon) ? dlon : 0;
            }
            string? directionality = root.TryGetProperty("directionality", out var dir) ? dir.GetString() : null;
            string? orientation = root.TryGetProperty("orientation", out var ori) ? ori.GetString() : null;
            string? color = root.TryGetProperty("color", out var col) ? col.GetString() : null;
            bool elevated = root.TryGetProperty("elevated", out var el) && el.ValueKind == JsonValueKind.True;
            bool ihp = root.TryGetProperty("ihp", out var ihpProp) && ihpProp.ValueKind == JsonValueKind.True;
            meta = new PointMetadata(id, airportId, type, name, lat, lon, directionality, orientation, color, elevated, ihp);
            return true;
        }
        catch
        {
            meta = null; return false;
        }
    }
}
