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
    private const string Schema = "bars-xplane-removals/v1";
    private const int MaxRemovalArtifactBytes = 8 * 1024 * 1024;
    private static readonly Uri CoreApiBase = new("https://v2.stopbars.com/");
    private static readonly HashSet<int> NodeCodes = [111, 112, 113, 114, 115, 116];
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
            return applied == null && !File.Exists(legacyBlockPath);
        }

        if (applied == null)
        {
            return false;
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
            var testingAirports = LoadPatchState(root).Airports
                .Where(item => item.PackageName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Icao)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (testingAirports.Length == 0) return false;
            if (IsXPlaneProcessRunning()) return true;

            var changed = false;
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
                    "Close X-Plane before applying testing removals; its active apt.dat is currently in use.");
            }
            var normalizedIcao = NormalizeIcao(icao);
            var artifact = JsonSerializer.Deserialize<RemovalArtifact>(
                removalJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (artifact?.Schema != Schema ||
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
            return await RestoreAirportAsync(root, icao, cancellationToken).ConfigureAwait(false);
        }

        var state = LoadPatchState(root);
        var existing = state.Airports.FirstOrDefault(item =>
            item.Icao.Equals(icao, StringComparison.OrdinalIgnoreCase));

        var activeSource = await FindActiveAirportSourceAsync(root, icao, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{icao} was not found in active X-Plane scenery.");
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
            !BlockHash(current).Equals(existingForSource.PatchedSha256, StringComparison.OrdinalIgnoreCase) &&
            !BlockHash(current).Equals(existingForSource.OriginalSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{icao}'s active apt.dat changed outside BARS. Restart with removals disabled before applying it again.");
        }

        var patched = PatchAirportBlock(original, artifact);
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
            previousSourceWasPatched = previousCurrentHash.Equals(
                existing.PatchedSha256,
                StringComparison.OrdinalIgnoreCase);
            if (!previousSourceWasPatched &&
                !previousCurrentHash.Equals(existing.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
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
        }

        var previousState = existing == null ? null : existing.Copy();
        existing ??= new AppliedAirportPatch { Icao = icao };
        existing.PackageName = packageName;
        existing.SourceRelativePath = Path.GetRelativePath(root, sourcePath);
        existing.BackupFile = backupFile;
        existing.OriginalSha256 = BlockHash(original);
        existing.PatchedSha256 = patchedHash;
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
                $"https://dev-cdn.stopbars.com/RemovalObjects/" +
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
        if (artifact?.Schema != Schema || NormalizeIcao(artifact.Icao) != icao)
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
                for (var offset = 0; offset < featureLines.Count; offset++)
                {
                    lines[index + offset] = featureLines[offset];
                }
            }
            index = end;
        }

        if (matchedSelectors.Count != artifact.Selectors.Count)
        {
            var unmatched = artifact.Selectors
                .Where(selector => !matchedSelectors.Contains(selector))
                .Take(12)
                .Select(selector => $"{selector.Feature}:{selector.Code}:run{selector.Run}");
            throw new InvalidOperationException(
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
        foreach (var codeGroup in selectors.GroupBy(selector => selector.Code))
        {
            var runs = FindRuns(featureLines, codeGroup.Key);
            foreach (var selector in codeGroup)
            {
                if (selector.Run < 0 || selector.Run >= runs.Count || selector.Ranges.Count == 0)
                {
                    continue;
                }

                var run = runs[selector.Run];
                var total = run.Sum(segment => segment.LengthMeters);
                if (!(total > 0))
                {
                    continue;
                }

                var cursor = 0d;
                var changed = false;
                foreach (var segment in run)
                {
                    var segmentStart = cursor / total;
                    cursor += segment.LengthMeters;
                    var segmentEnd = cursor / total;
                    if (!selector.Ranges.Any(range =>
                            range.Length == 2 &&
                            segmentEnd > range[0] &&
                            segmentStart < range[1]))
                    {
                        continue;
                    }
                    featureLines[segment.LineIndex] = RemoveStyleCode(featureLines[segment.LineIndex], codeGroup.Key);
                    changed = true;
                }
                if (changed)
                {
                    matchedSelectors.Add(selector);
                }
            }
        }
    }

    private static List<List<Segment>> FindRuns(IReadOnlyList<string> featureLines, int lightCode)
    {
        var runs = new List<List<Segment>>();
        List<Segment>? current = null;
        var nodes = featureLines
            .Select((line, index) => (Node: ParseNode(line), LineIndex: index))
            .Where(item => item.Node != null)
            .ToList();
        var closesPath = nodes.Count > 1 && nodes[^1].Node!.Code is 113 or 114;
        var segmentCount = Math.Max(0, nodes.Count - 1) + (closesPath ? 1 : 0);
        for (var index = 0; index < segmentCount; index++)
        {
            var start = nodes[index].Node!;
            var end = nodes[(index + 1) % nodes.Count].Node!;
            if (!start.Styles.Contains(lightCode))
            {
                current = null;
                continue;
            }
            if (current == null)
            {
                current = [];
                runs.Add(current);
            }
            current.Add(new Segment(
                nodes[index].LineIndex,
                DistanceMeters(start.Latitude, start.Longitude, end.Latitude, end.Longitude)));
        }
        return runs;
    }

    private static string RemoveStyleCode(string line, int lightCode)
    {
        var fields = SplitFields(line);
        if (fields.Count < 4 || !int.TryParse(fields[0], out var recordCode))
        {
            return line;
        }
        var styleStart = recordCode is 112 or 114 or 116 ? 5 : 3;
        if (fields.Count <= styleStart)
        {
            return line;
        }
        var styles = fields.Skip(styleStart)
            .SelectMany(field => field.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Where(value => !int.TryParse(value, out var parsed) || parsed != lightCode)
            .ToList();
        return string.Join(' ', fields.Take(styleStart).Concat(styles));
    }

    private static Node? ParseNode(string line)
    {
        var fields = SplitFields(line);
        if (fields.Count < 3 ||
            !int.TryParse(fields[0], out var code) ||
            !NodeCodes.Contains(code) ||
            !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
            !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
        {
            return null;
        }
        var styleStart = code is 112 or 114 or 116 ? 5 : 3;
        var styles = code is 115 or 116
            ? []
            : fields.Skip(styleStart)
                .SelectMany(field => field.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(value => int.TryParse(value, out var parsed) ? parsed : int.MinValue)
                .Where(value => value != int.MinValue)
                .ToHashSet();
        return new Node(code, latitude, longitude, styles);
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

    private static async Task<string?> ReadAirportBlockAsync(
        string path,
        string icao,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path);
        List<string>? block = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var fields = SplitFields(line);
            var isHeader = fields.Count >= 5 && fields[0] is "1" or "16" or "17";
            if (fields.Count > 0 && fields[0] == "99")
            {
                return block != null && AirportBlockMatches(block, icao)
                    ? string.Join(Environment.NewLine, block).TrimEnd()
                    : null;
            }
            if (isHeader)
            {
                if (block != null && AirportBlockMatches(block, icao))
                {
                    return string.Join(Environment.NewLine, block).TrimEnd();
                }
                block = [line];
                continue;
            }
            if (block != null)
            {
                block.Add(line);
            }
        }
        return block != null && AirportBlockMatches(block, icao)
            ? string.Join(Environment.NewLine, block).TrimEnd()
            : null;
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
            var fields = SplitFields(line);
            return fields.Count >= 3 &&
                   fields[0] == "1302" &&
                   fields[1].Equals("icao_code", StringComparison.OrdinalIgnoreCase) &&
                   fields[2].Equals(icao, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task<bool> RestoreAirportAsync(
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
            var currentHash = BlockHash(current);
            if (currentHash.Equals(applied.PatchedSha256, StringComparison.OrdinalIgnoreCase))
            {
                // Restore only the owned airport block. A full-file backup is
                // disaster-recovery evidence, not a normal rollback source: an
                // updater may have legitimately changed unrelated airports while
                // this BARS block remained untouched.
                await ReplaceAirportBlockAtomicallyAsync(
                        sourcePath,
                        icao,
                        original,
                        cancellationToken)
                    .ConfigureAwait(false);
                changed = true;
            }
            else if (!currentHash.Equals(applied.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
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
        if (!hasLegacyBlocks && Directory.Exists(packageRoot))
        {
            try
            {
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
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

    private static async Task ReplaceAirportBlockAtomicallyAsync(
        string aptPath,
        string icao,
        string replacement,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(aptPath))
        {
            throw new InvalidOperationException($"The active apt.dat for {icao} no longer exists.");
        }

        var temporary = aptPath + ".bars.tmp";
        var newLine = DetectNewLine(aptPath);
        var replaced = false;
        try
        {
            using var reader = new StreamReader(aptPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            await using var writer = new StreamWriter(
                temporary,
                append: false,
                new UTF8Encoding(false))
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
                    replaced = true;
                    return;
                }

                foreach (var blockLine in lines)
                {
                    writer.WriteLine(blockLine);
                }
            }

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var fields = SplitFields(line);
                var isHeader = fields.Count >= 5 && fields[0] is "1" or "16" or "17";
                if (isHeader)
                {
                    if (block != null)
                    {
                        WriteBlock(block);
                    }
                    block = [line];
                    continue;
                }

                if (fields.Count > 0 && fields[0] == "99")
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
        if (!string.Equals(artifact.Schema, Schema, StringComparison.Ordinal) ||
            artifact.Selectors == null)
        {
            throw new InvalidOperationException("The X-Plane removals artifact is invalid.");
        }

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

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6371008.8;
        var dLat = (lat2 - lat1) * Math.PI / 180d;
        var dLon = (lon2 - lon1) * Math.PI / 180d;
        var a = Math.Sin(dLat / 2d) * Math.Sin(dLat / 2d) +
                Math.Cos(lat1 * Math.PI / 180d) * Math.Cos(lat2 * Math.PI / 180d) *
                Math.Sin(dLon / 2d) * Math.Sin(dLon / 2d);
        return earthRadius * 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
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
    }

    private sealed class RemovalSelector
    {
        public string Feature { get; set; } = string.Empty;
        public int Code { get; set; }
        public int Run { get; set; }
        public List<double[]> Ranges { get; set; } = [];
    }

    private sealed record Node(int Code, double Latitude, double Longitude, HashSet<int> Styles);
    private sealed record Segment(int LineIndex, double LengthMeters);
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
            ArtifactIdentity = ArtifactIdentity,
            ArtifactGenerationId = ArtifactGenerationId,
            ArtifactSha256 = ArtifactSha256
        };
    }
}
