using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace BARS_Client_V2.Infrastructure.Simulators.XPlane;

internal sealed class XPlanePatchTransaction
{
    private static readonly AsyncLocal<XPlanePatchTransaction?> Active = new();
    internal static readonly AsyncLocal<Func<bool>?> ProcessProbeForTests = new();

    internal static void EnsureSimulatorClosed()
    {
        bool running;
        try { running = (ProcessProbeForTests.Value ?? XPlaneLocalRemovalsService.IsXPlaneProcessRunning)(); }
        catch { running = true; }
        if (running) throw new IOException("X-Plane started before BARS finished changing scenery. Close X-Plane and let BARS finish applying or recovering removals before restarting it. Pending recovery backups have been retained.");
    }
    private readonly string _root;
    private readonly string _stateRoot;
    private readonly string _directory;
    private readonly Journal _journal;
    private XPlanePatchTransaction(string root, string stateRoot, Journal journal)
    {
        _root = Path.GetFullPath(root);
        _stateRoot = Path.GetFullPath(stateRoot);
        _directory = Path.Combine(_stateRoot, "transaction");
        _journal = journal;
    }

    public static bool HasPending(string stateRoot) => File.Exists(Path.Combine(stateRoot, "transaction", "journal.json"));

    public static bool IsActive => Active.Value != null;

    public static void Recover(string root, string stateRoot)
    {
        var path = Path.Combine(stateRoot, "transaction", "journal.json");
        if (!File.Exists(path)) return;
        EnsureSimulatorClosed();
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid X-Plane transaction journal.");
        if (journal.Version != 1) throw new InvalidDataException("Unsupported X-Plane transaction journal.");
        var transaction = new XPlanePatchTransaction(root, stateRoot, journal);
        if (!journal.Committed) transaction.Rollback();
        transaction.Cleanup();
    }

    public static async Task<T> RunAsync<T>(string root, string stateRoot, Func<Task<T>> action)
    {
        if (Active.Value != null) return await action().ConfigureAwait(false);
        EnsureSimulatorClosed();
        Recover(root, stateRoot);
        var transaction = new XPlanePatchTransaction(root, stateRoot, new Journal());
        Active.Value = transaction;
        try
        {
            var result = await action().ConfigureAwait(false);
            if (transaction._journal.Entries.Count == 0) return result;
            EnsureSimulatorClosed();
            transaction._journal.Committed = true;
            try { transaction.Save(); }
            catch { transaction._journal.Committed = false; throw; }
            transaction.Cleanup();
            return result;
        }
        catch
        {
            // A committed journal is authoritative even if cleanup failed.
            if (!transaction._journal.Committed) { transaction.Rollback(); transaction.Cleanup(); }
            throw;
        }
        finally { Active.Value = null; }
    }

    public static void BeforeWrite(string path, byte[]? replacement)
    {
        EnsureSimulatorClosed();
        Active.Value?.Enlist(path, replacement == null ? "" : Hash(replacement));
        EnsureSimulatorClosed();
    }

    public static void BeforeMove(string source, string destination)
    {
        EnsureSimulatorClosed();
        if (Active.Value != null) Active.Value.Enlist(destination, FileHash(source));
        EnsureSimulatorClosed();
    }

    public static void Write(string path, byte[] bytes)
    {
        BeforeWrite(path, bytes);
        AtomicWrite(path, bytes);
    }

    private void Enlist(string path, string nextHash)
    {
        path = Path.GetFullPath(path);
        ValidatePath(path);
        Directory.CreateDirectory(_directory);
        var currentHash = File.Exists(path) ? FileHash(path) : "";
        var entry = _journal.Entries.FirstOrDefault(entry => Resolve(entry).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            var inRoot = IsWithin(path, _root);
            entry = new Entry { Area = inRoot ? "scenery" : "state", RelativePath = Path.GetRelativePath(inRoot ? _root : _stateRoot, path), OriginalHash = currentHash, CurrentHash = currentHash };
            if (currentHash.Length > 0)
            {
                entry.Backup = Guid.NewGuid().ToString("N") + ".bak";
                var bytes = File.ReadAllBytes(path);
                if (Hash(bytes) != currentHash) throw new IOException("X-Plane source changed while preparing a transaction.");
                AtomicWrite(Path.Combine(_directory, entry.Backup), bytes);
            }
            _journal.Entries.Add(entry);
        }
        else if (currentHash != entry.CurrentHash && !entry.ExpectedHashes.Contains(currentHash))
            throw new IOException("An X-Plane file changed outside the current BARS transaction.");
        entry.CurrentHash = currentHash;
        if (!entry.ExpectedHashes.Contains(nextHash)) entry.ExpectedHashes.Add(nextHash);
        Save();
    }

    private void Rollback()
    {
        EnsureSimulatorClosed();
        // Validate every backup and target before restoring the first file.
        foreach (var entry in _journal.Entries)
        {
            var target = Resolve(entry);
            var current = File.Exists(target) ? FileHash(target) : "";
            if (current != entry.OriginalHash && current != entry.CurrentHash && !entry.ExpectedHashes.Contains(current))
                throw new IOException($"Cannot recover {entry.RelativePath}: it changed outside BARS. Its transaction backup was retained.");
            if (entry.OriginalHash.Length > 0 && FileHash(BackupPath(entry)) != entry.OriginalHash)
                throw new InvalidDataException("X-Plane transaction backup checksum mismatch.");
        }
        foreach (var entry in Enumerable.Reverse(_journal.Entries))
        {
            var target = Resolve(entry);
            if ((File.Exists(target) ? FileHash(target) : "") == entry.OriginalHash) continue;
            EnsureSimulatorClosed();
            if (entry.OriginalHash.Length == 0) { if (File.Exists(target)) File.Delete(target); }
            else AtomicWrite(target, File.ReadAllBytes(BackupPath(entry)));
        }
    }

    private string BackupPath(Entry entry)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Backup, "^[a-f0-9]{32}\\.bak$")) throw new InvalidDataException("Invalid transaction backup path.");
        return Path.Combine(_directory, entry.Backup);
    }

    private string Resolve(Entry entry)
    {
        var area = entry.Area == "scenery" ? _root : entry.Area == "state" ? _stateRoot : throw new InvalidDataException("Invalid transaction area.");
        var path = Path.GetFullPath(Path.Combine(area, entry.RelativePath));
        if (!IsWithin(path, area)) throw new InvalidDataException("Transaction path escapes its root.");
        ValidatePath(path);
        return path;
    }

    private void ValidatePath(string path)
    {
        if (!IsWithin(path, _root) && !IsWithin(path, _stateRoot)) throw new InvalidDataException("X-Plane transaction path escapes its roots.");
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("X-Plane source patches do not follow filesystem links.");
        }
    }

    private static bool IsWithin(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void Save() => AtomicWrite(Path.Combine(_directory, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(_journal));
    private void Cleanup()
    {
        if (!Directory.Exists(_directory)) return;
        File.Delete(Path.Combine(_directory, "journal.json"));
        foreach (var entry in _journal.Entries.Where(entry => entry.Backup.Length > 0)) File.Delete(BackupPath(entry));
    }

    internal static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            EnsureSimulatorClosed();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private sealed class Journal
    {
        public int Version { get; set; } = 1;
        public bool Committed { get; set; }
        public List<Entry> Entries { get; set; } = [];
    }
    private sealed class Entry
    {
        public string Area { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string Backup { get; set; } = "";
        public string OriginalHash { get; set; } = "";
        public string CurrentHash { get; set; } = "";
        public List<string> ExpectedHashes { get; set; } = [];
    }
}
