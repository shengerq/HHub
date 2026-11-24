using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hhub.PluginContracts;

namespace Hhub.UnifiedHost;

public sealed partial class PluginStore
{
    private async Task<InstalledPackage> InstallTransactionAsync(string source, Func<InstalledPackage, Task>? verifyUpdate, string? expectedArchiveHash)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        string staging = Path.Combine(Root, "staging", Guid.NewGuid().ToString("N"));
        try
        {
            using var mutation = AcquireMutationLease();
            Directory.CreateDirectory(staging); EnsurePlainDirectory(staging);
            string archivePath = Path.Combine(staging, "package.hhub");
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > CertifiedPackages.MaxPackageBytes) throw new InvalidDataException("package.too-large");
                using var output = File.Create(archivePath); await input.CopyToAsync(output).ConfigureAwait(false);
            }
            var verified = CertifiedPackages.Verify(archivePath, _policy);
            if (expectedArchiveHash is not null && !verified.Sha256.Equals(expectedArchiveHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("plugin.archive-changed");
            string id = verified.Definition.Id;
            using var session = AcquirePluginLease(id);
            string pluginRoot = Path.Combine(Root, "plugins", id);
            if (Directory.Exists(pluginRoot)) EnsurePlainDirectory(pluginRoot);
            string pendingPath = Path.Combine(pluginRoot, "pending.json");
            if (File.Exists(pendingPath)) throw new InvalidDataException("plugin.update-recovery-required");
            string receiptPath = Path.Combine(pluginRoot, "current.json");
            InstallReceipt? previous = File.Exists(receiptPath) ? ReadReceipt(receiptPath) : null;
            if (previous is not null)
            {
                VerifyPayload(Path.Combine(pluginRoot, previous.Hash), previous.Hash);
                if (Version.Parse(verified.Definition.Version) <= Version.Parse(previous.Version))
                    throw new InvalidDataException("plugin.version-not-newer");
            }
            if (ReadAllPresets(id).Any(preset => preset.Values.Any(pair =>
                verified.Definition.Capabilities.SingleOrDefault(item => item.Id == pair.Key) is not { } capability
                || !capability.Value.Accepts(pair.Value)))) throw new InvalidDataException("preset.migration-required");
            string payload = Path.Combine(staging, "payload"); ZipFile.ExtractToDirectory(archivePath, payload);
            VerifyPayload(payload, verified.ContentHash);
            Directory.CreateDirectory(pluginRoot);
            string destination = Path.Combine(pluginRoot, verified.ContentHash);
            if (Directory.Exists(destination)) DeleteOwnedTree(destination);
            Directory.Move(payload, destination);
            var candidate = new InstalledPackage(verified.Definition, destination, verified.ContentHash);
            if (previous is null)
            {
                // A first install never starts code or creates plugin data.
                WriteAtomic(receiptPath, new InstallReceipt(candidate.Definition.Version, candidate.PackageHash));
                return candidate;
            }

            string transactionId = Guid.NewGuid().ToString("N");
            string transaction = Path.Combine(Root, "transactions", id, transactionId);
            Directory.CreateDirectory(transaction); EnsurePlainDirectory(transaction);
            var journal = new UpdateJournal(1, transactionId, previous,
                new(candidate.Definition.Version, candidate.PackageHash), Directory.Exists(DataDirectory(id)));
            try
            {
                SnapshotData(id, transaction, journal.HadData);
                WriteAtomic(pendingPath, journal);
                try
                {
                    if (verifyUpdate is not null) await verifyUpdate(candidate).ConfigureAwait(false);
                    else await PluginSession.ProbeCandidateAsync(this, candidate, _managedHostPath).ConfigureAwait(false);
                    // Startup may migrate data. Never commit an unreadable preset format.
                    if (ReadAllPresets(id).Any(preset => preset.Values.Any(pair =>
                        verified.Definition.Capabilities.SingleOrDefault(item => item.Id == pair.Key) is not { } capability
                        || !capability.Value.Accepts(pair.Value)))) throw new InvalidDataException("preset.migration-required");
                    VerifyPayload(destination, candidate.PackageHash);
                    WriteAtomic(receiptPath, journal.Candidate);
                    File.Delete(pendingPath); // Commit point: no user operation can precede this.
                }
                catch (Exception updateError)
                {
                    try { RestorePending(id, journal); }
                    catch (Exception recoveryError)
                    { throw new AggregateException("plugin.update-recovery-required", updateError, recoveryError); }
                    throw;
                }
            }
            finally
            {
                // A pending journal keeps its backup for a later recovery attempt.
                if (!File.Exists(pendingPath) && Directory.Exists(transaction)) DeleteOwnedTree(transaction);
            }
            CleanupVersions(pluginRoot, journal.Candidate.Hash, previous.Hash);
            return candidate;
        }
        finally
        {
            try { if (Directory.Exists(staging)) DeleteOwnedTree(staging); }
            finally { _gate.Release(); }
        }
    }

    public string[] RecoverInterruptedUpdates()
    {
        var issues = new List<string>();
        using var mutation = AcquireMutationLease();
        foreach (string folder in Directory.GetDirectories(Path.Combine(Root, "plugins")))
        {
            string id = Path.GetFileName(folder);
            try
            {
                EnsurePlainDirectory(folder);
                string pending = Path.Combine(folder, "pending.json"); if (!File.Exists(pending)) continue;
                if (!PluginApi.ValidId(id)) throw new InvalidDataException("plugin.id-invalid");
                using var lease = AcquirePluginLease(id);
                if (new FileInfo(pending).Length > 16384) throw new InvalidDataException("plugin.journal-too-large");
                RestorePending(id, PluginApi.Read<UpdateJournal>(File.ReadAllText(pending)));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException or UnauthorizedAccessException)
            { issues.Add(id + ": " + error.Message); }
        }
        string staging = Path.Combine(Root, "staging");
        if (Directory.Exists(staging))
        {
            try
            {
                EnsurePlainDirectory(staging);
                foreach (string path in Directory.GetDirectories(staging))
                    if (Guid.TryParseExact(Path.GetFileName(path), "N", out _)) DeleteOwnedTree(path);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { issues.Add("staging: " + error.Message); }
        }
        // Snapshots created before the journal, or left after its commit, have no
        // recovery role. Keep every snapshot when a journal is still pending.
        string transactions = Path.Combine(Root, "transactions");
        if (Directory.Exists(transactions))
        {
            try
            {
                EnsurePlainDirectory(transactions);
                foreach (string plugin in Directory.GetDirectories(transactions))
                {
                    string id = Path.GetFileName(plugin);
                    if (!PluginApi.ValidId(id) || File.Exists(Path.Combine(Root, "plugins", id, "pending.json"))) continue;
                    using var lease = AcquirePluginLease(id);
                    EnsurePlainDirectory(plugin);
                    foreach (string path in Directory.GetDirectories(plugin))
                        if (Guid.TryParseExact(Path.GetFileName(path), "N", out _)) DeleteOwnedTree(path);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { issues.Add("transactions: " + error.Message); }
        }
        RecoveryIssues = issues.ToArray(); return RecoveryIssues;
    }

    private void RestorePending(string id, UpdateJournal journal)
    {
        if (journal.SchemaVersion != 1 || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !IsHash(journal.Previous.Hash) || !IsHash(journal.Candidate.Hash)
            || !Version.TryParse(journal.Previous.Version, out _) || !Version.TryParse(journal.Candidate.Version, out _))
            throw new InvalidDataException("plugin.journal-invalid");
        string folder = Path.Combine(Root, "plugins", id);
        var original = VerifyPayload(Path.Combine(folder, journal.Previous.Hash), journal.Previous.Hash);
        if (original.Definition.Id != id || original.Definition.Version != journal.Previous.Version)
            throw new InvalidDataException("plugin.recovery-identity-mismatch");
        string transaction = Path.Combine(Root, "transactions", id, journal.TransactionId);
        EnsurePlainDirectory(transaction);
        string data = DataDirectory(id), backup = Path.Combine(transaction, "data");
        VerifySnapshot(transaction, journal.HadData);
        string restored = Path.Combine(transaction, "restored");
        if (Directory.Exists(restored)) DeleteOwnedTree(restored);
        if (journal.HadData) CopyPlainTree(backup, restored);
        string displaced = Path.Combine(transaction, "discarded-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(data)) { EnsurePlainDirectory(data); Directory.Move(data, displaced); }
        try
        {
            if (journal.HadData) { Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.Move(restored, data); }
            WriteAtomic(Path.Combine(folder, "current.json"), journal.Previous);
        }
        catch
        {
            if (!Directory.Exists(data) && Directory.Exists(displaced)) Directory.Move(displaced, data);
            throw;
        }
        File.Delete(Path.Combine(folder, "pending.json"));
        string failed = Path.Combine(folder, journal.Candidate.Hash);
        if (journal.Candidate.Hash != journal.Previous.Hash && Directory.Exists(failed)) DeleteOwnedTree(failed);
        DeleteOwnedTree(transaction);
    }

    internal IDisposable AcquireSessionLease(InstalledPackage plugin)
    {
        using var mutation = AcquireMutationLease();
        string folder = Path.Combine(Root, "plugins", plugin.Definition.Id);
        if (File.Exists(Path.Combine(folder, "pending.json"))) throw new InvalidDataException("plugin.update-recovery-required");
        var receipt = ReadReceipt(Path.Combine(folder, "current.json"));
        if (receipt.Hash != plugin.PackageHash) throw new InvalidDataException("plugin.version-changed");
        return AcquirePluginLease(plugin.Definition.Id);
    }

    private FileStream AcquireMutationLease() => OpenLease(Path.Combine(Root, ".mutation.lock"));
    private FileStream AcquirePluginLease(string id)
    {
        if (!PluginApi.ValidId(id)) throw new InvalidDataException("plugin.id-invalid");
        string locks = Path.Combine(Root, "locks"); Directory.CreateDirectory(locks); EnsurePlainDirectory(locks);
        return OpenLease(Path.Combine(locks, id + ".lock"));
    }
    private static FileStream OpenLease(string path)
    {
        EnsurePlainDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("storage.link-not-allowed");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static InstallReceipt ReadReceipt(string path)
    {
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("plugin.receipt-invalid");
        var receipt = PluginApi.Read<InstallReceipt>(File.ReadAllText(path));
        if (!IsHash(receipt.Hash) || !Version.TryParse(receipt.Version, out _)) throw new InvalidDataException("plugin.receipt-invalid");
        return receipt;
    }

    private void SnapshotData(string id, string transaction, bool exists)
    {
        string backup = Path.Combine(transaction, "data");
        if (exists) CopyPlainTree(DataDirectory(id), backup);
        var files = exists ? SnapshotIndex(backup) : [];
        WriteAtomic(Path.Combine(transaction, "snapshot.json"), new DataSnapshot(1, exists, files));
    }
    private static PackageFile[] SnapshotIndex(string root)
    {
        EnsurePlainDirectory(root); long total = 0;
        return CertifiedPackages.EnumeratePlainEntries(root).Where(File.Exists).Order(StringComparer.Ordinal).Select(path =>
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/'); PluginApi.ValidatePath(relative);
            using var input = File.OpenRead(path); total = checked(total + input.Length);
            if (total > CertifiedPackages.MaxExpandedBytes) throw new InvalidDataException("data.snapshot-too-large");
            return new PackageFile(relative, input.Length, Convert.ToHexString(SHA256.HashData(input)));
        }).ToArray();
    }
    private static void VerifySnapshot(string transaction, bool hadData)
    {
        string index = Path.Combine(transaction, "snapshot.json");
        if (new FileInfo(index).Length > 4 * 1024 * 1024) throw new InvalidDataException("data.snapshot-invalid");
        var expected = PluginApi.Read<DataSnapshot>(File.ReadAllText(index));
        if (expected.SchemaVersion != 1 || expected.HadData != hadData) throw new InvalidDataException("data.snapshot-invalid");
        var actual = hadData ? SnapshotIndex(Path.Combine(transaction, "data")) : [];
        if (!expected.Files.SequenceEqual(actual)) throw new InvalidDataException("data.snapshot-changed");
    }
    private static void CopyPlainTree(string source, string destination)
    {
        EnsurePlainDirectory(source); var entries = CertifiedPackages.EnumeratePlainEntries(source);
        _ = SnapshotIndex(source); Directory.CreateDirectory(destination); EnsurePlainDirectory(destination);
        foreach (string path in entries)
        {
            string relative = Path.GetRelativePath(source, path).Replace('\\', '/'); PluginApi.ValidatePath(relative);
            string target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path)) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target, overwrite: false); }
        }
    }
    private void DeleteOwnedTree(string path)
    {
        string absolute = Path.GetFullPath(path);
        if (!absolute.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("storage.delete-outside-root");
        EnsurePlainDirectory(absolute); _ = CertifiedPackages.EnumeratePlainEntries(absolute);
        Directory.Delete(absolute, recursive: true);
    }
    private void CleanupVersions(string folder, string current, string previous)
    {
        foreach (string path in Directory.GetDirectories(folder))
            if (IsHash(Path.GetFileName(path)) && Path.GetFileName(path) != current && Path.GetFileName(path) != previous)
                DeleteOwnedTree(path);
    }
    private sealed record UpdateJournal(int SchemaVersion, string TransactionId, InstallReceipt Previous, InstallReceipt Candidate, bool HadData);
    private sealed record DataSnapshot(int SchemaVersion, bool HadData, PackageFile[] Files);
}
