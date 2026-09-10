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

    public static Plan Prepare(string root, string stateRoot, string icao, string packageName, IReadOnlyCollection<XPlaneDsfSelector> selectors)
    {
        var state = Load(stateRoot);
        var previous = state.Airports.FirstOrDefault(a => a.Icao == icao);
        if (previous == null && selectors.Count == 0) return new Plan(false, () => { });
        List<Selection> selections = [];
        foreach (var group in selectors.GroupBy(selector => (selector.Source, selector.Sha256)))
        {
            foreach (var member in group) member.Validate();
            var selector = group.First();
            var candidates = Packages(root).Select(package => Path.Combine(package, selector.Source.Replace('/', Path.DirectorySeparatorChar))).Distinct(StringComparer.OrdinalIgnoreCase);
            var matches = candidates.Where(File.Exists).Where(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                var saved = state.Files.FirstOrDefault(f => f.SourcePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
                return (saved?.OriginalSha256 ?? XPlaneDsfPatcher.Hash(File.ReadAllBytes(path))) == selector.Sha256;
            }).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"Expected one active scenery source for {selector.Source}; found {matches.Length}. Regenerate removals for the installed package.");
            var target = matches[0];
            Resolve(root, Path.GetRelativePath(root, target));
            selections.AddRange(group.Select(member => new Selection { SourcePath = Path.GetRelativePath(root, target), Selector = member }));
        }
        if (previous != null) state.Airports.Remove(previous);
        if (selections.Count > 0) state.Airports.Add(new Airport { Icao = icao, PackageName = packageName, Selections = selections });
        var groups = state.Airports.SelectMany(a => a.Selections).GroupBy(s => s.SourcePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(s => s.Selector).ToArray(), StringComparer.OrdinalIgnoreCase);
        var affected = selections.Select(s => s.SourcePath).Concat(previous?.Selections.Select(s => s.SourcePath) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        List<(string Path, byte[] Bytes, string ExpectedHash)> writes = [];
        var changed = false;
        foreach (var relative in affected)
        {
            var target = Resolve(root, relative);
            var saved = state.Files.SingleOrDefault(f => f.SourcePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
            var current = File.ReadAllBytes(target);
            var currentHash = XPlaneDsfPatcher.Hash(current);
            byte[] original;
            if (saved == null)
            {
                original = current;
                saved = new SourceFile { SourcePath = relative, OriginalSha256 = currentHash, PatchedSha256 = currentHash };
                state.Files.Add(saved);
            }
            else
            {
                if (currentHash != saved.PatchedSha256) throw new IOException($"{relative} changed outside BARS. Its original backup has been retained.");
                original = File.ReadAllBytes(Backup(stateRoot, saved.OriginalSha256));
                if (XPlaneDsfPatcher.Hash(original) != saved.OriginalSha256) throw new InvalidDataException("DSF backup checksum mismatch.");
            }
            var activeSelectors = groups.GetValueOrDefault(relative) ?? [];
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
    private static IEnumerable<string> Packages(string root)
    {
        var ini = Path.Combine(root, "Custom Scenery", "scenery_packs.ini");
        if (File.Exists(ini))
            foreach (var line in File.ReadLines(ini))
                if (line.StartsWith("SCENERY_PACK ", StringComparison.Ordinal))
                    yield return Resolve(root, line[13..].Trim().Replace('/', Path.DirectorySeparatorChar));
        yield return Path.Combine(root, "Global Scenery", "Global Airports");
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
