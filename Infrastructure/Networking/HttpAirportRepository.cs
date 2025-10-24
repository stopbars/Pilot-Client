using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Application;
using BARS_Client_V2.Domain;

namespace BARS_Client_V2.Infrastructure.Networking;

// Fetches approved contributions and builds a list of airports with their available scenery packages.
internal sealed class HttpAirportRepository : IAirportRepository
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly JsonSerializerOptions _jsonOptions;

    public HttpAirportRepository(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    private sealed record ContributionDto(
        string id,
        string userId,
        string userDisplayName,
        string airportIcao,
        string packageName,
        string submittedXml,
        string? notes,
        DateTime submissionDate,
        string status,
        string? rejectionReason,
        DateTime? decisionDate
    );

    private sealed record ContributionsResponse(List<ContributionDto> contributions, long total, int page, long limit, int totalPages);

    public async Task<(IReadOnlyList<Airport> Items, int TotalCount)> SearchAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        // We fetch the full approved list (server default limit is huge per provided sample) and do client side paging.
        // If the endpoint later supports server-side paging + filtering we can shift to query params.
        var client = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://v2.stopbars.com/contributions?status=approved");
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var data = await JsonSerializer.DeserializeAsync<ContributionsResponse>(stream, _jsonOptions, ct)
                   ?? new ContributionsResponse(new List<ContributionDto>(), 0, 1, 0, 0);

        // Group by airport -> collect distinct package names
        var grouped = data.contributions
            .GroupBy(c => c.airportIcao.Trim().ToUpperInvariant())
            .Select(g => new
            {
                ICAO = g.Key,
                Packages = g.Select(c => c.packageName)
                             .Where(p => !string.IsNullOrWhiteSpace(p))
                             .Select(p => new SceneryPackage(p.Trim()))
                             .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .ToList()
            })
            .ToList();

        var airportNames = await FetchAirportNamesAsync(client, grouped.Select(g => g.ICAO), ct);

        var airports = grouped
            .Select(g =>
            {
                airportNames.TryGetValue(g.ICAO, out var name);
                return new Airport(g.ICAO, name, g.Packages);
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            airports = airports.Where(a =>
                    a.ICAO.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(a.Name) && a.Name.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    a.SceneryPackages.Any(p => p.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                               .ToList();
        }

        var total = airports.Count;
        var items = airports
            .OrderBy(a => a.ICAO, StringComparer.OrdinalIgnoreCase)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return (items, total);
    }

    private sealed class AirportMetadataDto
    {
        public string? Icao { get; set; }
        public string? Name { get; set; }
    }

    private async Task<Dictionary<string, string>> FetchAirportNamesAsync(HttpClient client, IEnumerable<string> icaos, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var distinct = icaos
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim().ToUpperInvariant())
            .Where(i => i.Length == 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinct.Length == 0)
        {
            return result;
        }

        const int batchSize = 50;
        foreach (var batch in distinct.Chunk(batchSize))
        {
            var payload = string.Join(",", batch);
            if (string.IsNullOrEmpty(payload))
            {
                continue;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://v2.stopbars.com/airports?icao={payload}");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var data = await JsonSerializer.DeserializeAsync<Dictionary<string, AirportMetadataDto>>(stream, _jsonOptions, ct);
            if (data == null)
            {
                continue;
            }

            foreach (var entry in data)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                var icao = entry.Key.Trim().ToUpperInvariant();
                var name = entry.Value?.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result[icao] = name.Trim();
                }
            }
        }

        return result;
    }
}
