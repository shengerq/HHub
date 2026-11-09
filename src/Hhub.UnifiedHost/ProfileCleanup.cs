using System.Text.Json;
using Hhub.PluginContracts;

namespace Hhub.UnifiedHost;

public sealed record ProfileCleanupPlan(string Root, string ProfileId, string[] Directories, int PresetsToKeep);
public sealed record ProfileCleanupResult(string[] RemovedDirectories, int PresetsKept);

public sealed partial class PluginStore
{
    private sealed record ProfileIdentity(int SchemaVersion, string Product, string ProfileId);
    private static readonly string[] ManagedDirectories = ["plugins", "data", "locks", "staging", "transactions", "downloads", "ui-cache", "logs"];

    public ProfileCleanupPlan PlanCleanup(bool preservePresets)
    {
        using var mutation = AcquireMutationLease();
        var leases = new List<IDisposable>();
        try { return PrepareCleanup(preservePresets, leases).Plan; }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    // This clears only the managed profile, not the host installation or OS registrations.
    // No success result is returned when any deletion fails; retry is safe.
    public async Task<ProfileCleanupResult> CleanupAsync(bool preservePresets)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var leases = new List<IDisposable>();
        try
        {
            using var mutation = AcquireMutationLease();
            var prepared = PrepareCleanup(preservePresets, leases);
            // Save only parsed host preset containers; private binaries/caches do not
            // survive the keep-presets choice. Keep original bytes, not reserialized data.
            foreach (var directory in prepared.Plan.Directories)
            {
                string path = Path.Combine(Root, directory);
                if (directory == "locks") continue; // Lock files are open until all content is removed.
                if (directory == "data" && preservePresets) continue;
                if (Directory.Exists(path)) DeleteOwnedTree(path);
            }
            if (preservePresets)
            {
                string data = Path.Combine(Root, "data");
                if (Directory.Exists(data))
                {
                    var keep = new HashSet<string>(prepared.PresetPaths, StringComparer.OrdinalIgnoreCase);
                    var entries = CertifiedPackages.EnumeratePlainEntries(data);
                    foreach (string path in entries.Where(File.Exists)) if (!keep.Contains(path)) File.Delete(path);
                    foreach (string path in entries.Where(Directory.Exists).OrderByDescending(path => path.Length))
                        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                    if (!Directory.EnumerateFileSystemEntries(data).Any()) Directory.Delete(data);
                }
            }
            foreach (var lease in leases) lease.Dispose(); leases.Clear();
            string locks = Path.Combine(Root, "locks"); if (Directory.Exists(locks)) DeleteOwnedTree(locks);
            Directory.CreateDirectory(Path.Combine(Root, "plugins"));
            RecoveryIssues = [];
            return new(prepared.Plan.Directories, prepared.Plan.PresetsToKeep);
        }
        finally { foreach (var lease in leases) lease.Dispose(); _gate.Release(); }
    }

    private (ProfileCleanupPlan Plan, string[] PresetPaths) PrepareCleanup(bool preservePresets, List<IDisposable> leases)
    {
        EnsurePlainDirectory(Root);
        string identityPath = Path.Combine(Root, "profile.json");
        if (!File.Exists(identityPath)) throw new InvalidDataException("profile.ownership-required");
        if ((File.GetAttributes(identityPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(identityPath).Length > 16384)
            throw new InvalidDataException("profile.identity-invalid");
        var identity = PluginApi.Read<ProfileIdentity>(File.ReadAllText(identityPath));
        if (identity.SchemaVersion != 1 || identity.Product != "hhub.unified" || !Guid.TryParseExact(identity.ProfileId, "N", out _))
            throw new InvalidDataException("profile.identity-invalid");
        // A future host may add storage categories. An older cleaner must refuse,
        // rather than claiming completion while leaving unknown executables behind.
        foreach (string entry in Directory.GetFileSystemEntries(Root))
        {
            string name = Path.GetFileName(entry);
            if (Directory.Exists(entry) ? !ManagedDirectories.Contains(name, StringComparer.Ordinal)
                : name is not ("profile.json" or ".mutation.lock"))
                throw new InvalidDataException("profile.unknown-entry: " + name);
        }
        var directories = ManagedDirectories.Where(name => Directory.Exists(Path.Combine(Root, name))).ToArray();
        foreach (string directory in directories)
        {
            string path = Path.Combine(Root, directory); EnsurePlainDirectory(path);
            _ = CertifiedPackages.EnumeratePlainEntries(path); // Reject every link before deleting anything.
        }
        string plugins = Path.Combine(Root, "plugins");
        var ids = Directory.GetDirectories(plugins).Select(Path.GetFileName).Cast<string>().ToHashSet(StringComparer.Ordinal);
        string dataRoot = Path.Combine(Root, "data");
        if (Directory.Exists(dataRoot))
            foreach (string path in Directory.GetDirectories(dataRoot)) ids.Add(Path.GetFileName(path));
        string lockRoot = Path.Combine(Root, "locks");
        if (Directory.Exists(lockRoot))
            foreach (string path in Directory.GetFiles(lockRoot, "*.lock")) ids.Add(Path.GetFileNameWithoutExtension(path));
        foreach (string id in ids.Order(StringComparer.Ordinal))
        {
            if (!PluginApi.ValidId(id)) throw new InvalidDataException("profile.plugin-id-invalid");
            leases.Add(AcquirePluginLease(id)); // A single busy plugin aborts the whole preflight.
            if (File.Exists(Path.Combine(plugins, id, "pending.json"))) throw new InvalidDataException("plugin.update-recovery-required");
        }
        var presets = new List<string>();
        if (preservePresets)
            foreach (string id in ids)
                foreach (var preset in ReadAllPresets(id)) presets.Add(PresetPath(id, preset.PresetId, preset.TargetId));
        return (new(Root, identity.ProfileId, directories, presets.Count), presets.ToArray());
    }

    // Only call while holding the host instance lease, after CleanupAsync(false).
    // Delete exact metadata and empty directories; never recursively delete Root.
    public void RemoveEmptyProfile()
    {
        string identity = Path.Combine(Root, "profile.json");
        byte[] original;
        using (var mutation = AcquireMutationLease())
        {
            var leases = new List<IDisposable>();
            try { _ = PrepareCleanup(false, leases); }
            finally { foreach (var lease in leases) lease.Dispose(); }
            foreach (string directory in Directory.GetDirectories(Root))
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("profile.not-empty");
                Directory.Delete(directory);
            }
            original = File.ReadAllBytes(identity);
        }
        File.Delete(Path.Combine(Root, ".mutation.lock"));
        try { File.Delete(identity); Directory.Delete(Root, recursive: false); }
        catch
        {
            if (Directory.Exists(Root) && !File.Exists(identity)) File.WriteAllBytes(identity, original);
            throw;
        }
    }
}
