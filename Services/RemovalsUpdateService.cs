using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Application;
using BARS_Client_V2.Infrastructure.Diagnostics;
using BARS_Client_V2.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace BARS_Client_V2.Services;

/// <summary>
/// Service responsible for checking and downloading removals packages from the CDN.
/// Compares ETags to detect changes and automatically updates the bars-removals package.
/// </summary>
public sealed class RemovalsUpdateService
{
    private const string Msfs2020RemovalsUrl = "https://dev-cdn.stopbars.com/packages/bars-removals-2020.zip";
    private const string Msfs2024RemovalsUrl = "https://dev-cdn.stopbars.com/packages/bars-removals-2024.zip";
    private const string RemovalsFolderName = "bars-removals";
    private const string InstallerSettingsFilename = "settings.json";
    private static readonly Random CacheBusterRandom = new();

    private readonly HttpClient _httpClient;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<RemovalsUpdateService> _logger;
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public RemovalsUpdateService(IHttpClientFactory httpClientFactory, ISettingsStore settingsStore, ILogger<RemovalsUpdateService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settingsStore = settingsStore;
        _logger = logger;
    }

    /// <summary>
    /// Result of checking and updating removals packages.
    /// </summary>
    public sealed record RemovalsUpdateResult(ClientSettings Settings, HashSet<string> UpdatedSimulators);

