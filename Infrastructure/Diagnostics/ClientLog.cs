using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace BARS_Client_V2.Infrastructure.Diagnostics;

internal static class ClientLog
{
    private static readonly RollingClientLog Writer = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BARS", "Client"));

    public static void StartSession()
    {
        Write($"Session started; version={Assembly.GetExecutingAssembly().GetName().Version}; " +
              $"runtime={Environment.Version}; OS={Environment.OSVersion}; " +
              $"culture={CultureInfo.CurrentCulture.Name}; UI culture={CultureInfo.CurrentUICulture.Name}");
    }

    public static void Write(string message) => Writer.Write(message);
}

internal sealed class RollingClientLog(string directory, long maxBytes = 5 * 1024 * 1024, int retainedFiles = 28)
{
    private readonly object _gate = new();
    private readonly string _session = Guid.NewGuid().ToString("N")[..8];
    private string? _currentPath;
    private string? _currentDay;
    private int _segment;
    private static readonly Regex Secrets = new(
        @"BARS_[A-Za-z0-9_+/.=\-]+|(?<prefix>[?&](?:key|token|api[_-]?key|access_token|authorization)=)[^&#\s""'<>]+|(?<prefix>Bearer\s+)[^\s""'<>]+|(?<prefix>""(?:apiToken|apiKey|access_token|token|authorization)""\s*:\s*"")[^""]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static string Redact(string message) => Secrets.Replace(message,
        match => match.Groups["prefix"].Value + "[REDACTED]");

    public void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                var now = DateTime.UtcNow;
                var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var line = $"{now:O} [P{Environment.ProcessId}/T{Environment.CurrentManagedThreadId}/{_session}] {Redact(message)}{Environment.NewLine}";
                if (_currentDay != day)
                {
                    _currentDay = day;
                    _segment = 0;
                    _currentPath = null;
                }
                var rotate = _currentPath == null ||
                    (File.Exists(_currentPath) && new FileInfo(_currentPath).Length + Encoding.UTF8.GetByteCount(line) > maxBytes);
                if (rotate)
                {
                    do
                    {
                        _currentPath = Path.Combine(directory, _segment == 0
                            ? $"client-{day}.log" : $"client-{day}-{_segment:D3}.log");
                        _segment++;
                    }
                    while (File.Exists(_currentPath) && new FileInfo(_currentPath).Length + Encoding.UTF8.GetByteCount(line) > maxBytes);
                }
                File.AppendAllText(_currentPath!, line, new UTF8Encoding(false));
                if (rotate) Prune(now);
            }
        }
        catch
        {
            // Logging must not interrupt simulator or scenery operations.
        }
    }

    private void Prune(DateTime now)
    {
        var logs = Directory.EnumerateFiles(directory, "client-*.log")
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^client-\d{4}-\d{2}-\d{2}(?:-\d{3,})?\.log$"))
            .Select(path => new FileInfo(path))
            .Where(file => !file.FullName.Equals(_currentPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        for (var index = 0; index < logs.Length; index++)
        {
            if (index >= retainedFiles - 1 || logs[index].LastWriteTimeUtc < now.AddDays(-14))
            {
                try { logs[index].Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
