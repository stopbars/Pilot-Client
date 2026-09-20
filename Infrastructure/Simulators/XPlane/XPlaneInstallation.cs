using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using BARS_Client_V2.Infrastructure.Diagnostics;
using Microsoft.Win32;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal static class XPlaneInstallation
{
    private static string? _lastRoot;

    public static string? FindRoot()
    {
        foreach (var process in Process.GetProcessesByName("X-Plane"))
        {
            using (process)
            {
                try
                {
                    var running = Path.GetDirectoryName(process.MainModule?.FileName);
                    if (IsValid(running)) return Remember(running!);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }

        if (IsValid(_lastRoot)) return _lastRoot;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var configured = ReadConfiguredPath(Path.Combine(local, "BARS", "Installer", "settings.json"));
        if (IsValid(configured)) return Remember(configured!);
        foreach (var candidate in ReadInstallList(Path.Combine(local, "x-plane_install_12.txt")))
            if (IsValid(candidate)) return Remember(candidate);
        foreach (var steam in SteamRoots())
        {
            var candidates = new[] { steam }.Concat(ReadSteamLibraries(Path.Combine(steam, "steamapps", "libraryfolders.vdf")));
            foreach (var library in candidates)
            {
                var candidate = Path.Combine(library, "steamapps", "common", "X-Plane 12");
                if (IsValid(candidate)) return Remember(candidate);
            }
        }
        return null;
    }

    internal static bool IsValid(string? path) => !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "X-Plane.exe")) && Directory.Exists(Path.Combine(path, "Custom Scenery"));

    internal static string? ReadConfiguredPath(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var client = document.RootElement.EnumerateObject().FirstOrDefault(property =>
                property.Name.Equals("Pilot-Client", StringComparison.OrdinalIgnoreCase)).Value;
            if (client.ValueKind != JsonValueKind.Object) return null;
            return client.EnumerateObject().FirstOrDefault(property =>
                property.Name.Equals("xplanePath", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String).Value is { ValueKind: JsonValueKind.String } value
                    ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            ClientLog.Write($"Could not read X-Plane installation settings: {ex.Message}");
            return null;
        }
    }

    internal static string[] ReadInstallList(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path).Select(line => line.Trim().Trim('"')).Where(line => line.Length > 0).ToArray() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static string[] ReadSteamLibraries(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return Regex.Matches(File.ReadAllText(path), "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value.Replace(@"\\", @"\")).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? registered = null;
        if (OperatingSystem.IsWindows())
        {
            try { registered = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        if (!string.IsNullOrWhiteSpace(registered)) yield return registered;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
    }

    private static string Remember(string path)
    {
        path = XPlaneRemovalStorage.NormalizeRoot(path);
        if (!string.Equals(path, _lastRoot, StringComparison.OrdinalIgnoreCase))
            ClientLog.Write($"X-Plane installation resolved: {path}");
        return _lastRoot = path;
    }
}
