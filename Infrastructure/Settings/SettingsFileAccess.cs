using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BARS_Client_V2.Infrastructure.Settings;

internal static class SettingsFileAccess
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task WriteAllTextAtomicAsync(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(contents).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    internal static void WriteAllTextAtomic(string path, string contents) =>
        WriteAllTextAtomicAsync(path, contents).ConfigureAwait(false).GetAwaiter().GetResult();
}