    /// <summary>
    /// Check both MSFS 2020 and MSFS 2024 removals for updates and download if needed.
    /// </summary>
    /// <param name="currentSettings">Current client settings containing stored ETags</param>
    /// <returns>Updated ClientSettings with new ETags and a set of simulators that were updated</returns>
    public async Task<RemovalsUpdateResult> CheckAndUpdateRemovalsAsync(ClientSettings currentSettings)
    {
        StartupTrace.Write("RemovalsUpdateService.CheckAndUpdateRemovalsAsync enter");
        var updatedSims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await _updateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var newMsfs2020Etag = currentSettings.Msfs2020RemovalsEtag;
            var newMsfs2024Etag = currentSettings.Msfs2024RemovalsEtag;

            var msfs2020Path = TryResolveCommunityPath("msfs2020");
            StartupTrace.Write($"MSFS 2020 community path: {msfs2020Path ?? "(null)"}");
            if (!string.IsNullOrWhiteSpace(msfs2020Path))
            {
                var result = await CheckAndUpdateSingleSimulatorAsync(
                    "msfs2020",
                    Msfs2020RemovalsUrl,
                    currentSettings.Msfs2020RemovalsEtag,
                    msfs2020Path).ConfigureAwait(false);
                if (result.Updated)
                {
                    newMsfs2020Etag = result.NewEtag;
                    updatedSims.Add("msfs2020");
                }
            }
            else
            {
                _logger.LogInformation("MSFS 2020 community path not configured, skipping removals check");
            }

            var msfs2024Path = TryResolveCommunityPath("msfs2024");
            StartupTrace.Write($"MSFS 2024 community path: {msfs2024Path ?? "(null)"}");
            if (!string.IsNullOrWhiteSpace(msfs2024Path))
            {
                var result = await CheckAndUpdateSingleSimulatorAsync(
                    "msfs2024",
                    Msfs2024RemovalsUrl,
                    currentSettings.Msfs2024RemovalsEtag,
                    msfs2024Path).ConfigureAwait(false);
                if (result.Updated)
                {
                    newMsfs2024Etag = result.NewEtag;
                    updatedSims.Add("msfs2024");
                }
            }
            else
            {
                _logger.LogInformation("MSFS 2024 community path not configured, skipping removals check");
            }

            if (newMsfs2020Etag != currentSettings.Msfs2020RemovalsEtag ||
                newMsfs2024Etag != currentSettings.Msfs2024RemovalsEtag)
            {
                var updatedSettings = currentSettings with
                {
                    Msfs2020RemovalsEtag = newMsfs2020Etag,
                    Msfs2024RemovalsEtag = newMsfs2024Etag
                };
                await _settingsStore.SaveAsync(updatedSettings).ConfigureAwait(false);
                StartupTrace.Write("RemovalsUpdateService: ETags updated and saved");
                return new RemovalsUpdateResult(updatedSettings, updatedSims);
            }

            StartupTrace.Write("RemovalsUpdateService.CheckAndUpdateRemovalsAsync exit (no changes)");
            return new RemovalsUpdateResult(currentSettings, updatedSims);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for removals updates");
            StartupTrace.Write($"RemovalsUpdateService error: {ex.Message}");
            return new RemovalsUpdateResult(currentSettings, updatedSims);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task<(bool Updated, string? NewEtag)> CheckAndUpdateSingleSimulatorAsync(
        string simulator,
        string url,
        string? storedEtag,
        string communityPath)
    {
        try
        {
            var cacheBustedUrl = AppendCacheBuster(url);
            _logger.LogInformation("Checking removals for {Simulator} at {Url}", simulator, cacheBustedUrl);
            StartupTrace.Write($"CheckAndUpdateSingleSimulatorAsync: simulator={simulator}, communityPath={communityPath}, storedEtag={storedEtag}");

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, cacheBustedUrl);
            AddNoCacheHeaders(headRequest);
            using var headResponse = await _httpClient.SendAsync(headRequest).ConfigureAwait(false);

            if (!headResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("HEAD request failed for {Simulator}: {StatusCode}", simulator, headResponse.StatusCode);
                return (false, storedEtag);
            }

            var currentEtag = headResponse.Headers.ETag?.Tag;
            if (string.IsNullOrWhiteSpace(currentEtag))
            {
                if (headResponse.Headers.TryGetValues("ETag", out var etagValues))
                {
                    currentEtag = string.Join("", etagValues);
                }
            }

            _logger.LogInformation("{Simulator} current ETag: {CurrentEtag}, stored ETag: {StoredEtag}",
                simulator, currentEtag, storedEtag);
            StartupTrace.Write($"{simulator} raw ETags - current: '{currentEtag}', stored: '{storedEtag}'");

            var normalizedCurrent = NormalizeEtag(currentEtag);
            var normalizedStored = NormalizeEtag(storedEtag);

            StartupTrace.Write($"{simulator} normalized ETags - current: '{normalizedCurrent}', stored: '{normalizedStored}'");

            if (string.Equals(normalizedCurrent, normalizedStored, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("{Simulator} removals are up to date", simulator);
                StartupTrace.Write($"{simulator} removals are up to date (ETags match)");
                return (false, storedEtag);
            }

            _logger.LogInformation("{Simulator} removals have changed, downloading update...", simulator);
            StartupTrace.Write($"{simulator} removals ETag changed from {normalizedStored} to {normalizedCurrent}, starting download");
            var success = await DownloadAndInstallRemovalsAsync(simulator, url, communityPath).ConfigureAwait(false);

            if (success)
            {
                _logger.LogInformation("{Simulator} removals updated successfully", simulator);
                StartupTrace.Write($"{simulator} removals updated successfully");
                return (true, currentEtag);
            }

            _logger.LogWarning("{Simulator} removals download/install returned false", simulator);
            StartupTrace.Write($"{simulator} removals download/install failed (returned false)");
            return (false, storedEtag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking/updating removals for {Simulator}", simulator);
            StartupTrace.Write($"CheckAndUpdateSingleSimulatorAsync EXCEPTION for {simulator}: {ex.GetType().Name}: {ex.Message}");
            StartupTrace.Write($"Stack trace: {ex.StackTrace}");
            return (false, storedEtag);
        }
    }

    private async Task<bool> DownloadAndInstallRemovalsAsync(string simulator, string url, string communityPath)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tempDir = Path.Combine(root, "BARS", "Installer", "TEMP");
        Directory.CreateDirectory(tempDir);

        var tempZipPath = Path.Combine(tempDir, $"bars-removals-{simulator}.zip");
        var removalsDestPath = Path.Combine(communityPath, RemovalsFolderName);

        StartupTrace.Write($"DownloadAndInstallRemovalsAsync: simulator={simulator}, tempZipPath={tempZipPath}, removalsDestPath={removalsDestPath}");

        try
        {
            var cacheBustedUrl = AppendCacheBuster(url);
            _logger.LogInformation("Downloading {Simulator} removals to {TempPath}", simulator, tempZipPath);
            StartupTrace.Write($"Starting download from {cacheBustedUrl}");
            using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, cacheBustedUrl);
            AddNoCacheHeaders(downloadRequest);
            using (var response = await _httpClient.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                StartupTrace.Write($"Download response status: {response.StatusCode}");
                response.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs).ConfigureAwait(false);
            }

            var downloadedSize = new FileInfo(tempZipPath).Length;
            _logger.LogInformation("Downloaded {Simulator} removals ({Size} bytes)", simulator, downloadedSize);
            StartupTrace.Write($"Downloaded {downloadedSize} bytes to {tempZipPath}");

            if (Directory.Exists(removalsDestPath))
            {
                _logger.LogInformation("Deleting existing removals folder: {Path}", removalsDestPath);
                StartupTrace.Write($"Deleting existing folder: {removalsDestPath}");
                try
                {
                    Directory.Delete(removalsDestPath, recursive: true);
                    StartupTrace.Write("Existing folder deleted successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete existing removals folder, attempting to overwrite");
                    StartupTrace.Write($"Failed to delete existing folder: {ex.Message}");
                }
            }

            _logger.LogInformation("Extracting removals to {DestPath}", communityPath);
            StartupTrace.Write($"Extracting zip to {communityPath}");
            ZipFile.ExtractToDirectory(tempZipPath, communityPath, overwriteFiles: true);
            StartupTrace.Write("Extraction completed successfully");

            _logger.LogInformation("{Simulator} removals installed successfully", simulator);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download/install removals for {Simulator}", simulator);
            StartupTrace.Write($"DownloadAndInstallRemovalsAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            StartupTrace.Write($"Stack trace: {ex.StackTrace}");
            return false;
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try
                {
                    File.Delete(tempZipPath);
                    _logger.LogDebug("Deleted temp file: {TempPath}", tempZipPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file: {TempPath}", tempZipPath);
                }
            }
        }
    }

