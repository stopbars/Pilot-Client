using System.IO;
using BARS_Client_V2.Infrastructure.Diagnostics;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal static class XPlaneSceneryPaths
{
    internal static IEnumerable<string> Packages(string root)
    {
        var global = Path.Combine(root, "Global Scenery", "Global Airports");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ini = Path.Combine(root, "Custom Scenery", "scenery_packs.ini");
        if (File.Exists(ini))
        {
            foreach (var line in File.ReadLines(ini))
            {
                var entry = line.Trim();
                const string prefix = "SCENERY_PACK ";
                if (!entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                entry = entry[prefix.Length..].Trim().Trim('"');
                if (entry.Length == 0) continue;
                var path = entry == "*GLOBAL_AIRPORTS*" ? global : Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar));
                string? resolved = null;
                try { resolved = Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    ClientLog.Write($"Skipping invalid X-Plane scenery entry '{entry}': {ex.Message}");
                }
                if (resolved != null && seen.Add(resolved)) yield return resolved;
            }
        }
        if (seen.Add(Path.GetFullPath(global))) yield return Path.GetFullPath(global);
    }

    internal static bool CanPatch(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;
        for (var current = full; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        return true;
    }
}
