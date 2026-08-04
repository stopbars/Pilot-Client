using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Infrastructure.Networking;
using Microsoft.Extensions.Logging;

namespace BARS_Client_V2.Services;

public sealed class TestingModeService
{
    private const string ProtocolScheme = "bars";
    private const string ProtocolHost = "test";
    private const string GenerateUrl = "https://v2.stopbars.com/supports/generate";
    private const int MinTokenLength = 20;
    private const int MaxTokenLength = 160;
    private const int MaxResponseBytes = 10 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AirportStateHub _hub;
    private readonly ILogger<TestingModeService> _logger;

    public TestingModeService(IHttpClientFactory httpClientFactory, AirportStateHub hub, ILogger<TestingModeService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _hub = hub;
        _logger = logger;
    }

    public async Task HandleProtocolUrlAsync(string rawUrl, CancellationToken ct = default)
    {
        var token = ExtractGenerationToken(rawUrl);
        _logger.LogInformation("Fetching support testing map from generation token");

        var uriBuilder = new UriBuilder(GenerateUrl)
        {
            Query = "token=" + Uri.EscapeDataString(token)
        };

        using var response = await _httpClient.GetAsync(uriBuilder.Uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Testing map request failed with status {(int)response.StatusCode}.");
        }

        var body = await ReadBoundedResponseAsync(response, ct).ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize<TestingModeResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Testing map response was empty.");

        if (!string.Equals(payload.Token, token, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Testing map response token did not match the requested token.");
        }

        var icao = SanitizeIcao(payload.Icao);
        if (icao == null)
        {
            throw new InvalidOperationException("Testing map response did not include a valid ICAO.");
        }

        if (string.IsNullOrWhiteSpace(payload.BarsXml))
        {
            throw new InvalidOperationException("Testing map response did not include BARS XML.");
        }

        if (SceneryService.Instance.CurrentSimulator.Equals("xplane", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(payload.RemovalsJson))
        {
            var applied = await SceneryService.Instance
                .ApplyXPlaneTestingRemovalsAsync(icao, payload.RemovalsJson, ct)
                .ConfigureAwait(false);
            if (!applied)
            {
                throw new InvalidOperationException(
                    "The X-Plane testing removals did not match the installed scenery.");
            }
        }

        await _hub.LoadTestingMapAsync(icao, payload.BarsXml, ct).ConfigureAwait(false);
    }

    private static string ExtractGenerationToken(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, ProtocolScheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, ProtocolHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsupported BARS testing URL.");
        }

        var token = SanitizeGenerationToken(GetQueryValue(uri.Query, "token"));
        if (token == null)
        {
            throw new InvalidOperationException("Invalid testing generation token.");
        }

        return token;
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmed = query[0] == '?' ? query[1..] : query;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = WebUtility.UrlDecode(parts[0]);
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
        }

        return null;
    }

    private static string? SanitizeGenerationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();
        if (trimmed.Length < MinTokenLength || trimmed.Length > MaxTokenLength)
        {
            return null;
        }

        foreach (var character in trimmed)
        {
            if (!char.IsLetterOrDigit(character) && character != '-' && character != '_')
            {
                return null;
            }
        }

        return trimmed;
    }

    private static string? SanitizeIcao(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        var normalized = icao.Trim().ToUpperInvariant();
        if (normalized.Length != 4)
        {
            return null;
        }

        foreach (var character in normalized)
        {
            if (!char.IsLetterOrDigit(character))
            {
                return null;
            }
        }

        return normalized;
    }

    private static async Task<string> ReadBoundedResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new InvalidOperationException("Testing map response was too large.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        var totalBytes = 0;

        while (true)
        {
            var read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > MaxResponseBytes)
            {
                throw new InvalidOperationException("Testing map response was too large.");
            }

            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private sealed class TestingModeResponse
    {
        public string? Token { get; set; }
        public string? Icao { get; set; }
        public string? SupportsXml { get; set; }
        public string? RemovalsJson { get; set; }
        public string? BarsXml { get; set; }
    }
}
