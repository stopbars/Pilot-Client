using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal sealed class XPlaneLocalRemovalsService
{
    private const string PackageName = "BARS X-Plane Removals";
    private const string Schema = "bars-xplane-removals/v2";
    private const string LegacySchema = "bars-xplane-removals/v1";
    private const int MaxRemovalArtifactBytes = 8 * 1024 * 1024;
    private static readonly Uri CoreApiBase = new("https://v2.stopbars.com/");
    private static readonly HashSet<int> NodeCodes = [111, 112, 113, 114, 115, 116];
    private readonly HttpClient _httpClient;
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly AsyncLocal<AirportReadSession?> ReadSession = new();

    internal static IDisposable BeginReadSession(IEnumerable<string> airports)
    {
        var session = new AirportReadSession(airports, ReadSession.Value);
        ReadSession.Value = session;
        return session;
    }

    internal static void InvalidateReadCache(string path) => ReadSession.Value?.Invalidate(path);
    internal static void ReleaseCachedReads() => ReadSession.Value?.Clear();
    private readonly Dictionary<string, DownloadedArtifact> _artifactCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public XPlaneLocalRemovalsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<bool> ApplyAsync(
        string icao,
        string packageName,
        bool enabled,
        string? removalArtifactKey = null,
        string? artifactIdentity = null,
        string? artifactGenerationId = null,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveXPlaneRoot()
            ?? throw new InvalidOperationException(
                "X-Plane 12 could not be found. Start X-Plane or configure its install path in the BARS Installer.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedIcao = NormalizeIcao(icao);
            if (IsXPlaneProcessRunning())
            {
                // Every enabled request is freshness-sensitive. Package identity and
                // immutable artifact generations can change while the installed block
                // remains byte-for-byte identical, so it must be retried after exit.
                return true;
            }
            XPlanePatchTransaction.Recover(root, GetStateRoot(root));
            if (!enabled || string.IsNullOrWhiteSpace(packageName))
            {
                return await RestoreAirportAsync(root, normalizedIcao, cancellationToken)
                    .ConfigureAwait(false);
            }

            var artifact = await DownloadArtifactAsync(
                    normalizedIcao,
                    packageName,
                    removalArtifactKey,
                    artifactIdentity,
                    artifactGenerationId,
                    cancellationToken)
                .ConfigureAwait(false);
            return await ApplyArtifactAsync(
                root,
                normalizedIcao,
                artifact.Artifact,
                packageName,
                artifact.Identity,
                artifact.GenerationId,
                artifact.Sha256,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IsStateApplied(string icao, bool enabled)
    {
        var root = ResolveXPlaneRoot();
        if (root == null)
        {
            return false;
        }

        if (XPlanePatchTransaction.HasPending(GetStateRoot(root))) return false;
        var normalizedIcao = NormalizeIcao(icao);
        var state = LoadPatchState(root);
        var applied = state.Airports.FirstOrDefault(item =>
            item.Icao.Equals(normalizedIcao, StringComparison.OrdinalIgnoreCase));
        var legacyBlockPath = Path.Combine(
            root,
            "Custom Scenery",
            PackageName,
            ".bars",
            normalizedIcao + ".apt");
        if (!enabled)
        {
            return applied == null && !XPlaneDsfRemovals.HasAirport(GetStateRoot(root), normalizedIcao) && !File.Exists(legacyBlockPath);
        }

        var hasDsf = XPlaneDsfRemovals.HasAirport(GetStateRoot(root), normalizedIcao);
        if (hasDsf && !XPlaneDsfRemovals.IsApplied(root, GetStateRoot(root), normalizedIcao)) return false;
        if (applied == null)
        {
            return hasDsf;
        }

        var sourcePath = ResolveStateSourcePath(root, applied.SourceRelativePath);
        var current = ReadAirportBlockAsync(sourcePath, normalizedIcao, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return current != null &&
               BlockHash(current).Equals(applied.PatchedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> RestoreTestingArtifactsAsync(CancellationToken cancellationToken = default)
    {
        var root = ResolveXPlaneRoot();
        if (root == null) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recoveryPending = XPlanePatchTransaction.HasPending(GetStateRoot(root));
            if (recoveryPending)
            {
                if (IsXPlaneProcessRunning()) return true;
                XPlanePatchTransaction.Recover(root, GetStateRoot(root));
            }
            var testingAirports = LoadPatchState(root).Airports
                .Where(item => item.PackageName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Icao)
                .Concat(XPlaneDsfRemovals.TestingAirports(GetStateRoot(root)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (testingAirports.Length == 0) return recoveryPending;
            if (IsXPlaneProcessRunning()) return true;

            var changed = recoveryPending;
            foreach (var airport in testingAirports)
            {
                changed |= await RestoreAirportAsync(root, airport, cancellationToken).ConfigureAwait(false);
            }
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ApplyTestingArtifactAsync(
        string icao,
        string removalJson,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveXPlaneRoot();
        if (root == null || string.IsNullOrWhiteSpace(removalJson))
        {
            return false;
        }
        if (Encoding.UTF8.GetByteCount(removalJson) > MaxRemovalArtifactBytes)
        {
            throw new InvalidOperationException(
                $"The X-Plane removals artifact exceeds the {MaxRemovalArtifactBytes / (1024 * 1024)} MB safety limit.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsXPlaneProcessRunning())
            {
                throw new InvalidOperationException(
                    "Close X-Plane before applying testing removals; its scenery files are currently in use.");
            }
            var normalizedIcao = NormalizeIcao(icao);
            var artifact = JsonSerializer.Deserialize<RemovalArtifact>(
                removalJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (artifact == null || (artifact.Schema != Schema && artifact.Schema != LegacySchema) ||
                NormalizeIcao(artifact.Icao) != normalizedIcao)
            {
                return false;
            }
            ValidateArtifact(artifact);

            var artifactJson = JsonSerializer.SerializeToUtf8Bytes(
                artifact,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await ApplyArtifactAsync(
                root,
                normalizedIcao,
                artifact,
                "Testing",
                "testing",
                null,
                Convert.ToHexString(SHA256.HashData(artifactJson)).ToLowerInvariant(),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ApplyArtifactAsync(
        string root, string icao, RemovalArtifact artifact, string packageName,
        string artifactIdentity, string? artifactGenerationId, string artifactSha256, CancellationToken cancellationToken)
    {
        ValidateArtifact(artifact);
        var stateRoot = GetStateRoot(root);
        XPlanePatchTransaction.Recover(root, stateRoot);
        var applyDsf = XPlaneDsfRemovals.Prepare(root, stateRoot, icao, packageName, artifact.DsfSelectors);
        return await XPlanePatchTransaction.RunAsync(root, stateRoot, async () =>
        {
            var changed = await ApplyAptArtifactAsync(root, icao, artifact, packageName, artifactIdentity, artifactGenerationId, artifactSha256, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            applyDsf.Apply();
            return changed || applyDsf.Changed;
        }).ConfigureAwait(false);
    }

    private static async Task<bool> RestoreAirportAsync(string root, string icao, CancellationToken cancellationToken)
    {
        var stateRoot = GetStateRoot(root);
        XPlanePatchTransaction.Recover(root, stateRoot);
        var restoreDsf = XPlaneDsfRemovals.Prepare(root, stateRoot, icao, "", []);
        return await XPlanePatchTransaction.RunAsync(root, stateRoot, async () =>
        {
            var changed = await RestoreAptAirportAsync(root, icao, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            restoreDsf.Apply();
            return changed || restoreDsf.Changed;
        }).ConfigureAwait(false);
    }

    private async Task<bool> ApplyAptArtifactAsync(
        string root,
        string icao,
        RemovalArtifact artifact,
        string packageName,
        string artifactIdentity,
        string? artifactGenerationId,
        string artifactSha256,
        CancellationToken cancellationToken)
    {
        ValidateArtifact(artifact);
        if (artifact.Selectors.Count == 0)
        {
            return await RestoreAptAirportAsync(root, icao, cancellationToken).ConfigureAwait(false);
        }

        var state = LoadPatchState(root);
        var existing = state.Airports.FirstOrDefault(item =>
            item.Icao.Equals(icao, StringComparison.OrdinalIgnoreCase));

        var activeSource = await FindActiveAirportSourceAsync(root, icao, cancellationToken).ConfigureAwait(false)
            ?? throw new XPlaneRemovalMismatchException($"{icao} was not found in active X-Plane scenery.");
        var sourcePath = activeSource.Path;
        var previousSourcePath = existing == null
            ? null
            : ResolveStateSourcePath(root, existing.SourceRelativePath);
        var sourceChanged = previousSourcePath != null &&
                            !Path.GetFullPath(previousSourcePath).Equals(
                                Path.GetFullPath(sourcePath),
                                StringComparison.OrdinalIgnoreCase);
        var existingForSource = sourceChanged ? null : existing;

        var sourceBackup = await EnsureFullSourceBackupAsync(
                root,
                state,
                sourcePath,
                cancellationToken)
            .ConfigureAwait(false);
        var fullBackupPath = ResolveBackupPath(root, sourceBackup.BackupFile);
        var original = await ReadAirportBlockAsync(fullBackupPath, icao, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{icao} was not found in its verified full-file backup; BARS will not modify active scenery.");
        var originalHash = BlockHash(original);
        if (existingForSource != null &&
            !originalHash.Equals(existingForSource.OriginalSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{icao}'s full-file backup does not match its saved removal state.");
        }

        var backupFile = existingForSource?.BackupFile ??
                         $"{icao}-{originalHash[..16]}.apt";
        await WriteAtomicallyIfChangedAsync(
                ResolveBackupPath(root, backupFile),
                NormalizeBlock(original),
                cancellationToken)
            .ConfigureAwait(false);

        var current = await ReadAirportBlockAsync(sourcePath, icao, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{icao} disappeared from its active apt.dat.");
        if (existingForSource != null &&
            !MatchesPatchedBlock(current, existingForSource) &&
            !RampIndependentHash(current).Equals(RampIndependentHash(original), StringComparison.OrdinalIgnoreCase))
        {
            _ = PatchAirportBlock(current, artifact);
            var previousArtifact = artifact;
            var previousArtifactVerified = false;
            var reproducesPreviousPatch = false;
            try { reproducesPreviousPatch = MatchesPatchedBlock(PatchAirportBlock(original, artifact), existingForSource); }
            catch (XPlaneRemovalMismatchException) { }
            if (!reproducesPreviousPatch)
            {
                if (string.IsNullOrWhiteSpace(existingForSource.ArtifactGenerationId) ||
                    string.IsNullOrWhiteSpace(existingForSource.ArtifactIdentity))
                    throw new XPlaneRemovalMismatchException("The previous removal artifact cannot be verified. The saved backup was preserved.");
                var previous = await DownloadArtifactAsync(icao, existingForSource.PackageName,
                    $"ContributionArtifacts/{icao}/{existingForSource.ArtifactIdentity}/xplane/{existingForSource.ArtifactGenerationId}/removals.json",
                    existingForSource.ArtifactIdentity, existingForSource.ArtifactGenerationId, cancellationToken)
                    .ConfigureAwait(false);
                if (!previous.Sha256.Equals(existingForSource.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
                    throw new XPlaneRemovalMismatchException("The previous removal artifact failed verification. The saved backup was preserved.");
                previousArtifact = previous.Artifact;
                previousArtifactVerified = true;
            }
            ValidateReplacementBaseline(original, current, artifact, previousArtifact, existingForSource, previousArtifactVerified);
            // Replace only this airport in a new copy of the verified baseline.
            // Other airports may still have active patches sharing the same source.
            var refreshedFile = $"source-refresh-{Guid.NewGuid():N}.apt.dat";
            var refreshedPath = ResolveBackupPath(root, refreshedFile);
            await CopyFileAsync(fullBackupPath, refreshedPath, cancellationToken).ConfigureAwait(false);
            await ReplaceAirportBlockAtomicallyAsync(refreshedPath, icao, current, cancellationToken)
                .ConfigureAwait(false);
            state.SourceBackups.Remove(sourceBackup);
            state.SourceBackups.Add(new SourceFileBackup
            {
                SourceRelativePath = sourceBackup.SourceRelativePath,
                BackupFile = refreshedFile,
                OriginalSha256 = await FileSha256Async(refreshedPath, cancellationToken).ConfigureAwait(false)
            });
            original = current;
            backupFile = $"{icao}-{BlockHash(original)[..16]}.apt";
            await WriteAtomicallyIfChangedAsync(ResolveBackupPath(root, backupFile),
                NormalizeBlock(original), cancellationToken).ConfigureAwait(false);
        }

        var patched = PreserveRampMetadata(PatchAirportBlock(original, artifact), current);
        var patchedHash = BlockHash(patched);
        var targetChanged = !BlockHash(current).Equals(patchedHash, StringComparison.OrdinalIgnoreCase);

        string? previousSourceCurrent = null;
        string? previousSourceOriginal = null;
        var previousSourceWasPatched = false;
        if (sourceChanged && existing != null && previousSourcePath != null)
        {
            previousSourceCurrent = await ReadAirportBlockAsync(
                    previousSourcePath,
                    icao,
                    cancellationToken)
                .ConfigureAwait(false);
            if (previousSourceCurrent == null)
            {
                throw new InvalidOperationException(
                    $"{icao} disappeared from its previous active apt.dat; package selection was not changed.");
            }
            var previousCurrentHash = BlockHash(previousSourceCurrent);
            previousSourceWasPatched = MatchesPatchedBlock(previousSourceCurrent, existing);
            if (!previousSourceWasPatched &&
                !previousCurrentHash.Equals(existing.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new XPlaneRemovalMismatchException(
                    $"{icao}'s previous active apt.dat changed outside BARS; package selection was not changed.");
            }

            var previousBackup = await EnsureFullSourceBackupAsync(
                    root,
                    state,
                    previousSourcePath,
                    cancellationToken)
                .ConfigureAwait(false);
            previousSourceOriginal = await ReadAirportBlockAsync(
                    ResolveBackupPath(root, previousBackup.BackupFile),
                    icao,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"{icao} was not found in its previous verified backup.");
            if (!BlockHash(previousSourceOriginal).Equals(
                    existing.OriginalSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{icao}'s previous source backup does not match its saved removal state.");
            }
            previousSourceOriginal = PreserveRampMetadata(previousSourceOriginal, previousSourceCurrent);
        }

        var previousState = existing == null ? null : existing.Copy();
        existing ??= new AppliedAirportPatch { Icao = icao };
        existing.PackageName = packageName;
        existing.SourceRelativePath = Path.GetRelativePath(root, sourcePath);
        existing.BackupFile = backupFile;
        existing.OriginalSha256 = BlockHash(original);
        existing.PatchedSha256 = patchedHash;
        existing.PatchedRampIndependentSha256 = RampIndependentHash(patched);
        existing.ArtifactIdentity = artifactIdentity;
        existing.ArtifactGenerationId = artifactGenerationId;
        existing.ArtifactSha256 = artifactSha256;
        if (!state.Airports.Contains(existing))
        {
            state.Airports.Add(existing);
        }

        try
        {
            if (sourceChanged && previousSourceWasPatched)
            {
                await ReplaceAirportBlockAtomicallyAsync(
                        previousSourcePath!,
                        icao,
                        previousSourceOriginal!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            if (targetChanged)
            {
                await ReplaceAirportBlockAtomicallyAsync(
                        sourcePath,
                        icao,
                        patched,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            await SavePatchStateAsync(root, state, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Once a source file has changed, rollback must finish even if the
            // caller cancelled during shutdown or switched operations.
            var rollbackToken = CancellationToken.None;
            if (targetChanged)
            {
                await ReplaceAirportBlockAtomicallyAsync(
                        sourcePath,
                        icao,
                        current,
                        rollbackToken)
                    .ConfigureAwait(false);
            }
            if (sourceChanged && previousSourceWasPatched && previousSourceCurrent != null)
            {
                await ReplaceAirportBlockAtomicallyAsync(
                        previousSourcePath!,
                        icao,
                        previousSourceCurrent,
                        rollbackToken)
                    .ConfigureAwait(false);
            }
            if (previousState != null)
            {
                var index = state.Airports.IndexOf(existing);
                state.Airports[index] = previousState;
            }
            else
            {
                state.Airports.Remove(existing);
            }
            throw;
        }

        var changed = targetChanged || (sourceChanged && previousSourceWasPatched);
        changed |= await RemoveLegacyOverrideForAirportAsync(root, icao, cancellationToken)
            .ConfigureAwait(false);
        return changed;
    }

    private static void ValidateReplacementBaseline(
        string original, string current, RemovalArtifact artifact, RemovalArtifact previousArtifact, AppliedAirportPatch existing,
        bool previousArtifactVerified)
    {
        // Every selected source feature must still be pristine in the replacement.
        // Validate this first so outdated contributions report their missing selectors.
        _ = PatchAirportBlock(current, artifact);
        var previousPatched = PatchAirportBlock(original, previousArtifact);
        // Older patcher versions can serialize the same owned edits differently.
        // The caller verifies both the saved original and pinned artifact hashes.
        if (!MatchesPatchedBlock(previousPatched, existing) && !previousArtifactVerified)
        {
            throw new XPlaneRemovalMismatchException(
                "The scenery changed and the previous removal ownership could not be verified. The saved backup was preserved.");
        }
        var originalFeatures = AirportFeatureIds(original);
        var changedFeatures = AirportFeatureIds(previousPatched);
        changedFeatures.ExceptWith(originalFeatures);
        if (changedFeatures.Overlaps(AirportFeatureIds(current)))
            throw new XPlaneRemovalMismatchException("The updated scenery still contains previous BARS removals. The saved backup was preserved.");
    }

    internal string? GetAvailabilityStamp(string icao, string packageName)
    {
        var root = ResolveXPlaneRoot();
        return root == null ? null : AvailabilityStamp(root, icao, packageName);
    }

    private static string? AvailabilityStamp(string root, string icao, string packageName)
    {
        if (XPlanePatchTransaction.HasPending(GetStateRoot(root))) return null;
        var state = LoadPatchState(root);
        var airport = state.Airports.FirstOrDefault(item => item.Icao.Equals(icao, StringComparison.OrdinalIgnoreCase)
            && item.PackageName.Equals(packageName, StringComparison.Ordinal));
        var dsfFiles = XPlaneDsfRemovals.AvailabilityFiles(root, GetStateRoot(root), icao, packageName);
        if (airport == null && dsfFiles == null) return null;
        var paths = new List<string>(dsfFiles ?? []);
        if (airport != null)
        {
            var backup = state.SourceBackups.FirstOrDefault(item => item.SourceRelativePath.Equals(airport.SourceRelativePath, StringComparison.OrdinalIgnoreCase));
            if (backup == null) return null;
            paths.AddRange([GetStatePath(root), ResolveStateSourcePath(root, airport.SourceRelativePath), ResolveBackupPath(root, backup.BackupFile)]);
        }
        var ini = new FileInfo(Path.Combine(root, "Custom Scenery", "scenery_packs.ini"));
        var stamps = new List<string> { ini.Exists ? $"{ini.FullName}|{ini.Length}|{ini.LastWriteTimeUtc.Ticks}" : $"{ini.FullName}|missing" };
        foreach (var path in paths)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            stamps.Add($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        }
        return string.Join('\n', stamps);
    }

    private static HashSet<string> AirportFeatureIds(string airport)
    {
        var lines = airport.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < lines.Length; index++)
        {
            if (!StartsWithRecord(lines[index], 120)) continue;
            var end = index + 1;
            while (end < lines.Length && (string.IsNullOrWhiteSpace(lines[end]) || IsNodeRecord(lines[end]))) end++;
            result.Add(FeatureId(lines[index..end]));
            index = end - 1;
        }
        return result;
    }

    private async Task<DownloadedArtifact> DownloadArtifactAsync(
        string icao,
        string packageName,
        string? removalArtifactKey,
        string? artifactIdentity,
        string? artifactGenerationId,
        CancellationToken cancellationToken)
    {
        var legacySafePackage = string.Concat(packageName.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '-'));
        var effectiveIdentity = !string.IsNullOrWhiteSpace(artifactIdentity)
            ? artifactIdentity.Trim()
            : $"legacy:{icao}:{legacySafePackage}";
        var cacheKey = $"{effectiveIdentity}:{artifactGenerationId}";
        if (!string.IsNullOrWhiteSpace(artifactGenerationId) &&
            _artifactCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var url = !string.IsNullOrWhiteSpace(removalArtifactKey)
            ? BuildCoreArtifactUrl(removalArtifactKey)
            : new Uri(
                $"https://cdn.stopbars.com/RemovalObjects/" +
                $"{icao}_{Uri.EscapeDataString(legacySafePackage)}_xplane_removals.json");
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxRemovalArtifactBytes)
        {
            throw new InvalidOperationException(
                $"The X-Plane removals artifact exceeds the {MaxRemovalArtifactBytes / (1024 * 1024)} MB safety limit.");
        }

        var bytes = await ReadBoundedArtifactAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var artifact = JsonSerializer.Deserialize<RemovalArtifact>(
            bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (artifact == null || (artifact.Schema != Schema && artifact.Schema != LegacySchema) || NormalizeIcao(artifact.Icao) != icao)
        {
            throw new InvalidOperationException("The X-Plane removals artifact is invalid.");
        }
        ValidateArtifact(artifact);
        var downloaded = new DownloadedArtifact(
            artifact,
            effectiveIdentity,
            artifactGenerationId,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(artifactGenerationId))
        {
            _artifactCache[cacheKey] = downloaded;
        }
        return downloaded;
    }

    private static Uri BuildCoreArtifactUrl(string artifactKey)
    {
        var segments = artifactKey
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        if (segments.Length == 0 ||
            artifactKey.Contains('\\') ||
            artifactKey.Contains('?') ||
            artifactKey.Contains('#') ||
            segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("The X-Plane removal artifact key is invalid.");
        }
        return new Uri(
            CoreApiBase,
            "cdn/files/" + string.Join('/', segments.Select(Uri.EscapeDataString)));
    }

    private static async Task<byte[]> ReadBoundedArtifactAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (destination.Length + read > MaxRemovalArtifactBytes)
            {
                throw new InvalidOperationException(
                    $"The X-Plane removals artifact exceeds the {MaxRemovalArtifactBytes / (1024 * 1024)} MB safety limit.");
            }
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static string PatchAirportBlock(string airportBlock, RemovalArtifact artifact)
    {
        var lines = airportBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var selectorsByFeature = artifact.Selectors
            .GroupBy(selector => selector.Feature, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var matchedSelectors = new HashSet<RemovalSelector>();

        for (var index = 0; index < lines.Count;)
        {
            if (!StartsWithRecord(lines[index], 120))
            {
                index++;
                continue;
            }

            var end = index + 1;
            while (end < lines.Count &&
                   (string.IsNullOrWhiteSpace(lines[end]) || IsNodeRecord(lines[end])))
            {
                end++;
            }

            var featureLines = lines.GetRange(index, end - index);
            var featureId = FeatureId(featureLines);
            if (selectorsByFeature.TryGetValue(featureId, out var selectors))
            {
                PatchFeature(featureLines, selectors, matchedSelectors);
                lines.RemoveRange(index, end - index);
                lines.InsertRange(index, featureLines);
                end = index + featureLines.Count;
            }
            index = end;
        }

        if (matchedSelectors.Count != artifact.Selectors.Count)
        {
            var unmatched = artifact.Selectors
                .Where(selector => !matchedSelectors.Contains(selector))
                .Take(12)
                .Select(selector => $"{selector.Feature}:{selector.Code}:run{selector.Run}");
            throw new XPlaneRemovalMismatchException(
                $"Installed apt.dat did not match {artifact.Selectors.Count - matchedSelectors.Count} " +
                $"of {artifact.Selectors.Count} X-Plane removal selectors " +
                $"({string.Join(", ", unmatched)}).");
        }
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    private static void PatchFeature(
        List<string> featureLines,
        IReadOnlyCollection<RemovalSelector> selectors,
        ISet<RemovalSelector> matchedSelectors)
    {
        var patched = XPlaneAptRangePatcher.Patch(featureLines,
            selectors.Select(selector => new XPlaneAptRangePatcher.Selection(selector.Code, selector.Run, selector.Ranges)).ToArray());
        featureLines.Clear();
        featureLines.AddRange(patched);
        foreach (var selector in selectors) matchedSelectors.Add(selector);
    }

    private static async Task<SourceAirport?> FindActiveAirportSourceAsync(
        string root,
        string icao,
        CancellationToken cancellationToken)
    {
        foreach (var aptPath in EnumerateAptDatByPriority(root))
        {
            var block = await ReadAirportBlockAsync(aptPath, icao, cancellationToken).ConfigureAwait(false);
            if (block != null)
            {
                return new SourceAirport(aptPath, block);
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateAptDatByPriority(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var iniPath = Path.Combine(root, "Custom Scenery", "scenery_packs.ini");
        if (File.Exists(iniPath))
        {
            foreach (var line in File.ReadLines(iniPath))
            {
                var trimmed = line.Trim();
                const string prefix = "SCENERY_PACK ";
                if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var relative = trimmed[prefix.Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
                if (relative.Contains(PackageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var candidate = Path.Combine(root, relative, "Earth nav data", "apt.dat");
                if (File.Exists(candidate) && seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        var global = Path.Combine(root, "Global Scenery", "Global Airports", "Earth nav data", "apt.dat");
        if (File.Exists(global) && seen.Add(global))
        {
            yield return global;
        }
    }

    private static Task<string?> ReadAirportBlockAsync(
        string path,
        string icao,
        CancellationToken cancellationToken) => Task.Run(() => ReadAirportBlock(path, icao, cancellationToken), cancellationToken);

    private static string? ReadAirportBlock(string path, string icao, CancellationToken cancellationToken)
    {
        if (ReadSession.Value is { } session && session.Airports.Contains(icao))
        {
            var file = session.GetFile(path);
            file.Blocks ??= ReadAirportBlocks(path, session.Airports, cancellationToken);
            return file.Blocks.GetValueOrDefault(icao);
        }
        return ReadAirportBlocks(path, new HashSet<string>([icao], StringComparer.OrdinalIgnoreCase), cancellationToken)
            .GetValueOrDefault(icao);
    }

    private static Dictionary<string, string> ReadAirportBlocks(
        string path, HashSet<string> airports, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 128 * 1024);
        StringBuilder? block = null;
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void SaveBlock()
        {
            if (block == null || matches.Count == 0) return;
            var text = block.ToString().TrimEnd();
            foreach (var match in matches) result.TryAdd(match, text);
        }
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Geometry dominates global apt.dat files. Only headers and the end
            // marker need tokenizing while locating an airport.
            var record = FirstField(line);
            if (record is not ("1" or "16" or "17" or "99"))
            {
                block?.Append(line).Append(Environment.NewLine);
                if (block != null && record is "1302")
                {
                    var metadata = SplitFields(line.ToString());
                    if (metadata.Count >= 3 &&
                        metadata[1].Equals("icao_code", StringComparison.OrdinalIgnoreCase) &&
                        airports.Contains(metadata[2])) matches.Add(metadata[2]);
                }
                continue;
            }
            var fields = SplitFields(line.ToString());
            var isHeader = fields.Count >= 5 && fields[0] is "1" or "16" or "17";
            if (fields.Count > 0 && fields[0] == "99")
            {
                SaveBlock();
                return result;
            }
            if (isHeader)
            {
                SaveBlock();
                if (result.Count == airports.Count) return result;
                block ??= new StringBuilder();
                block.Clear();
                block.Append(line).Append(Environment.NewLine);
                matches.Clear();
                if (airports.Contains(fields[4])) matches.Add(fields[4]);
                continue;
            }
            if (block != null)
            {
                block.Append(line).Append(Environment.NewLine);
            }
        }
        SaveBlock();
        return result;
    }

    private sealed class AirportReadSession(IEnumerable<string> airports, AirportReadSession? previous) : IDisposable
    {
        internal HashSet<string> Airports { get; } = new(airports, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LockedAirportFile> _files = new(StringComparer.OrdinalIgnoreCase);

        internal LockedAirportFile GetFile(string path)
        {
            path = Path.GetFullPath(path);
            if (!_files.TryGetValue(path, out var file)) _files.Add(path, file = new LockedAirportFile(path));
            return file;
        }

        internal void Invalidate(string path)
        {
            if (_files.Remove(Path.GetFullPath(path), out var file)) file.Dispose();
        }

        public void Dispose()
        {
            Clear();
            ReadSession.Value = previous;
        }

        internal void Clear()
        {
            foreach (var file in _files.Values) file.Dispose();
            _files.Clear();
        }
    }

    private sealed class LockedAirportFile(string path) : IDisposable
    {
        // Deny both writes and deletion for the entire lifetime of cached data.
        private readonly FileStream _lease = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        internal Dictionary<string, string>? Blocks { get; set; }
        internal string? Sha256 { get; set; }
        public void Dispose() => _lease.Dispose();
    }


    private static bool AirportBlockMatches(IReadOnlyList<string> block, string icao)
    {
        var header = block.Count > 0 ? SplitFields(block[0]) : [];
        if (header.Count >= 5 && string.Equals(header[4], icao, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return block.Any(line =>
        {
            if (FirstField(line) is not "1302") return false;
            var fields = SplitFields(line);
            return fields.Count >= 3 &&
                   fields[0] == "1302" &&
                   fields[1].Equals("icao_code", StringComparison.OrdinalIgnoreCase) &&
                   fields[2].Equals(icao, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static ReadOnlySpan<char> FirstField(ReadOnlySpan<char> line)
    {
        var text = line.TrimStart();
        var length = 0;
        while (length < text.Length && !char.IsWhiteSpace(text[length])) length++;
        return text[..length];
    }

    private static async Task<bool> RestoreAptAirportAsync(
        string root,
        string icao,
        CancellationToken cancellationToken)
    {
        var state = LoadPatchState(root);
        var applied = state.Airports.FirstOrDefault(item =>
            item.Icao.Equals(icao, StringComparison.OrdinalIgnoreCase));
        var changed = false;

        if (applied != null)
        {
            var sourcePath = ResolveStateSourcePath(root, applied.SourceRelativePath);
            var sourceBackup = await EnsureFullSourceBackupAsync(
                    root,
                    state,
                    sourcePath,
                    cancellationToken)
                .ConfigureAwait(false);
            var fullBackupPath = ResolveBackupPath(root, sourceBackup.BackupFile);
            var original = await ReadAirportBlockAsync(fullBackupPath, icao, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"{icao} was not found in its verified full-file backup; BARS will not modify active scenery.");
            if (!BlockHash(original).Equals(applied.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{icao}'s full-file backup does not match its saved removal state.");
            }
            var current = await ReadAirportBlockAsync(sourcePath, icao, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{icao} disappeared from its active apt.dat.");
            if (MatchesPatchedBlock(current, applied))
            {
                // Restore only the owned airport block. A full-file backup is
                // disaster-recovery evidence, not a normal rollback source: an
                // updater may have legitimately changed unrelated airports while
                // this BARS block remained untouched.
                await ReplaceAirportBlockAtomicallyAsync(
                        sourcePath,
                        icao,
                        PreserveRampMetadata(original, current),
                        cancellationToken)
                    .ConfigureAwait(false);
                changed = true;
            }
            else if (!RampIndependentHash(current).Equals(RampIndependentHash(original), StringComparison.OrdinalIgnoreCase))
            {
                throw new XPlaneRemovalMismatchException(
                    $"{icao}'s active apt.dat changed outside BARS. Its original backup was preserved and no overwrite was attempted.");
            }

            state.Airports.Remove(applied);
            try
            {
                await SavePatchStateAsync(root, state, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                state.Airports.Add(applied);
                if (changed)
                {
                    // The apt.dat change must be undone even when the state
                    // save failed because the caller cancelled.
                    await ReplaceAirportBlockAtomicallyAsync(
                            sourcePath,
                            icao,
                            current,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                throw;
            }
        }

        changed |= await RemoveLegacyOverrideForAirportAsync(root, icao, cancellationToken)
            .ConfigureAwait(false);
        return changed;
    }

    private static async Task<bool> RemoveLegacyOverrideForAirportAsync(
        string root,
        string icao,
        CancellationToken cancellationToken)
    {
        var packageRoot = Path.Combine(root, "Custom Scenery", PackageName);
        var blocksDirectory = Path.Combine(packageRoot, ".bars");
        var blockPath = Path.Combine(blocksDirectory, icao + ".apt");
        var changed = false;
        if (File.Exists(blockPath))
        {
            XPlanePatchTransaction.BeforeWrite(blockPath, null);
            File.Delete(blockPath);
            changed = true;
        }

        changed |= await RebuildPackageAsync(
                root,
                packageRoot,
                blocksDirectory,
                changed: false,
                cancellationToken)
            .ConfigureAwait(false);

        var hasLegacyBlocks = Directory.Exists(blocksDirectory) &&
                              Directory.EnumerateFiles(blocksDirectory, "*.apt").Any();
        if (!XPlanePatchTransaction.IsActive && !hasLegacyBlocks && Directory.Exists(packageRoot))
        {
            try
            {
                XPlanePatchTransaction.EnsureSimulatorClosed();
                Directory.Delete(packageRoot, recursive: true);
            }
            catch (IOException)
            {
                // The package has already been disabled; stale cache files are harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // The package has already been disabled; stale cache files are harmless.
            }
        }
        return changed;
    }

    private static PatchState LoadPatchState(string root)
    {
        var path = GetStatePath(root);
        if (!File.Exists(path))
        {
            return new PatchState();
        }

        var state = JsonSerializer.Deserialize<PatchState>(
            File.ReadAllText(path),
            StateJsonOptions);
        if (state == null ||
            (state.Schema != PatchState.SchemaName &&
             state.Schema != PatchState.LegacySchemaName) ||
            state.Airports == null ||
            state.Airports.Any(item =>
                item == null ||
                string.IsNullOrWhiteSpace(item.Icao) ||
                string.IsNullOrWhiteSpace(item.SourceRelativePath) ||
                string.IsNullOrWhiteSpace(item.BackupFile) ||
                !IsSha256(item.OriginalSha256) ||
                !IsSha256(item.PatchedSha256) ||
                (!string.IsNullOrWhiteSpace(item.ArtifactSha256) &&
                 !IsSha256(item.ArtifactSha256))))
        {
            throw new InvalidOperationException(
                "The saved X-Plane removal state is invalid; BARS will not modify active scenery.");
        }
        state.SourceBackups ??= [];
        if (state.SourceBackups.Any(item =>
                item == null ||
                string.IsNullOrWhiteSpace(item.SourceRelativePath) ||
                string.IsNullOrWhiteSpace(item.BackupFile) ||
                !IsSha256(item.OriginalSha256)))
        {
            throw new InvalidOperationException(
                "The saved X-Plane removal backup state is invalid; BARS will not modify active scenery.");
        }
        state.Schema = PatchState.SchemaName;
        return state;
    }

    private static async Task SavePatchStateAsync(
        string root,
        PatchState state,
        CancellationToken cancellationToken)
    {
        state.Schema = PatchState.SchemaName;
        state.Airports = state.Airports
            .OrderBy(item => item.Icao, StringComparer.OrdinalIgnoreCase)
            .ToList();
        state.SourceBackups = state.SourceBackups
            .OrderBy(item => item.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var source in state.SourceBackups)
        {
            var sourcePath = ResolveStateSourcePath(root, source.SourceRelativePath);
            if (!File.Exists(sourcePath)) continue;
            var activeAirports = new List<string>();
            foreach (var airport in state.Airports
                .Where(airport => airport.SourceRelativePath.Equals(source.SourceRelativePath, StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                var current = await ReadAirportBlockAsync(sourcePath, airport.Icao, cancellationToken).ConfigureAwait(false);
                if (current != null && MatchesPatchedBlock(current, airport)) activeAirports.Add(airport.Icao);
            }
            var marker = JsonSerializer.Serialize(new { schema = "bars-xplane-source-status/v1", airports = activeAirports });
            await WriteAtomicallyIfChangedAsync(sourcePath + ".bars-removals.json",
                marker, cancellationToken).ConfigureAwait(false);
        }
        var json = JsonSerializer.Serialize(state, StateJsonOptions) + Environment.NewLine;
        await WriteAtomicallyIfChangedAsync(GetStatePath(root), json, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<SourceFileBackup> EnsureFullSourceBackupAsync(
        string root,
        PatchState state,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException("The active X-Plane apt.dat no longer exists.");
        }

        var sourceRelativePath = Path.GetRelativePath(root, sourcePath);
        var activePatches = state.Airports
            .Where(item => item.SourceRelativePath.Equals(
                sourceRelativePath,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var existing = state.SourceBackups.FirstOrDefault(item =>
            item.SourceRelativePath.Equals(sourceRelativePath, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            var existingPath = ResolveBackupPath(root, existing.BackupFile);
            if (!File.Exists(existingPath) ||
                !IsSha256(existing.OriginalSha256) ||
                !string.Equals(
                    await FileSha256Async(existingPath, cancellationToken).ConfigureAwait(false),
                    existing.OriginalSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The full X-Plane scenery backup is missing or damaged; BARS will not modify active scenery.");
            }

            if (activePatches.Count != 0)
            {
                return existing;
            }

            var currentHash = await FileSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
            if (currentHash.Equals(existing.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            // With no active BARS patches, a changed source is a legitimate simulator
            // or scenery update. Preserve that new pristine version before patching it.
            state.SourceBackups.Remove(existing);
        }

        var backupsDirectory = Path.Combine(GetStateRoot(root), "backups");
        Directory.CreateDirectory(backupsDirectory);
        EnsureAvailableSpace(backupsDirectory, new FileInfo(sourcePath).Length);
        var temporary = Path.Combine(backupsDirectory, $"source-{Guid.NewGuid():N}.tmp");

        try
        {
            await CopyFileAsync(sourcePath, temporary, cancellationToken).ConfigureAwait(false);

            // Version 1 stored only airport blocks. Reconstruct a pristine full source
            // once, verify every old block, and then migrate to the full-file backup.
            foreach (var patch in activePatches)
            {
                var current = await ReadAirportBlockAsync(sourcePath, patch.Icao, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"{patch.Icao} disappeared from its active apt.dat during backup migration.");
                var currentHash = BlockHash(current);
                if (!currentHash.Equals(patch.PatchedSha256, StringComparison.OrdinalIgnoreCase) &&
                    !currentHash.Equals(patch.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{patch.Icao}'s active apt.dat changed outside BARS. Its existing backup was preserved.");
                }

                var blockBackupPath = ResolveBackupPath(root, patch.BackupFile);
                var original = File.Exists(blockBackupPath)
                    ? await File.ReadAllTextAsync(blockBackupPath, cancellationToken).ConfigureAwait(false)
                    : null;
                if (string.IsNullOrWhiteSpace(original) ||
                    !BlockHash(original).Equals(patch.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{patch.Icao}'s saved airport backup is missing or damaged; BARS will not migrate it.");
                }

                await ReplaceAirportBlockAtomicallyAsync(
                        temporary,
                        patch.Icao,
                        original,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var originalSha256 = await FileSha256Async(temporary, cancellationToken).ConfigureAwait(false);
            var pathHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(sourceRelativePath.ToUpperInvariant())))
                .ToLowerInvariant()[..12];
            var backupFile = $"source-{pathHash}-{originalSha256[..16]}.apt.dat";
            var backupPath = ResolveBackupPath(root, backupFile);
            if (File.Exists(backupPath))
            {
                var savedHash = await FileSha256Async(backupPath, cancellationToken).ConfigureAwait(false);
                if (!savedHash.Equals(originalSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "An existing full X-Plane scenery backup failed verification.");
                }
                File.Delete(temporary);
            }
            else
            {
                File.Move(temporary, backupPath);
            }

            var sourceBackup = new SourceFileBackup
            {
                SourceRelativePath = sourceRelativePath,
                BackupFile = backupFile,
                OriginalSha256 = originalSha256
            };
            state.SourceBackups.Add(sourceBackup);
            await SavePatchStateAsync(root, state, cancellationToken).ConfigureAwait(false);
            return sourceBackup;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await input.CopyToAsync(output, bufferSize, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task<string> FileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        // Temporary backup files are renamed after hashing and must not be leased.
        cancellationToken.ThrowIfCancellationRequested();
        var cached = path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ? null : ReadSession.Value?.GetFile(path);
        if (cached?.Sha256 is { } verifiedHash) return verifiedHash;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var value = Convert.ToHexString(hash).ToLowerInvariant();
        if (cached != null) cached.Sha256 = value;
        return value;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void EnsureAvailableSpace(string directory, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("BARS could not determine available backup space.");
        }

        var available = new DriveInfo(root).AvailableFreeSpace;
        const long safetyMargin = 64L * 1024L * 1024L;
        if (available < requiredBytes + safetyMargin)
        {
            throw new InvalidOperationException(
                "Not enough free disk space to create a safe X-Plane scenery backup.");
        }
    }

    private static string GetStateRoot(string root)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rootHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))
            .ToLowerInvariant()[..16];
        return Path.Combine(localAppData, "BARS", "Client", "XPlaneRemovals", rootHash);
    }

    private static string GetStatePath(string root) =>
        Path.Combine(GetStateRoot(root), "state.json");

    private static string ResolveBackupPath(string root, string backupFile)
    {
        if (!string.Equals(Path.GetFileName(backupFile), backupFile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The saved X-Plane removal backup path is invalid.");
        }
        return Path.Combine(GetStateRoot(root), "backups", backupFile);
    }

    private static string ResolveStateSourcePath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The saved X-Plane apt.dat path is outside the simulator installation.");
        }
        return resolved;
    }

    private static string NormalizeBlock(string block) =>
        block.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd() + Environment.NewLine;

    private static string BlockHash(string block) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    block.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n')
                        .TrimEnd())))
            .ToLowerInvariant();

    // Ramp metadata is unrelated to light removals. Keep every other record,
    // including the ramp locations and metadata record positions, hash-bound.
    private static string RampIndependentHash(string block) => BlockHash(string.Join('\n',
        block.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd().Split('\n')
            .Select(line => FirstField(line) is "1301" ? "1301" : line)));

    private static bool MatchesPatchedBlock(string current, AppliedAirportPatch applied) =>
        BlockHash(current).Equals(applied.PatchedSha256, StringComparison.OrdinalIgnoreCase) ||
        (IsSha256(applied.PatchedRampIndependentSha256 ?? "") &&
         RampIndependentHash(current).Equals(applied.PatchedRampIndependentSha256, StringComparison.OrdinalIgnoreCase));

    private static string PreserveRampMetadata(string target, string current)
    {
        static string[] Lines(string block) => block.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').TrimEnd().Split('\n');
        static IEnumerable<string> RampStructure(IEnumerable<string> lines) => lines
            .Where(line => FirstField(line) is "1300" or "1301")
            .Select(line => FirstField(line) is "1301" ? "1301" : line);
        var targetLines = Lines(target);
        var currentLines = Lines(current);
        if (!RampStructure(targetLines).SequenceEqual(RampStructure(currentLines)))
            throw new InvalidOperationException("X-Plane ramp locations changed outside BARS; no overwrite was attempted.");
        var metadata = new Queue<string>(currentLines.Where(line => FirstField(line) is "1301"));
        return string.Join(Environment.NewLine, targetLines.Select(line =>
            FirstField(line) is "1301" ? metadata.Dequeue() : line));
    }

    private static Task ReplaceAirportBlockAtomicallyAsync(
        string aptPath, string icao, string replacement, CancellationToken cancellationToken) =>
        Task.Run(() => ReplaceAirportBlockCoreAsync(aptPath, icao, replacement, cancellationToken), cancellationToken);

    private static async Task ReplaceAirportBlockCoreAsync(
        string aptPath,
        string icao,
        string replacement,
        CancellationToken cancellationToken)
    {
        ReadSession.Value?.Invalidate(aptPath);
        if (!File.Exists(aptPath))
        {
            throw new InvalidOperationException($"The active apt.dat for {icao} no longer exists.");
        }

        var temporary = aptPath + ".bars.tmp";
        var newLine = DetectNewLine(aptPath);
        var replaced = false;
        try
        {
            using var stream = new FileStream(aptPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 128 * 1024);
            await using var writer = new StreamWriter(
                temporary,
                append: false,
                new UTF8Encoding(false),
                bufferSize: 128 * 1024)
            {
                NewLine = newLine
            };

            List<string>? block = null;

            void WriteBlock(IReadOnlyList<string> lines)
            {
                if (AirportBlockMatches(lines, icao))
                {
                    foreach (var replacementLine in replacement
                                 .Replace("\r\n", "\n", StringComparison.Ordinal)
                                 .Replace('\r', '\n')
                                 .TrimEnd()
                                 .Split('\n'))
                    {
                        writer.WriteLine(replacementLine);
                    }
                    var trailing = lines.Count;
                    while (trailing > 0 && string.IsNullOrWhiteSpace(lines[trailing - 1])) trailing--;
                    for (var index = trailing; index < lines.Count; index++) writer.WriteLine(lines[index]);
                    replaced = true;
                    return;
                }

                foreach (var blockLine in lines)
                {
                    writer.WriteLine(blockLine);
                }
            }

            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = FirstField(line);
                var isHeader = record is "1" or "16" or "17" && SplitFields(line).Count >= 5;
                if (isHeader)
                {
                    if (block != null)
                    {
                        WriteBlock(block);
                    }
                    block = [line];
                    continue;
                }

                if (record is "99")
                {
                    if (block != null)
                    {
                        WriteBlock(block);
                        block = null;
                    }
                    writer.WriteLine(line);
                    continue;
                }

                if (block != null)
                {
                    block.Add(line);
                }
                else
                {
                    writer.WriteLine(line);
                }
            }

            if (block != null)
            {
                WriteBlock(block);
            }
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (writer.BaseStream is FileStream fileStream)
            {
                fileStream.Flush(flushToDisk: true);
            }

            if (!replaced)
            {
                throw new InvalidOperationException($"{icao} was not found while rebuilding its active apt.dat.");
            }
            await writer.DisposeAsync().ConfigureAwait(false);
            reader.Dispose();
            XPlanePatchTransaction.BeforeMove(temporary, aptPath);
            File.Move(temporary, aptPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string DetectNewLine(string path)
    {
        using var stream = File.OpenRead(path);
        var previous = -1;
        for (var index = 0; index < 64 * 1024; index++)
        {
            var current = stream.ReadByte();
            if (current < 0)
            {
                break;
            }
            if (current == '\n')
            {
                return previous == '\r' ? "\r\n" : "\n";
            }
            previous = current;
        }
        return Environment.NewLine;
    }

    private static async Task<bool> RebuildPackageAsync(
        string root,
        string packageRoot,
        string blocksDirectory,
        bool changed,
        CancellationToken cancellationToken)
    {
        var blocks = (Directory.Exists(blocksDirectory)
                ? Directory.EnumerateFiles(blocksDirectory, "*.apt")
                : [])
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText)
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .ToList();
        var earthNavData = Path.Combine(packageRoot, "Earth nav data");
        var aptPath = Path.Combine(earthNavData, "apt.dat");
        if (blocks.Count == 0)
        {
            if (File.Exists(aptPath))
            {
                XPlanePatchTransaction.BeforeWrite(aptPath, null);
                File.Delete(aptPath);
                changed = true;
            }
            return EnsurePackagePriority(root, enabled: false) || changed;
        }

        Directory.CreateDirectory(earthNavData);
        var content = $"I{Environment.NewLine}1200 Generated by BARS{Environment.NewLine}{Environment.NewLine}" +
                      string.Join(Environment.NewLine + Environment.NewLine, blocks) +
                      $"{Environment.NewLine}{Environment.NewLine}99{Environment.NewLine}";
        changed |= await WriteAtomicallyIfChangedAsync(aptPath, content, cancellationToken).ConfigureAwait(false);
        changed |= EnsurePackagePriority(root, enabled: true);
        return changed;
    }

    private static bool EnsurePackagePriority(string root, bool enabled)
    {
        var iniPath = Path.Combine(root, "Custom Scenery", "scenery_packs.ini");
        var entry = $"SCENERY_PACK Custom Scenery/{PackageName}/";
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : [];
        var original = lines.ToArray();
        lines.RemoveAll(line =>
            line.Contains($"Custom Scenery/{PackageName}/", StringComparison.OrdinalIgnoreCase));
        if (enabled)
        {
            var insertionIndex = lines.FindIndex(line =>
                line.StartsWith("SCENERY_PACK", StringComparison.OrdinalIgnoreCase));
            lines.Insert(insertionIndex < 0 ? lines.Count : insertionIndex, entry);
        }
        if (original.SequenceEqual(lines, StringComparer.Ordinal))
        {
            return false;
        }

        var temporary = iniPath + ".bars.tmp";
        File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
        XPlanePatchTransaction.BeforeMove(temporary, iniPath);
        File.Move(temporary, iniPath, true);
        return true;
    }

    private static async Task<bool> WriteAtomicallyIfChangedAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path) &&
            string.Equals(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                content,
                StringComparison.Ordinal))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        XPlanePatchTransaction.BeforeMove(temporary, path);
        File.Move(temporary, path, true);
        return true;
    }

    private static string FeatureId(IEnumerable<string> lines)
    {
        var normalized = string.Join(
            '\n',
            lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(NormalizeAptLine));
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"xplane-apt-feature-v1|{normalized}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static string NormalizeAptLine(string line) =>
        string.Join(' ', SplitFields(line));

    private static bool StartsWithRecord(string line, int code)
    {
        var fields = SplitFields(line);
        return fields.Count > 0 && fields[0] == code.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsNodeRecord(string line)
    {
        var fields = SplitFields(line);
        return fields.Count > 0 && int.TryParse(fields[0], out var code) && NodeCodes.Contains(code);
    }

    private static List<string> SplitFields(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string NormalizeIcao(string icao)
    {
        var normalized = (icao ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 4 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidOperationException("Invalid airport ICAO.");
        }
        return normalized;
    }

    private static void ValidateArtifact(RemovalArtifact artifact)
    {
        if ((artifact.Schema != Schema && artifact.Schema != LegacySchema) ||
            artifact.Selectors == null)
        {
            throw new InvalidOperationException("The X-Plane removals artifact is invalid.");
        }

        if (artifact.DsfSelectors == null || artifact.DsfSelectors.Count + artifact.Selectors.Count > 20000 ||
            (artifact.Schema == LegacySchema && artifact.DsfSelectors.Count != 0)) throw new InvalidOperationException("Invalid DSF artifact version or selector count.");
        foreach (var selector in artifact.DsfSelectors) selector.Validate();
        foreach (var selector in artifact.Selectors)
        {
            if (selector == null ||
                selector.Feature.Length != 16 ||
                selector.Feature.Any(character => !Uri.IsHexDigit(character)) ||
                selector.Code is < 101 or > 108 ||
                selector.Run < 0 ||
                selector.Ranges == null ||
                selector.Ranges.Count == 0 ||
                selector.Ranges.Any(range =>
                    range == null ||
                    range.Length != 2 ||
                    !double.IsFinite(range[0]) ||
                    !double.IsFinite(range[1]) ||
                    range[0] < 0 ||
                    range[1] > 1 ||
                    range[1] <= range[0]))
            {
                throw new InvalidOperationException("The X-Plane removals artifact contains an invalid selector.");
            }
        }
    }

    private static string? ResolveXPlaneRoot()
    {
        try
        {
            var running = Process.GetProcessesByName("X-Plane")
                .Select(process =>
                {
                    try { return Path.GetDirectoryName(process.MainModule?.FileName); }
                    catch { return null; }
                    finally { process.Dispose(); }
                })
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (IsXPlaneRoot(running))
            {
                return running;
            }
        }
        catch { }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installerSettings = Path.Combine(localAppData, "BARS", "Installer", "settings.json");
        if (File.Exists(installerSettings))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(installerSettings));
                if (document.RootElement.TryGetProperty("Pilot-Client", out var client))
                {
                    foreach (var property in client.EnumerateObject())
                    {
                        if (property.Name.Contains("xplane", StringComparison.OrdinalIgnoreCase) &&
                            property.Value.ValueKind == JsonValueKind.String &&
                            IsXPlaneRoot(property.Value.GetString()))
                        {
                            return property.Value.GetString();
                        }
                    }
                }
            }
            catch { }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", "X-Plane 12"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Steam", "steamapps", "common", "X-Plane 12"),
        };
        return candidates.FirstOrDefault(IsXPlaneRoot);
    }

    private static bool IsXPlaneRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "Custom Scenery"));

    internal static bool IsXPlaneProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("X-Plane");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Failing to inspect the process list is not proof that X-Plane is
            // closed. Refuse to edit active scenery in that case.
            return true;
        }
    }

    private sealed class RemovalArtifact
    {
        public string Schema { get; set; } = string.Empty;
        public string Icao { get; set; } = string.Empty;
        public List<RemovalSelector> Selectors { get; set; } = [];
        public List<XPlaneDsfSelector> DsfSelectors { get; set; } = [];
    }

    private sealed class RemovalSelector
    {
        public string Feature { get; set; } = string.Empty;
        public int Code { get; set; }
        public int Run { get; set; }
        public List<double[]> Ranges { get; set; } = [];
    }

    private sealed record SourceAirport(string Path, string AirportBlock);
    private sealed record DownloadedArtifact(
        RemovalArtifact Artifact,
        string Identity,
        string? GenerationId,
        string Sha256);

    private sealed class PatchState
    {
        public const string SchemaName = "bars-xplane-source-patches/v2";
        public const string LegacySchemaName = "bars-xplane-source-patches/v1";
        public string Schema { get; set; } = SchemaName;
        public List<AppliedAirportPatch> Airports { get; set; } = [];
        public List<SourceFileBackup> SourceBackups { get; set; } = [];
    }

    private sealed class SourceFileBackup
    {
        public string SourceRelativePath { get; set; } = string.Empty;
        public string BackupFile { get; set; } = string.Empty;
        public string OriginalSha256 { get; set; } = string.Empty;
    }

    private sealed class AppliedAirportPatch
    {
        public string Icao { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;
        public string SourceRelativePath { get; set; } = string.Empty;
        public string BackupFile { get; set; } = string.Empty;
        public string OriginalSha256 { get; set; } = string.Empty;
        public string PatchedSha256 { get; set; } = string.Empty;
        public string? PatchedRampIndependentSha256 { get; set; }
        public string? ArtifactIdentity { get; set; }
        public string? ArtifactGenerationId { get; set; }
        public string? ArtifactSha256 { get; set; }

        public AppliedAirportPatch Copy() => new()
        {
            Icao = Icao,
            PackageName = PackageName,
            SourceRelativePath = SourceRelativePath,
            BackupFile = BackupFile,
            OriginalSha256 = OriginalSha256,
            PatchedSha256 = PatchedSha256,
            PatchedRampIndependentSha256 = PatchedRampIndependentSha256,
            ArtifactIdentity = ArtifactIdentity,
            ArtifactGenerationId = ArtifactGenerationId,
            ArtifactSha256 = ArtifactSha256
        };
    }
}
