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
    private const string Msfs2020RemovalsUrl = "https://cdn.stopbars.com/packages/bars-removals-2020.zip";
    private const string Msfs2024RemovalsUrl = "https://cdn.stopbars.com/packages/bars-removals-2024.zip";
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
    public sealed record RemovalsUpdateResult(ClientSettings Settings, HashSet<string> UpdatedSimulators)
    {
        public HashSet<string> FailedSimulators { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check both MSFS 2020 and MSFS 2024 removals for updates and download if needed.
    /// </summary>
    /// <param name="currentSettings">Current client settings containing stored ETags</param>
    /// <returns>Updated ClientSettings with new ETags and a set of simulators that were updated</returns>
    public async Task<RemovalsUpdateResult> CheckAndUpdateRemovalsAsync(
        ClientSettings currentSettings,
        CancellationToken cancellationToken = default)
    {
        StartupTrace.Write("RemovalsUpdateService.CheckAndUpdateRemovalsAsync enter");
        var updatedSims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedSims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            currentSettings = await _settingsStore.LoadAsync().ConfigureAwait(false);
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
                    msfs2020Path,
                    cancellationToken).ConfigureAwait(false);
                if (result.Failed) failedSims.Add("msfs2020");
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
                    msfs2024Path,
                    cancellationToken).ConfigureAwait(false);
                if (result.Failed) failedSims.Add("msfs2024");
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
                var updatedSettings = await _settingsStore.UpdateAsync(latest => latest with
                {
                    Msfs2020RemovalsEtag = updatedSims.Contains("msfs2020") ? newMsfs2020Etag : latest.Msfs2020RemovalsEtag,
                    Msfs2024RemovalsEtag = updatedSims.Contains("msfs2024") ? newMsfs2024Etag : latest.Msfs2024RemovalsEtag
                }).ConfigureAwait(false);
                StartupTrace.Write("RemovalsUpdateService: ETags updated and saved");
                return new RemovalsUpdateResult(updatedSettings, updatedSims) { FailedSimulators = failedSims };
            }

            StartupTrace.Write("RemovalsUpdateService.CheckAndUpdateRemovalsAsync exit (no changes)");
            return new RemovalsUpdateResult(currentSettings, updatedSims) { FailedSimulators = failedSims };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for removals updates");
            StartupTrace.Write($"RemovalsUpdateService error: {ex.Message}");
            throw;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task<(bool Updated, string? NewEtag, bool Failed)> CheckAndUpdateSingleSimulatorAsync(
        string simulator,
        string url,
        string? storedEtag,
        string communityPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var cacheBustedUrl = AppendCacheBuster(url);
            _logger.LogInformation("Checking removals for {Simulator} at {Url}", simulator, cacheBustedUrl);
            StartupTrace.Write($"CheckAndUpdateSingleSimulatorAsync: simulator={simulator}, communityPath={communityPath}, storedEtag={storedEtag}");

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, cacheBustedUrl);
            AddNoCacheHeaders(headRequest);
            using var headResponse = await _httpClient.SendAsync(headRequest, cancellationToken).ConfigureAwait(false);

            if (!headResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("HEAD request failed for {Simulator}: {StatusCode}", simulator, headResponse.StatusCode);
                return (false, storedEtag, true);
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

            var removalsDestPath = Path.Combine(communityPath, RemovalsFolderName);
            if (Directory.Exists(removalsDestPath) &&
                normalizedCurrent != null &&
                string.Equals(normalizedCurrent, normalizedStored, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("{Simulator} removals are up to date", simulator);
                StartupTrace.Write($"{simulator} removals are up to date (ETags match)");
                return (false, storedEtag, false);
            }

            _logger.LogInformation("{Simulator} removals have changed, downloading update...", simulator);
            StartupTrace.Write($"{simulator} removals ETag changed from {normalizedStored} to {normalizedCurrent}, starting download");
            var download = await DownloadAndInstallRemovalsAsync(
                simulator,
                cacheBustedUrl,
                communityPath,
                currentEtag,
                cancellationToken).ConfigureAwait(false);

            if (download.Success)
            {
                _logger.LogInformation("{Simulator} removals updated successfully", simulator);
                StartupTrace.Write($"{simulator} removals updated successfully");
                return (true, download.Etag ?? currentEtag, false);
            }

            _logger.LogWarning("{Simulator} removals download/install returned false", simulator);
            StartupTrace.Write($"{simulator} removals download/install failed (returned false)");
            return (false, storedEtag, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not RemovalPackageInstaller.RecoveryException)
        {
            _logger.LogError(ex, "Error checking/updating removals for {Simulator}", simulator);
            StartupTrace.Write($"CheckAndUpdateSingleSimulatorAsync EXCEPTION for {simulator}: {ex.GetType().Name}: {ex.Message}");
            StartupTrace.Write($"Stack trace: {ex.StackTrace}");
            return (false, storedEtag, true);
        }
    }

    private async Task<(bool Success, string? Etag)> DownloadAndInstallRemovalsAsync(
        string simulator,
        string downloadUrl,
        string communityPath,
        string? expectedEtag,
        CancellationToken cancellationToken)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tempDir = Path.Combine(root, "BARS", "Installer", "TEMP");
        Directory.CreateDirectory(tempDir);

        var operationId = Guid.NewGuid().ToString("N");
        var tempZipPath = Path.Combine(tempDir, $"bars-removals-{simulator}-{operationId}.zip");
        var removalsDestPath = Path.Combine(communityPath, RemovalsFolderName);
        var stageRoot = Path.Combine(communityPath, $".{RemovalsFolderName}-stage-{operationId}");
        var stagePackagePath = Path.Combine(stageRoot, RemovalsFolderName);
        var backupPath = Path.Combine(communityPath, $".{RemovalsFolderName}-backup-{operationId}");
        var previousMoved = false;
        var replacementInstalled = false;
        var removalGateHeld = false;

        StartupTrace.Write($"DownloadAndInstallRemovalsAsync: simulator={simulator}, tempZipPath={tempZipPath}, removalsDestPath={removalsDestPath}");

        try
        {
            _logger.LogInformation("Downloading {Simulator} removals to {TempPath}", simulator, tempZipPath);
            StartupTrace.Write($"Starting download from {downloadUrl}");
            using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            AddNoCacheHeaders(downloadRequest);
            if (!string.IsNullOrWhiteSpace(expectedEtag))
            {
                downloadRequest.Headers.TryAddWithoutValidation("If-Match", expectedEtag);
            }
            string? downloadedEtag;
            using (var response = await _httpClient.SendAsync(
                       downloadRequest,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                StartupTrace.Write($"Download response status: {response.StatusCode}");
                response.EnsureSuccessStatusCode();
                downloadedEtag = response.Headers.ETag?.Tag;
                if (!string.IsNullOrWhiteSpace(expectedEtag) &&
                    !string.IsNullOrWhiteSpace(downloadedEtag) &&
                    !string.Equals(
                        NormalizeEtag(expectedEtag),
                        NormalizeEtag(downloadedEtag),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The removals package changed between the update check and download.");
                }
                await using var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            var downloadedSize = new FileInfo(tempZipPath).Length;
            if (downloadedSize == 0)
            {
                throw new InvalidDataException("The downloaded removals archive was empty.");
            }
            _logger.LogInformation("Downloaded {Simulator} removals ({Size} bytes)", simulator, downloadedSize);
            StartupTrace.Write($"Downloaded {downloadedSize} bytes to {tempZipPath}");

            ValidateArchive(tempZipPath);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stageRoot);
            ZipFile.ExtractToDirectory(tempZipPath, stageRoot, overwriteFiles: false);
            var stagedRemovalsRoot = Path.Combine(stagePackagePath, "Scenery", "removals");
            if (!Directory.Exists(stagedRemovalsRoot))
            {
                throw new InvalidDataException(
                    $"The {simulator} removals archive does not contain {RemovalsFolderName}/Scenery/removals.");
            }

            await RemovalFileAccess.MsfsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            removalGateHeld = true;
            if (Directory.Exists(removalsDestPath))
            {
                _logger.LogInformation("Moving existing removals folder aside: {Path}", removalsDestPath);
                try
                {
                    Directory.Move(removalsDestPath, backupPath);
                    previousMoved = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    StartupTrace.Write($"Package directory rename failed ({ex.Message}); replacing files with rollback");
                    for (var attempt = 0; ; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            RemovalPackageInstaller.ReplaceFiles(stagePackagePath, removalsDestPath, backupPath);
                            replacementInstalled = true;
                            break;
                        }
                        catch (Exception fileError) when (attempt < 2 && fileError is IOException or UnauthorizedAccessException)
                        {
                            StartupTrace.Write($"Removal file replacement failed; retrying attempt {attempt + 2}: {fileError.Message}");
                            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }

            if (!replacementInstalled) Directory.Move(stagePackagePath, removalsDestPath);
            replacementInstalled = true;
            StartupTrace.Write("Staged removals package installed successfully");

            if (Directory.Exists(backupPath))
            {
                try
                {
                    Directory.Delete(backupPath, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete the previous removals backup at {Path}", backupPath);
                }
            }

            _logger.LogInformation("{Simulator} removals installed successfully", simulator);
            return (true, downloadedEtag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (previousMoved && !replacementInstalled && !Directory.Exists(removalsDestPath) && Directory.Exists(backupPath))
            {
                try
                {
                    Directory.Move(backupPath, removalsDestPath);
                    previousMoved = false;
                }
                catch (Exception rollbackException)
                {
                    _logger.LogCritical(
                        rollbackException,
                        "Failed to restore the previous removals package from {BackupPath}",
                        backupPath);
                    throw new RemovalPackageInstaller.RecoveryException(
                        $"Removal update rollback failed. Backups retained at {backupPath}.",
                        new[] { ex, rollbackException });
                }
            }
            _logger.LogError(ex, "Failed to download/install removals for {Simulator}", simulator);
            StartupTrace.Write($"DownloadAndInstallRemovalsAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            StartupTrace.Write($"Stack trace: {ex.StackTrace}");
            if (ex is RemovalPackageInstaller.RecoveryException) throw;
            return (false, null);
        }
        finally
        {
            if (removalGateHeld)
            {
                RemovalFileAccess.MsfsGate.Release();
            }
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
            if (Directory.Exists(stageRoot))
            {
                try { Directory.Delete(stageRoot, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete removals staging folder {Path}", stageRoot); }
            }
        }
    }

    private static void ValidateArchive(string archivePath)
    {
        const long maxExpandedBytes = 2L * 1024 * 1024 * 1024;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("The removals archive contained no files.");
        }

        long expandedBytes = 0;
        var expectedPrefix = RemovalsFolderName + "/";
        var hasPackageContent = false;
        foreach (var entry in archive.Entries)
        {
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > maxExpandedBytes)
            {
                throw new InvalidDataException("The removals archive exceeds the expanded-size safety limit.");
            }
            if (entry.FullName.Replace('\\', '/').StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                hasPackageContent = true;
            }
        }

        if (!hasPackageContent)
        {
            throw new InvalidDataException($"The removals archive does not contain a {RemovalsFolderName} package.");
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