    private static string? NormalizeEtag(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
            return null;
        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        normalized = normalized.Trim('"');
        return normalized;
    }

    private static void AddNoCacheHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store, must-revalidate");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        request.Headers.TryAddWithoutValidation("Expires", "0");
    }

    private static string AppendCacheBuster(string url)
    {
        var token = RandomCacheBusterToken(3);
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}s={token}";
    }

    private static string RandomCacheBusterToken(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var result = new char[length];
        lock (CacheBusterRandom)
        {
            for (var i = 0; i < length; i++)
            {
                result[i] = chars[CacheBusterRandom.Next(chars.Length)];
            }
        }
        return new string(result);
    }

    private static string? TryResolveCommunityPath(string simulator)
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var settingsPath = Path.Combine(root, "BARS", "Installer", InstallerSettingsFilename);
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            var json = File.ReadAllText(settingsPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var settings = JsonSerializer.Deserialize<InstallerSettings>(json, options);
            var pilotClient = settings?.PilotClient;
            if (pilotClient == null)
            {
                return null;
            }

            return simulator.Equals("msfs2024", StringComparison.OrdinalIgnoreCase)
                ? pilotClient.Msfs2024Path
                : pilotClient.Msfs2020Path;
        }
        catch
        {
            return null;
        }
    }

    private sealed class InstallerSettings
    {
        [JsonPropertyName("Pilot-Client")]
        public InstallerClientSettings? PilotClient { get; set; }
    }

    private sealed class InstallerClientSettings
    {
        [JsonPropertyName("msfs2020Path")]
        public string? Msfs2020Path { get; set; }

        [JsonPropertyName("msfs2024Path")]
        public string? Msfs2024Path { get; set; }
    }
}
