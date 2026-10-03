using System.IO;
using System.Text.Json;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal static class XPlaneDsfRemovals
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string StatePath(string stateRoot) => Path.Combine(stateRoot, "dsf-state.json");
    private static State Load(string stateRoot)
    {
        var path = StatePath(stateRoot);
        var state = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("Invalid DSF removal state.") : new State();
        if (state.Schema != "bars-xplane-dsf-patches/v1") throw new InvalidDataException("Unsupported DSF removal state.");
        return state;
    }

    public static IReadOnlyList<string> TestingAirports(string stateRoot) => Load(stateRoot).Airports.Where(a => a.PackageName == "Testing").Select(a => a.Icao).ToArray();
    public static bool HasAirport(string stateRoot, string icao) => Load(stateRoot).Airports.Any(a => a.Icao == icao);
    public static bool IsApplied(string root, string stateRoot, string icao)
    {
        var state = Load(stateRoot);
        var airport = state.Airports.FirstOrDefault(a => a.Icao == icao);
        return airport != null && airport.Selections.All(selection =>
        {
            var file = state.Files.SingleOrDefault(f => f.SourcePath == selection.SourcePath);
            return file != null && File.Exists(Resolve(root, file.SourcePath)) && XPlaneDsfPatcher.Hash(File.ReadAllBytes(Resolve(root, file.SourcePath))) == file.PatchedSha256;
        });
    }

    internal sealed record Plan(bool Changed, Action Apply);


    internal static string[]? AvailabilityFiles(string root, string stateRoot, string icao, string packageName)
    {
        var state = Load(stateRoot);
        var airport = state.Airports.FirstOrDefault(a => a.Icao == icao && a.PackageName == packageName);
        if (airport == null) return null;
        var files = new List<string> { StatePath(stateRoot) };
        foreach (var selection in airport.Selections)
        {
            var source = state.Files.Single(f => f.SourcePath == selection.SourcePath);
            files.Add(Resolve(root, source.SourcePath));
            files.Add(Backup(stateRoot, source.OriginalSha256));
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static Plan Prepare(string root, string stateRoot, string icao, string packageName, IReadOnlyCollection<XPlaneDsfSelector> selectors)
    {
        selectors = selectors.Select(XPlaneDsfPatcher.WithReferenceGeometry).ToArray();
        var state = Load(stateRoot);
        var previous = state.Airports.FirstOrDefault(a => a.Icao == icao);
        if (previous == null && selectors.Count == 0) return new Plan(false, () => { });
        List<Selection> selections = [];
        foreach (var group in selectors.GroupBy(selector => (selector.Source, selector.Sha256)))
        {
            foreach (var member in group) member.Validate();
            var selector = group.First();
            var candidates = XPlaneSceneryPaths.Packages(root)
                .Select(package => Path.Combine(package, selector.Source.Replace('/', Path.DirectorySeparatorChar)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
            var matches = candidates.Where(File.Exists).Where(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                var saved = state.Files.FirstOrDefault(f => f.SourcePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                return (saved?.OriginalSha256 ?? XPlaneDsfPatcher.Hash(File.ReadAllBytes(path))) == selector.Sha256;
            }).ToArray();
            XPlaneDsfSelector[]? resolved = null;
            if (matches.Length == 0 && group.All(member => member.Geometry != null))
            {
                var geometryMatches = new List<(string Path, XPlaneDsfSelector[] Selectors)>();
                foreach (var path in candidates)
                {
                    if (!XPlaneSceneryPaths.CanPatch(root, path)) continue;
                    var relative = Path.GetRelativePath(root, path);
                    var saved = state.Files.SingleOrDefault(file => file.SourcePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                    var current = File.ReadAllBytes(Resolve(root, relative));
                    var original = ReadOriginal(stateRoot, relative, current, saved);
                    try { geometryMatches.Add((path, XPlaneDsfPatcher.ResolveSelectors(original, group.ToArray()))); }
                    catch (XPlaneRemovalMismatchException) { }
                }
                matches = geometryMatches.Select(match => match.Path).ToArray();
                if (geometryMatches.Count == 1) resolved = geometryMatches[0].Selectors;
            }
            if (matches.Length != 1) throw new XPlaneRemovalMismatchException($"Expected one active scenery source for {selector.Source}; found {matches.Length}. Regenerate removals for the installed package.");
            var target = matches[0];
            if (!XPlaneSceneryPaths.CanPatch(root, target))
                throw new XPlaneRemovalMismatchException($"The matching scenery file is outside the X-Plane folder or uses a filesystem link: {target}. BARS left it unchanged.");
            Resolve(root, Path.GetRelativePath(root, target));
            selections.AddRange((resolved ?? group.ToArray()).Select(member => new Selection { SourcePath = Path.GetRelativePath(root, target), Selector = member }));
        }
        if (previous != null) state.Airports.Remove(previous);
        if (selections.Count > 0) state.Airports.Add(new Airport { Icao = icao, PackageName = packageName, Selections = selections });
        var groups = state.Airports.SelectMany(a => a.Selections).GroupBy(s => s.SourcePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var affected = selections.Select(s => s.SourcePath).Concat(previous?.Selections.Select(s => s.SourcePath) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        List<(string Path, byte[] Bytes, string ExpectedHash)> writes = [];
        var changed = false;
        foreach (var relative in affected)
        {
            var target = Resolve(root, relative);
            var saved = state.Files.SingleOrDefault(f => f.SourcePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
            var current = File.ReadAllBytes(target);
            var currentHash = XPlaneDsfPatcher.Hash(current);
            var original = ReadOriginal(stateRoot, relative, current, saved);
            var activeSelections = groups.GetValueOrDefault(relative) ?? [];
            if (saved == null)
            {
                saved = new SourceFile { SourcePath = relative, OriginalSha256 = currentHash, PatchedSha256 = currentHash };
                state.Files.Add(saved);
            }
            else if (XPlaneDsfPatcher.Hash(original) != saved.OriginalSha256)
            {
                var legacy = activeSelections.Where(s => s.Selector.Sha256 == saved.OriginalSha256 && s.Selector.Geometry == null).ToArray();
                if (legacy.Length > 0)
                {
                    var captured = XPlaneDsfPatcher.ResolveSelectors(ReadBackup(stateRoot, saved.OriginalSha256), legacy.Select(s => s.Selector).ToArray(), captureGeometry: true);
                    for (var index = 0; index < legacy.Length; index++) legacy[index].Selector = captured[index];
                }
                // Keep the old backup and restore this installed version when removals are disabled.
                saved.OriginalSha256 = currentHash;
            }
            var activeSelectors = XPlaneDsfPatcher.ResolveSelectors(original, activeSelections.Select(s => s.Selector).ToArray());
            for (var index = 0; index < activeSelections.Length; index++) activeSelections[index].Selector = activeSelectors[index];
            var patched = XPlaneDsfPatcher.Patch(original, activeSelectors);
            var backup = Backup(stateRoot, saved.OriginalSha256);
            if (File.Exists(backup))
            {
                if (XPlaneDsfPatcher.Hash(File.ReadAllBytes(backup)) != saved.OriginalSha256) throw new InvalidDataException("DSF backup checksum mismatch.");
            }
            else writes.Add((backup, original, ""));
            if (XPlaneDsfPatcher.Hash(patched) != currentHash)
            {
                writes.Add((target, patched, currentHash));
                changed = true;
            }
            saved.PatchedSha256 = XPlaneDsfPatcher.Hash(patched);
            if (activeSelectors.Length == 0) state.Files.Remove(saved);
        }
        writes.Add((StatePath(stateRoot), JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), File.Exists(StatePath(stateRoot)) ? XPlaneDsfPatcher.Hash(File.ReadAllBytes(StatePath(stateRoot))) : ""));
        // Preparation completes for every shared tile before the first target write.
        return new Plan(changed, () =>
        {
            foreach (var write in writes)
                if ((File.Exists(write.Path) ? XPlaneDsfPatcher.Hash(File.ReadAllBytes(write.Path)) : "") != write.ExpectedHash)
                    throw new IOException("A DSF target or its removal state changed after preparation.");
            foreach (var write in writes)
                if (XPlaneDsfPatcher.Hash(write.Bytes) != write.ExpectedHash) XPlanePatchTransaction.Write(write.Path, write.Bytes);
        });
    }

    private static byte[] ReadOriginal(string stateRoot, string relative, byte[] current, SourceFile? saved)
    {
        if (saved == null) return current;
        var original = ReadBackup(stateRoot, saved.OriginalSha256);
        if (XPlaneDsfPatcher.Hash(current) == saved.PatchedSha256) return original;
        if (XPlaneDsfPatcher.HasSameSceneryContent(original, current)) return current;
        throw new XPlaneRemovalMismatchException($"{relative} changed outside BARS. Its original backup has been retained.");
    }

    private static byte[] ReadBackup(string stateRoot, string hash)
    {
        var bytes = File.ReadAllBytes(Backup(stateRoot, hash));
        if (XPlaneDsfPatcher.Hash(bytes) != hash) throw new InvalidDataException("DSF backup checksum mismatch.");
        return bytes;
    }

    private static string Backup(string stateRoot, string hash)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid DSF backup identity.");
        return Path.Combine(stateRoot, "dsf-backups", hash + ".dsf");
    }
    private static string Resolve(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DSF path escapes the simulator root.");
        for (var path = full; path != null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("DSF patches do not follow filesystem links.");
        return full;
    }
    private sealed class State
    {
        public string Schema { get; set; } = "bars-xplane-dsf-patches/v1";
        public List<Airport> Airports { get; set; } = [];
        public List<SourceFile> Files { get; set; } = [];
    }
    private sealed class Airport
    {
        public string Icao { get; set; } = "";
        public string PackageName { get; set; } = "";
        public List<Selection> Selections { get; set; } = [];
    }
    private sealed class Selection
    {
        public string SourcePath { get; set; } = "";
        public XPlaneDsfSelector Selector { get; set; } = new();
    }
    private sealed class SourceFile
    {
        public string SourcePath { get; set; } = "";
        public string OriginalSha256 { get; set; } = "";
        public string PatchedSha256 { get; set; } = "";
    }
}
