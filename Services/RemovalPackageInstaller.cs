using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BARS_Client_V2.Services;

internal static class RemovalPackageInstaller
{
    internal sealed class RecoveryException(string message, IEnumerable<Exception> errors)
        : AggregateException(message, errors);
    // A directory handle can prevent renaming the package even when its files are writable.
    // Keep the root in place and retain each replaced file until the whole update succeeds.
    internal static void ReplaceFiles(string stagedPackage, string destination, string backup)
    {
        var changes = new List<(string Destination, string Backup, bool Existed)>();
        var stagedFiles = Directory.GetFiles(stagedPackage, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetFileName(path).Equals("layout.json", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var incoming = stagedFiles.Select(path => Path.GetRelativePath(stagedPackage, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldFiles = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);

        try
        {
            foreach (var stagedFile in stagedFiles)
            {
                var relative = Path.GetRelativePath(stagedPackage, stagedFile);
                var target = Path.Combine(destination, relative);
                var saved = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                var existed = File.Exists(target);
                // Copy to a sibling so the staged package remains available for a retry.
                var pending = target + "." + Guid.NewGuid().ToString("N") + ".pending";
                try
                {
                    File.Copy(stagedFile, pending);
                    if (existed) File.Replace(pending, target, saved);
                    else File.Move(pending, target);
                    changes.Add((target, saved, existed));
                }
                finally
                {
                    if (File.Exists(pending)) File.Delete(pending);
                }
            }

            // Includes obsolete .bgl.disabled files; settings are reapplied after installation.
            foreach (var oldFile in oldFiles)
            {
                var relative = Path.GetRelativePath(destination, oldFile);
                if (incoming.Contains(relative)) continue;
                var saved = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Move(oldFile, saved);
                changes.Add((oldFile, saved, true));
            }
        }
        catch (Exception installError)
        {
            var errors = new List<Exception> { installError };
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.Existed) File.Move(change.Backup, change.Destination, overwrite: true);
                    else File.Delete(change.Destination);
                }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            if (errors.Count > 1)
                throw new RecoveryException($"Removal update rollback failed. Backups retained at {backup}.", errors);
            throw;
        }
    }
}
