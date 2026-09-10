using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace BARS_Client_V2.Infrastructure.Networking;

internal sealed record ApprovedContributionMetadata(
    string Id,
    string AirportIcao,
    string PackageName,
    string? Simulator,
    string? ArtifactIdentity,
    string? ArtifactGenerationId,
    string? RemovalArtifactKey,
    string? BarsArtifactKey);

internal static class ApprovedContributionsCache
{
    private const string MetadataUrl =
        "https://v2.stopbars.com/contributions?projection=metadata";
    private const string LegacySimpleUrl =
        "https://v2.stopbars.com/contributions?status=approved&simple=true";
    private const int MaxMetadataBytes = 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static IReadOnlyList<ApprovedContributionMetadata>? _lastKnownGood;
    private static DateTime _lastSuccessfulFetchUtc;
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

    public static async Task<IReadOnlyList<ApprovedContributionMetadata>> GetAsync(
        HttpClient client,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && IsFresh())
        {
            return _lastKnownGood!;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && IsFresh())
            {
                return _lastKnownGood!;
            }

            MetadataResponse? payload;
            try
            {
                payload = await FetchAsync(client, MetadataUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (
                error is ApprovedMetadataTooLargeException or HttpRequestException or JsonException)
            {
                // Compatibility with Core versions that ignore projection=metadata
                // and would otherwise return every contribution's submitted XML.
                payload = await FetchAsync(client, LegacySimpleUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (payload?.Contributions == null)
            {
                throw new InvalidOperationException(
                    "The approved-contributions metadata response was invalid.");
            }

            _lastKnownGood = payload.Contributions
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Id) &&
                    !string.IsNullOrWhiteSpace(item.AirportIcao) &&
                    !string.IsNullOrWhiteSpace(item.PackageName))
                .ToArray();
            _lastSuccessfulFetchUtc = DateTime.UtcNow;
            return _lastKnownGood;
        }
        catch when (!cancellationToken.IsCancellationRequested && _lastKnownGood != null)
        {
            return _lastKnownGood;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsFresh() =>
        _lastKnownGood != null &&
        DateTime.UtcNow - _lastSuccessfulFetchUtc < FreshnessWindow;

    private static async Task<MetadataResponse?> FetchAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxMetadataBytes)
        {
            throw new ApprovedMetadataTooLargeException();
        }

        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (bounded.Length + read > MaxMetadataBytes)
            {
                throw new ApprovedMetadataTooLargeException();
            }
            bounded.Write(buffer, 0, read);
        }
        bounded.Position = 0;
        return await JsonSerializer.DeserializeAsync<MetadataResponse>(
                bounded,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record MetadataResponse(
        List<ApprovedContributionMetadata> Contributions);

    private sealed class ApprovedMetadataTooLargeException : Exception
    {
    }
}
