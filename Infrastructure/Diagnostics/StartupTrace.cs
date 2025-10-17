using System;
using System.IO;
using System.Threading;

namespace BARS_Client_V2.Infrastructure.Diagnostics;

internal static class StartupTrace
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? string.Empty,
        "BARS",
        "Client",
        "startup.log");

    public static void Reset()
    {
        TryWrite(_ =>
        {
            if (File.Exists(LogPath))
            {
                File.Delete(LogPath);
            }
        });
    }

    public static void Write(string message)
    {
        TryWrite(path =>
        {
            var line = $"{DateTime.UtcNow:O} [T{Environment.CurrentManagedThreadId}] {message}";
            File.AppendAllText(path, line + Environment.NewLine);
        });
    }

    private static void TryWrite(Action<string> action)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                action(path);
            }
        }
        catch
        {
            // Swallow logging errors; diagnostics should never crash the app.
        }
    }
}
