using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hhub.Core;

public sealed class PluginCatalog(AppStorage storage)
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonFormat.Options) { RespectNullableAnnotations = true };
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const long MaxExtractedBytes = 1024L * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxArchiveEntries = 16_384;

    public static PluginManifest ReadPackageManifest(string package) => ReadPackage(package).Manifest;

    internal static LocalPluginPackage ReadPackage(string package, string? expectedSha256 = null, bool validate = true)
    {
        string path = Path.GetFullPath(package);
        using var file = OpenPackage(path);
        string sha256 = HashPackage(file);
        VerifyPackageHash(expectedSha256, sha256);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        ValidateArchive(archive);
        var manifest = ReadPackageMetadata(archive);
        if (validate)
        {
            Validate(manifest);
            foreach (string license in PluginDisclosures.LicenseFiles(manifest))
                if (!archive.Entries.Any(entry => !IsDirectory(entry) && string.Equals(entry.FullName.Replace('\\', '/'), license.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("插件包缺少声明的许可文件：" + license);
        }
        return new(manifest, path, sha256);
    }

    private static FileStream OpenPackage(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length <= MaxPackageBytes) return file;
        file.Dispose();
        throw new InvalidDataException("插件包超过大小限制");
    }

    private static string HashPackage(FileStream file)
    {
        string hash = Convert.ToHexString(SHA256.HashData(file));
        file.Position = 0;
        return hash;
    }

    private static void VerifyPackageHash(string? expected, string actual)
    {
        if (expected is not null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件包已变化或 SHA-256 不匹配，请重新选择插件包");
    }

    private static bool IsDirectory(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxArchiveEntries) throw new InvalidDataException("插件包文件数量超过限制");
        long remaining = MaxExtractedBytes;
        string root = Path.GetTempPath();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            string path = ResolveInside(root, entry.FullName);
            if (!paths.Add(path)) throw new InvalidDataException("插件包包含重复路径：" + entry.FullName);
            if (entry.Length < 0 || entry.Length > remaining || IsDirectory(entry) && entry.Length != 0)
                throw new InvalidDataException("插件包解压大小超过限制或目录记录无效");
            remaining -= entry.Length;
        }
    }

    private static void CopyEntry(ZipArchiveEntry entry, Stream destination, byte[] buffer)
    {
        using var input = entry.Open();
        long remaining = entry.Length;
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (count > remaining) throw new InvalidDataException("插件包条目的实际大小与声明不符");
            destination.Write(buffer, 0, count);
            remaining -= count;
        }
        if (remaining != 0) throw new InvalidDataException("插件包条目的实际大小与声明不符");
    }

    private static PluginManifest ReadPackageMetadata(ZipArchive archive)
    {
        var entry = archive.GetEntry("plugin.json") ?? throw new InvalidDataException("包中缺少 plugin.json");
        if (entry.Length > MaxManifestBytes) throw new InvalidDataException("插件清单超过大小限制");
        using var content = new MemoryStream();
        CopyEntry(entry, content, new byte[81920]);
        content.Position = 0;
        return DeserializeManifest(content);
    }

    private static PluginManifest DeserializeManifest(Stream content)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(content, ManifestJson)
            ?? throw new InvalidDataException("插件清单为空");
        return manifest;
    }

    public static (LocalPluginPackage[] Packages, string[] Errors) ReadBundledPackages(string directory)
    {
        if (!Directory.Exists(directory)) return ([], []);
        var candidates = new List<LocalPluginPackage>();
        var errors = new List<string>();
        foreach (string path in Directory.GetFiles(directory, "*.hhub"))
        {
            try
            {
                var package = ReadPackage(path, validate: false);
                if (!Version.TryParse(package.Manifest.Version, out _))
                    throw new InvalidDataException("插件标识或版本无效");
                ValidateId(package.Manifest.Id);
                candidates.Add(package);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
            { errors.Add(Path.GetFileName(path) + "：" + error.Message); }
        }
        var packages = new List<LocalPluginPackage>();
        foreach (var group in candidates.GroupBy(package => package.Manifest.Id))
        {
            // Validate the selected version, not superseded historical packages.
            var newest = group.MaxBy(package => Version.Parse(package.Manifest.Version))!;
            try { Validate(newest.Manifest); packages.Add(newest); }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
            { errors.Add(Path.GetFileName(newest.Path) + "：" + error.Message); }
        }
        return (packages.ToArray(), errors.ToArray());
    }

    public CatalogResult Discover()
    {
        List<InstalledPlugin> plugins = [];
        List<string> errors = [];
        foreach (string folder in Directory.EnumerateDirectories(storage.Plugins))
        {
            if (Directory.Exists(Path.Combine(PendingUpdates, Path.GetFileName(folder)))) continue;
            try { plugins.Add(Read(folder)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
            { errors.Add($"{Path.GetFileName(folder)}：{ex.Message}"); }
        }
        return new(plugins.OrderBy(p => p.Manifest.Name).ToArray(), errors);
    }

    public static InstalledPlugin Read(string folder)
    {
        using var content = File.OpenRead(Path.Combine(folder, "plugin.json"));
        if (content.Length > MaxManifestBytes) throw new InvalidDataException("插件清单超过大小限制");
        using var reader = new StreamReader(content);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(reader.ReadToEnd(), ManifestJson)
            ?? throw new InvalidDataException("插件清单为空");
        Validate(manifest);
        foreach (string file in PluginDisclosures.LicenseFiles(manifest))
            if (!File.Exists(ResolveInside(folder, file))) throw new InvalidDataException("找不到声明的许可文件：" + file);
        string entry = ResolveInside(folder, manifest.EntryPoint);
        if (!File.Exists(entry)) throw new InvalidDataException("找不到插件入口");
        foreach (var device in manifest.DevicePresentations)
            if (device.Image is { } image && !File.Exists(ResolveInside(folder, image))) throw new InvalidDataException("找不到插件设备图片：" + image);
        if (manifest.UiAssembly is { } uiAssembly && !File.Exists(ResolveInside(folder, uiAssembly)))
            throw new InvalidDataException("找不到插件界面模块：" + uiAssembly);
        return new(manifest, folder);
    }

    public static void Validate(PluginManifest manifest)
    {
        if (manifest.Arguments.Any(argument => argument is null)
            || manifest.Commands.Any(command => command is null || command.Parameters.Any(parameter => parameter is null))
            || manifest.DeviceMatches.Any(match => match is null) || manifest.DevicePresentations.Any(device => device is null)
            || manifest.Integrations.Any(integration => integration is null))
            throw new InvalidDataException("插件清单数组包含空元素");
        PluginDisclosures.Validate(manifest.Disclosure);
        if (manifest.RequiredCapabilities.Contains("plugin.disclosure.v1") && manifest.Disclosure is null)
            throw new InvalidDataException("插件要求开源与依赖披露能力，但未提供 disclosure 声明");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("不支持此插件协议版本");
        if (manifest.PersistentTaskReason is not null && (string.IsNullOrWhiteSpace(manifest.PersistentTaskReason)
            || manifest.PersistentTaskReason.Length > 240 || !manifest.SupportsBackgroundActivity || manifest.Transport != "stdio-v1"
            || !manifest.RequiredCapabilities.Contains("background-persistent.v1")))
            throw new InvalidDataException("持续任务须声明有效原因、后台任务支持及集成通信方式");
        PluginCompatibility.Validate(manifest);
        if (manifest.DeviceMatches.Any(match => match.AudioRenderEndpointId is { } id &&
            (string.IsNullOrWhiteSpace(id) || id.Length > 256))
            || manifest.DeviceMatches.Any(match => match.AudioRenderEndpointId is not null)
                && !manifest.RequiredCapabilities.Contains("device.audio-endpoint-match.v1"))
            throw new InvalidDataException("音频端点匹配须声明 device.audio-endpoint-match.v1 并提供有效端点标识");
        if (manifest.DeviceMatches.Any(match => match.NameContains is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 128))
            || manifest.DeviceMatches.Any(match => match.NameContains is not null) && !manifest.RequiredCapabilities.Contains("device.name-match.v1"))
            throw new InvalidDataException("设备名称匹配须声明 device.name-match.v1 并提供有效名称");
        if (manifest.Integrations.Length > 0 && !manifest.RequiredCapabilities.Contains("plugin.composition.v1")
            || manifest.Integrations.Select(item => item.Id).Distinct().Count() != manifest.Integrations.Length)
            throw new InvalidDataException("插件协作须声明能力且入口标识不能重复");
        foreach (var integration in manifest.Integrations)
            if (string.IsNullOrWhiteSpace(integration.Id) || !Regex.IsMatch(integration.PluginId, "^[a-z0-9][a-z0-9.-]*$")
                || integration.PluginId == manifest.Id || integration.Commands.Length == 0
                || integration.Commands.Any(string.IsNullOrWhiteSpace)
                || integration.DeviceOutputCommand is { } output && (!integration.Commands.Contains(output) || string.IsNullOrWhiteSpace(integration.DeviceOutputParameter)))
                throw new InvalidDataException("插件协作声明无效");
        foreach (var relay in manifest.Commands.Select(command => command.Relay).OfType<PluginCommandRelay>())
        {
            var integration = PluginComposition.Integration(manifest, relay.Integration);
            if (!integration.Commands.Contains(relay.Command)) throw new InvalidDataException("转交命令不在协作声明范围内");
        }
        foreach (var device in manifest.DevicePresentations)
        {
            if (device.Category is not null && string.IsNullOrWhiteSpace(device.Category))
                throw new InvalidDataException("设备展示声明的类型不能为空");
            if (!Regex.IsMatch(device.Vid, "^[a-fA-F0-9]{4}$") || !Regex.IsMatch(device.Pid, "^[a-fA-F0-9]{4}$") || string.IsNullOrWhiteSpace(device.Name))
                throw new InvalidDataException("设备展示声明的 VID/PID 或名称无效");
            if (device.Image is { } image)
            {
                _ = ResolveInside(Path.GetTempPath(), image);
                if (!DeviceMetadata.SupportedImage(image)) throw new InvalidDataException("设备图片格式不支持");
            }
        }
        ValidateId(manifest.Id);
        _ = ResolveInside(Path.GetTempPath(), manifest.EntryPoint);
        if (string.IsNullOrWhiteSpace(manifest.Name) || !Version.TryParse(manifest.Version, out _))
            throw new InvalidDataException("插件名称或版本格式不正确");
        if (manifest.Transport is not ("standalone" or "stdio-v1")) throw new InvalidDataException("不支持此通信方式");
        if (manifest.SnapshotMaxAgeSeconds is < 1 or > 3600) throw new InvalidDataException("数据有效时间必须为 1–3600 秒");
        if (!double.IsFinite(manifest.ShutdownTimeoutSeconds) || manifest.ShutdownTimeoutSeconds is < 1 or > 30)
            throw new InvalidDataException("停止等待时间必须为 1–30 秒");
        if (manifest.Commands.Select(c => c.Id).Distinct().Count() != manifest.Commands.Length)
            throw new InvalidDataException("插件命令 ID 重复");
        if (manifest.BatteryTelemetry is { } battery && (battery.CacheSeconds is < 10 or > 3600
            || !manifest.Commands.Any(command => command.Id == battery.RefreshCommand) || manifest.Transport != "stdio-v1"))
            throw new InvalidDataException("电量接口须引用已声明的 stdio 命令，缓存有效期为 10–3600 秒");
        if (manifest.DeviceOverview is { } overview)
        {
            var apply = manifest.Commands.FirstOrDefault(command => command.Id == overview.ApplyCommand);
            bool integration = apply?.Relay is not null;
            string?[] integrationKeys = [overview.IntegrationOptionsKey, overview.IntegrationCurrentKey, overview.IntegrationEndpointKey];
            if (!manifest.RequiredCapabilities.Contains("device.overview.v1") || manifest.Category != "device"
                || manifest.Transport != "stdio-v1" || manifest.DeviceMatches.Length == 0
                || overview.RefreshCommand is { } refresh && !manifest.Commands.Any(command =>
                    command.Id == refresh && command.Relay is null && command.Parameters.Length == 0)
                || overview.ApplyCommand is not null && (apply?.Parameters is not [{ Key: "name", Type: "string" }])
                || apply?.Relay is { } relay && !PluginComposition.Integration(manifest, relay.Integration).DeviceCommands.Contains(relay.Command)
                || (integration ? integrationKeys.Any(string.IsNullOrWhiteSpace) : integrationKeys.Any(key => key is not null)))
                throw new InvalidDataException("设备概览须声明匹配规则、能力及有效的刷新和配置命令");
        }
        foreach (var command in manifest.Commands)
        {
            if (command.Parameters.Any(parameter => parameter.Type is not ("number" or "string" or "boolean")))
                throw new InvalidDataException("命令包含不支持的参数类型：" + command.Name);
            if (command.Parameters.Select(parameter => parameter.Key).Distinct().Count() != command.Parameters.Length)
                throw new InvalidDataException("命令参数标识重复：" + command.Name);
        }
        PluginPageData.Validate(manifest);
        if (manifest.UiAssembly is { } uiAssembly)
        {
            _ = ResolveInside(Path.GetTempPath(), uiAssembly);
            if (!uiAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件界面模块必须是 DLL");
        }
        if (manifest.Navigation?.Command is { } navigationCommand
            && (manifest.Transport != "stdio-v1" || !manifest.Commands.Any(command => command.Id == navigationCommand && command.Parameters.Length == 0)))
            throw new InvalidDataException("侧栏入口必须引用已声明的无参数集成命令");
        if (manifest.Navigation is { } navigation && (navigation.Placement is not (null or "after-plugins")
            || (navigation.Placement is not null || navigation.Switch is not null) && !manifest.RequiredCapabilities.Contains("navigation.service-switch.v1")
            || navigation.Switch is { } toggle && (string.IsNullOrWhiteSpace(toggle.StateKey) || manifest.Transport != "stdio-v1"
                || !new[] { toggle.OnCommand, toggle.OffCommand }.All(id => manifest.Commands.Any(command => command.Id == id && command.Parameters.Length == 0)))))
            throw new InvalidDataException("侧栏服务开关声明无效");
        if (manifest.Navigation?.PathData is { } pathData && (string.IsNullOrWhiteSpace(pathData) || pathData.Length > 2048
            || !Regex.IsMatch(pathData, "^[MmLlHhVvZz0-9., +\\-\\r\\n]+$")))
            throw new InvalidDataException("侧栏矢量图标必须使用有效的直线路径");
    }

    private string PendingUpdates => Path.Combine(storage.Root, "plugin-updates");
    private string Trash => Path.Combine(storage.Root, "plugin-trash");

    public InstalledPlugin Install(string package, bool replace = false) =>
        InstallAsync(package, replace, null, null).GetAwaiter().GetResult();

    public InstalledPlugin Install(LocalPluginPackage package, bool replace = false) =>
        InstallAsync(package, replace).GetAwaiter().GetResult();

    public Task<InstalledPlugin> InstallAsync(LocalPluginPackage package, bool replace,
        Func<InstalledPlugin, Task>? verifyUpdate = null) =>
        InstallAsync(package.Path, replace, package.Sha256, verifyUpdate);

    public static void ValidateUpgrade(PluginManifest current, PluginManifest incoming)
    {
        if (Version.Parse(incoming.Version) <= Version.Parse(current.Version))
            throw new InvalidDataException($"已安装 {current.Name} {current.Version}，更新包须高于当前版本。");
    }

    private async Task<InstalledPlugin> InstallAsync(string package, bool replace, string? expectedSha256,
        Func<InstalledPlugin, Task>? verifyUpdate)
    {
        using var file = OpenPackage(package);
        if (expectedSha256 is not null) VerifyPackageHash(expectedSha256, HashPackage(file));
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        ValidateArchive(archive);
        Validate(ReadPackageMetadata(archive));
        string staging = Path.Combine(storage.Root, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            byte[] buffer = new byte[81920];
            foreach (var entry in archive.Entries)
            {
                string target = ResolveInside(staging, entry.FullName);
                if (IsDirectory(entry))
                    Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyEntry(entry, output, buffer);
                }
            }
            var plugin = Read(staging);
            string destination = Path.Combine(storage.Plugins, plugin.Manifest.Id);
            var installed = new InstalledPlugin(plugin.Manifest, destination);
            string previous = Path.Combine(PendingUpdates, plugin.Manifest.Id);
            if (Directory.Exists(previous))
                throw new IOException("此插件还有未恢复的更新，请重启 Hhub 后重试。");
            if (!Directory.Exists(destination))
            {
                Directory.Move(staging, destination);
                return installed;
            }

            if (!replace) throw new InvalidDataException("已安装此插件");
            ValidateUpgrade(Read(destination).Manifest, plugin.Manifest);
            Directory.CreateDirectory(PendingUpdates);
            Directory.Move(destination, previous);
            try
            {
                Directory.Move(staging, destination);
                if (verifyUpdate is not null) await verifyUpdate(installed).ConfigureAwait(false);
                // Moving the old package out of PendingUpdates commits the new version.
                TryDeleteDirectory(MoveToTrash(previous));
            }
            catch (Exception updateError)
            {
                try { RestoreUpdate(previous, destination); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("更新失败，旧版已保存在待恢复目录。请退出 Hhub 后重试启动。",
                        new AggregateException(updateError, rollbackError));
                }
                throw;
            }
            return installed;
        }
        finally { TryDeleteDirectory(staging); }
    }

    // Called once under the host's instance lease, before any plugin is started.
    public string[] RecoverInterruptedUpdates()
    {
        var errors = new List<string>();
        if (Directory.Exists(PendingUpdates))
            foreach (string previous in Directory.GetDirectories(PendingUpdates))
            {
                string id = Path.GetFileName(previous);
                try
                {
                    ValidateId(id);
                    if (Read(previous).Manifest.Id != id) throw new InvalidDataException("旧版插件 ID 与目录不一致");
                    RestoreUpdate(previous, Path.Combine(storage.Plugins, id));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
                {
                    errors.Add(id + " 恢复失败：" + error.Message);
                }
            }
        if (Directory.Exists(Trash))
            foreach (string directory in Directory.GetDirectories(Trash)) TryDeleteDirectory(directory);
        return errors.ToArray();
    }

    private void RestoreUpdate(string previous, string destination)
    {
        string? discarded = Directory.Exists(destination) ? MoveToTrash(destination) : null;
        Directory.Move(previous, destination);
        if (discarded is not null) TryDeleteDirectory(discarded);
    }

    public string[] Uninstall(InstalledPlugin plugin)
    {
        string id = plugin.Manifest.Id;
        ValidateId(id);
        string directory = Path.Combine(storage.Plugins, id);
        if (Directory.Exists(Path.Combine(PendingUpdates, id)))
            throw new IOException("此插件还有未恢复的更新，请重启 Hhub 后重试。");
        var remaining = new List<string>();
        if (Directory.Exists(directory))
        {
            string removed = MoveToTrash(directory);
            if (!TryDeleteDirectory(removed)) remaining.Add(removed);
        }
        string uiCache = Path.Combine(storage.Root, "ui-cache", id);
        if (!TryDeleteDirectory(uiCache)) remaining.Add(uiCache);
        string backups = Path.Combine(storage.Root, "backups");
        try
        {
            if (Directory.Exists(backups))
                foreach (string backup in Directory.EnumerateDirectories(backups, id + "-*"))
                {
                    string name = Path.GetFileName(backup);
                    if (name.StartsWith(id + "-", StringComparison.Ordinal)
                        && Guid.TryParseExact(name[(id.Length + 1)..], "N", out _)
                        && !TryDeleteDirectory(backup)) remaining.Add(backup);
                }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { remaining.Add(backups); }
        return remaining.ToArray();
    }

    private string MoveToTrash(string directory)
    {
        Directory.CreateDirectory(Trash);
        string target = Path.Combine(Trash, Guid.NewGuid().ToString("N"));
        Directory.Move(directory, target);
        return target;
    }

    private static bool TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine(error.Message);
            return false;
        }
    }

    public static string ResolveInside(string root, string relative)
    {
        string path = relative.Replace('\\', '/').TrimEnd('/');
        if (path.Split('/').Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ')
            || part.Any(character => character < ' ' || "<>:\"|?*".Contains(character))
            || Regex.IsMatch(part.Split('.')[0].TrimEnd(' '), @"\A(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("插件路径包含无效的 Windows 文件名");
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(prefix, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("插件路径超出包目录");
        return full;
    }

    private static void ValidateId(string id)
    {
        if (!Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9.-]*\z")) throw new InvalidDataException("插件 ID 格式不正确");
        _ = ResolveInside(Path.GetTempPath(), id);
    }
}
