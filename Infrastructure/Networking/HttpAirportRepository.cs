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
using BARS_Client_V2.Services;

namespace BARS_Client_V2.Infrastructure.Networking;

// Fetches approved contributions and caches them briefly. Filters by simulator locally.
internal sealed class HttpAirportRepository : IAirportRepository
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly JsonSerializerOptions _jsonOptions;

    private IReadOnlyList<ApprovedContributionMetadata>? _cachedContributions;
    private Dictionary<string, string>? _cachedAirportNames;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private DateTime _cacheLoadedUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheFreshness = TimeSpan.FromMinutes(5);

    public HttpAirportRepository(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// Ensures contributions and airport names are available as one complete cache entry.
    /// </summary>
    private async Task EnsureCacheLoadedAsync(CancellationToken ct)
    {
        if (IsCacheFresh()) return;

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (IsCacheFresh()) return;

            try
            {
                var client = _httpClientFactory.CreateClient();
                var contributions = await ApprovedContributionsCache
                    .GetAsync(client, cancellationToken: ct)
                    .ConfigureAwait(false);

                var allIcaos = contributions
                    .Select(c => c.AirportIcao.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var airportNames = await FetchAirportNamesAsync(client, allIcaos, ct);

                // Publish both values only after the complete refresh succeeds.
                _cachedAirportNames = airportNames;
                _cachedContributions = contributions;
                _cacheLoadedUtc = DateTime.UtcNow;
            }
            catch when (!ct.IsCancellationRequested && _cachedContributions != null && _cachedAirportNames != null)
            {
                // Keep the last complete cache entry. A later request will retry.
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<(IReadOnlyList<Airport> Items, int TotalCount)> SearchAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        // Ensure a recent complete cache entry is available.
        await EnsureCacheLoadedAsync(ct);

        // Get the configured simulator from SceneryService to filter packages (UI toggle selection)
        var configuredSimulator = SceneryService.Instance.ConfiguredSimulator;
        var airports = BuildAirports(configuredSimulator);

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

    private bool IsCacheFresh() =>
        _cachedContributions != null &&
        _cachedAirportNames != null &&
        DateTime.UtcNow - _cacheLoadedUtc < CacheFreshness;

    public async Task<IReadOnlyList<Airport>> GetAllForSimulatorAsync(
        string simulator,
        CancellationToken ct = default)
    {
        await EnsureCacheLoadedAsync(ct);
        return BuildAirports(simulator)
            .OrderBy(airport => airport.ICAO, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<Airport> BuildAirports(string simulator)
    {
        var normalizedSimulator = simulator.Trim().ToLowerInvariant();

        // Group by airport -> collect distinct package names for the requested simulator only.
        var contributions = _cachedContributions ?? Array.Empty<ApprovedContributionMetadata>();
        var airportNames = _cachedAirportNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var grouped = contributions
            .Where(c =>
            {
                // Default to "msfs2020" if simulator is null or empty (matches SceneryService behavior)
                var sim = string.IsNullOrWhiteSpace(c.Simulator) ? "msfs2020" : c.Simulator.Trim().ToLowerInvariant();
                return string.Equals(sim, normalizedSimulator, StringComparison.OrdinalIgnoreCase);
            })
            .GroupBy(c => c.AirportIcao.Trim().ToUpperInvariant())
            .Select(g => new
            {
                ICAO = g.Key,
                Packages = g.Where(c => !string.IsNullOrWhiteSpace(c.PackageName))
                             .Select(c => new SceneryPackage(
                                 c.PackageName.Trim(),
                                 c.ArtifactIdentity,
                                 c.ArtifactGenerationId,
                                 c.RemovalArtifactKey))
                             .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .ToList()
            })
            .ToList();

        return grouped
            .Select(g =>
            {
                airportNames.TryGetValue(g.ICAO, out var name);
                return new Airport(g.ICAO, name, g.Packages);
            })
            .ToList();
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
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var data = await JsonSerializer.DeserializeAsync<Dictionary<string, AirportMetadataDto>>(stream, _jsonOptions, ct);
            if (data == null)
            {
                throw new InvalidOperationException("The airport metadata response was invalid.");
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
