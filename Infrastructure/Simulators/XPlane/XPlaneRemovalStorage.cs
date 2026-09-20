using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BARS_Client_V2.Infrastructure.Diagnostics;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal static class XPlaneRemovalStorage
{
    private static readonly ConcurrentDictionary<string, string> Roots = new(StringComparer.OrdinalIgnoreCase);

    internal static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    internal static string GetStateRoot(string root) => Roots.GetOrAdd(NormalizeRoot(root), normalized =>
        Resolve(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BARS", "Client", "XPlaneRemovals"), normalized));

    internal static string Resolve(string storage, string root)
    {
        var normalized = NormalizeRoot(root);
        var canonical = DirectoryFor(storage, normalized);
        var legacy = DirectoryFor(storage, Path.EndsInDirectorySeparator(normalized)
            ? normalized : normalized + Path.DirectorySeparatorChar);
        if (canonical == legacy || !Directory.Exists(legacy)) return canonical;
        if (!Directory.Exists(canonical)) return legacy;

        // Older clients could patch under one spelling, then treat the same scenery
        // as pristine under the other. Keep the directory that owns those patches.
        var canonicalActive = HasRecoveryState(canonical);
        var legacyActive = HasRecoveryState(legacy);
        if (canonicalActive && legacyActive)
            throw new InvalidDataException(
                $"Two saved X-Plane removal histories need reconciliation. BARS preserved both and left scenery unchanged: {canonical}; {legacy}");
        var selected = legacyActive ? legacy : canonical;
        ClientLog.Write($"X-Plane removal history resolved: root={normalized}; state={selected}; alternate backups preserved");
        return selected;
    }

    internal static string DirectoryFor(string storage, string root)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))
            .ToLowerInvariant()[..16];
        return Path.Combine(storage, hash);
    }

    private static bool HasRecoveryState(string directory)
    {
        var active = File.Exists(Path.Combine(directory, "transaction", "journal.json"));
        foreach (var (file, schemas) in new[]
        {
            ("state.json", new[] { "bars-xplane-source-patches/v1", "bars-xplane-source-patches/v2" }),
            ("dsf-state.json", new[] { "bars-xplane-dsf-patches/v1" })
        })
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var state = document.RootElement;
            if (!state.TryGetProperty("schema", out var schema) || !schemas.Contains(schema.GetString()) ||
                !state.TryGetProperty("airports", out var airports) || airports.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"The saved X-Plane removal history is invalid. BARS preserved it: {path}");
            active |= airports.GetArrayLength() > 0;
        }
        return active;
    }
}
