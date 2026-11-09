using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hhub.PluginContracts;

namespace Hhub.UnifiedHost;

public sealed record InstalledPackage(PluginDefinition Definition, string Directory, string PackageHash);
public sealed record PresetDocument(int SchemaVersion, string PluginId, string Name, Dictionary<string, JsonElement> Values,
    string PresetId = "default", string? TargetId = null);

public sealed partial class PluginStore
{
    public string Root { get; }
    private readonly CertificationPolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _managedHostPath;
    public string[] RecoveryIssues { get; private set; } = [];
    public PluginStore(string root, CertificationPolicy policy, string? managedHostPath = null)
    {
        Root = Path.GetFullPath(root); _policy = policy;
        Directory.CreateDirectory(Root); EnsurePlainDirectory(Root);
        if (!Directory.EnumerateFileSystemEntries(Root).Any())
            WriteAtomic(Path.Combine(Root, "profile.json"), new ProfileIdentity(1, "hhub.unified", Guid.NewGuid().ToString("N")));
        _managedHostPath = managedHostPath ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } dotnet ? Path.Combine(dotnet, "dotnet.exe") : "dotnet");
        Directory.CreateDirectory(Path.Combine(Root, "plugins"));
        RecoveryIssues = RecoverInterruptedUpdates();
    }

    public string DataDirectory(string pluginId)
    {
        if (!PluginApi.ValidId(pluginId)) throw new InvalidDataException("plugin.id-invalid");
        return Path.Combine(Root, "data", pluginId);
    }

    public (InstalledPackage[] Plugins, string[] Errors) Discover()
    {
        var plugins = new List<InstalledPackage>(); var errors = new List<string>();
        errors.AddRange(RecoveryIssues);
        foreach (string folder in Directory.GetDirectories(Path.Combine(Root, "plugins")))
        {
            try
            {
                EnsurePlainDirectory(folder);
                if (File.Exists(Path.Combine(folder, "pending.json"))) throw new InvalidDataException("plugin.update-recovery-required");
                var receipt = PluginApi.Read<InstallReceipt>(File.ReadAllText(Path.Combine(folder, "current.json")));
                if (!PluginApi.ValidId(Path.GetFileName(folder)) || !IsHash(receipt.Hash)) throw new InvalidDataException("plugin.receipt-invalid");
                string payload = Path.Combine(folder, receipt.Hash);
                var verified = VerifyPayload(payload, receipt.Hash);
                if (verified.Definition.Id != Path.GetFileName(folder)) throw new InvalidDataException("plugin.receipt-mismatch");
                plugins.Add(verified);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException)
            { errors.Add(Path.GetFileName(folder) + ": " + error.Message); }
        }
        return (plugins.ToArray(), errors.ToArray());
    }

    public Task<InstalledPackage> InstallAsync(string source, Func<InstalledPackage, Task>? verifyUpdate = null)
        => InstallTransactionAsync(source, verifyUpdate);

    public InstalledPackage VerifyInstalled(InstalledPackage plugin)
    {
        string expected = Path.Combine(Root, "plugins", plugin.Definition.Id, plugin.PackageHash);
        if (!string.Equals(Path.GetFullPath(plugin.Directory), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("plugin.directory-invalid");
        return VerifyPayload(expected, plugin.PackageHash);
    }

    private InstalledPackage VerifyPayload(string directory, string packageHash)
    {
        if (_policy.Revoked(packageHash)) throw new InvalidDataException("package.revoked");
        EnsurePlainDirectory(directory);
        var all = CertifiedPackages.EnumeratePlainEntries(directory);
        string signaturePath = Path.Combine(directory, CertifiedPackages.SignatureEntry);
        var signature = new FileInfo(signaturePath);
        if (signature.Length > 1024 * 1024) throw new InvalidDataException("package.signature-too-large");
        var cms = new System.Security.Cryptography.Pkcs.SignedCms(); cms.Decode(File.ReadAllBytes(signaturePath));
        cms.CheckSignature(verifySignatureOnly: true);
        if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].DigestAlgorithm.Value != "2.16.840.1.101.3.4.2.1"
            || cms.SignerInfos[0].Certificate is not { } certificate
            || !_policy.Trusts(certificate.GetCertHashString(HashAlgorithmName.SHA256)))
            throw new InvalidDataException("package.signer-untrusted");
        string signedHash = Convert.ToHexString(SHA256.HashData(cms.ContentInfo.Content));
        if (signedHash != packageHash || _policy.Revoked(signedHash)) throw new InvalidDataException("package.receipt-or-revocation-invalid");
        var inventory = PluginApi.Read<PackageInventory>(System.Text.Encoding.UTF8.GetString(cms.ContentInfo.Content));
        var files = all.Where(File.Exists).ToArray();
        if (inventory.SchemaVersion != 1 || inventory.Files.Length > 4096 || files.Length != inventory.Files.Length + 1
            || inventory.Files.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inventory.Files.Length)
            throw new InvalidDataException("package.installed-inventory-mismatch");
        long total = 0;
        foreach (var item in inventory.Files)
        {
            PluginApi.ValidatePath(item.Path);
            if (item.Path == CertifiedPackages.SignatureEntry) throw new InvalidDataException("package.inventory-invalid");
            string path = Path.Combine(directory, item.Path.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path); total = checked(total + info.Length);
            if (total > CertifiedPackages.MaxExpandedBytes || info.Length != item.Length) throw new InvalidDataException("package.installed-changed");
            using var input = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("package.installed-changed");
        }
        string manifestPath = Path.Combine(directory, "plugin.json");
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new InvalidDataException("package.manifest-too-large");
        var definition = PluginApi.Read<PluginDefinition>(File.ReadAllText(manifestPath)); PluginApi.Validate(definition);
        if (definition.Id != inventory.PluginId || definition.Version != inventory.Version
            || !inventory.Files.Any(file => file.Path == definition.EntryPoint)) throw new InvalidDataException("package.identity-mismatch");
        return new(definition, directory, packageHash);
    }

    // Caller stops the session first. Preserving data never preserves executable payloads.
    public async Task UninstallAsync(string pluginId, bool preserveData)
    {
        if (!PluginApi.ValidId(pluginId)) throw new InvalidDataException("plugin.id-invalid");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var mutation = AcquireMutationLease();
            using (var session = AcquirePluginLease(pluginId))
            {
                if (File.Exists(Path.Combine(Root, "plugins", pluginId, "pending.json")))
                    throw new InvalidDataException("plugin.update-recovery-required");
                string folder = Path.Combine(Root, "plugins", pluginId);
                if (Directory.Exists(folder)) DeleteOwnedTree(folder);
                if (!preserveData && Directory.Exists(DataDirectory(pluginId))) DeleteOwnedTree(DataDirectory(pluginId));
                string transactions = Path.Combine(Root, "transactions", pluginId);
                if (Directory.Exists(transactions)) DeleteOwnedTree(transactions);
            }
            File.Delete(Path.Combine(Root, "locks", pluginId + ".lock"));
        }
        finally { _gate.Release(); }
    }

    public void SavePreset(PresetDocument document)
    {
        using var mutation = AcquireMutationLease();
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > 128
            || !PluginApi.ValidId(document.PresetId) || document.TargetId?.Length > 256)
            throw new InvalidDataException("preset.format-unsupported");
        string directory = DataDirectory(document.PluginId); Directory.CreateDirectory(directory); EnsurePlainDirectory(directory);
        _ = LoadPreset(document.PluginId, document.PresetId, document.TargetId);
        string path = PresetPath(document.PluginId, document.PresetId, document.TargetId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); EnsurePlainDirectory(Path.GetDirectoryName(path)!);
        WriteAtomic(path, document);
    }

    public PresetDocument? LoadPreset(string pluginId, string presetId = "default", string? targetId = null)
    {
        string directory = DataDirectory(pluginId); if (!Directory.Exists(directory)) return null;
        EnsurePlainDirectory(directory); string path = PresetPath(pluginId, presetId, targetId);
        if (!File.Exists(path)) return null;
        return ReadPreset(path, pluginId, presetId, targetId);
    }

    public PresetDocument[] ListPresets(string pluginId, string? targetId = null)
        => ReadAllPresets(pluginId).Where(preset => preset.TargetId == targetId).OrderBy(preset => preset.Name, StringComparer.Ordinal).ToArray();

    private PresetDocument[] ReadAllPresets(string pluginId)
    {
        string root = DataDirectory(pluginId); if (!Directory.Exists(root)) return [];
        EnsurePlainDirectory(root); var result = new List<PresetDocument>();
        var legacy = LoadPreset(pluginId); if (legacy is not null) result.Add(legacy);
        string presets = Path.Combine(root, "presets");
        if (Directory.Exists(presets))
        {
            foreach (string path in CertifiedPackages.EnumeratePlainEntries(presets).Where(File.Exists))
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("preset.too-large");
                var document = PluginApi.Read<PresetDocument>(File.ReadAllText(path));
                if (!string.Equals(PresetPath(pluginId, document.PresetId, document.TargetId), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("preset.path-invalid");
                result.Add(ReadPreset(path, pluginId, document.PresetId, document.TargetId));
            }
        }
        return result.ToArray();
    }

    private static PresetDocument ReadPreset(string path, string pluginId, string presetId, string? targetId)
    {
        EnsurePlainDirectory(Path.GetDirectoryName(path)!);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("preset.too-large");
        var document = PluginApi.Read<PresetDocument>(File.ReadAllText(path));
        if (document.SchemaVersion != 1 || document.PluginId != pluginId || document.PresetId != presetId || document.TargetId != targetId)
            throw new InvalidDataException("preset.migration-required");
        return document;
    }

    private string PresetPath(string pluginId, string presetId, string? targetId)
    {
        if (!PluginApi.ValidId(presetId) || targetId?.Length > 256) throw new InvalidDataException("preset.identity-invalid");
        if (presetId == "default" && targetId is null) return Path.Combine(DataDirectory(pluginId), "preset.json");
        string target = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(targetId ?? "")));
        return Path.Combine(DataDirectory(pluginId), "presets", target, presetId + ".json");
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static void EnsurePlainDirectory(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("storage.link-not-allowed");
    }
    private static void WriteAtomic<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, PluginApi.Json)); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record InstallReceipt(string Version, string Hash);
}
