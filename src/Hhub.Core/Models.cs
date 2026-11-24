using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hhub.Core;

public static class JsonFormat
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
}

public sealed record PluginCommand(string Id, string Name, string Resource)
{
    public PluginCommandRelay? Relay { get; init; }
    public CommandParameter[] Parameters { get; init; } = [];
    public string? ConflictResource { get; init; }
    public bool SceneConfigurable { get; init; }
}
public sealed record CommandParameter(string Key, string Name, string Type, double? Minimum = null,
    double? Maximum = null, JsonElement? DefaultValue = null);
public sealed record DeviceMatch(string? Vid = null, string? Pid = null, string? Category = null, string? Connection = null)
{
    public string? NameContains { get; init; }
    public string? AudioRenderEndpointId { get; init; }
}
public sealed record PluginCommandRelay(string Integration, string Command);
public sealed record PluginIntegration(string Id, string PluginId, string[] Commands)
{
    public string[] DeviceCommands { get; init; } = [];
    public string? DeviceOutputCommand { get; init; }
    public string DeviceOutputParameter { get; init; } = "id";
}
public sealed record PluginConfiguration(string DeviceId, string PluginId, string Command, string Name, Dictionary<string, JsonElement> Values);
public sealed record DevicePresentation(string Vid, string Pid, string Name, string? Image = null)
{
    public string? Category { get; init; }
}
public sealed record PageElement(string Type, string Label, string? Key = null, string? Command = null)
{
    public PageElement[] Children { get; init; } = [];
    public Dictionary<string, JsonElement> Values { get; init; } = [];
    public string Parameter { get; init; } = "value";
    public string? OptionsKey { get; init; }
    public PageOption[] Options { get; init; } = [];
    public string OptionLabelKey { get; init; } = "label";
    public string OptionValueKey { get; init; } = "value";
    public string? EnabledKey { get; init; }
    public string? DisabledKey { get; init; }
    public string? VisibleKey { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 100;
    public double Step { get; init; } = 1;
    public string Unit { get; init; } = "";
    public double Width { get; init; }
    public bool Fill { get; init; }
    public double Height { get; init; }
    public string? OffCommand { get; init; }
    public bool ShowStateText { get; init; } = true;
    public string ItemValueKey { get; init; } = "value";
    public string ItemLabelKey { get; init; } = "label";
    public string ItemIdKey { get; init; } = "id";
    public string IndexParameter { get; init; } = "index";
    public string? SecondaryKey { get; init; }
    public string? SecondaryCommand { get; init; }
    public string SecondaryParameter { get; init; } = "value";
}
public sealed record PageOption(string Label, JsonElement Value);
public sealed record PluginPage(string Title, PageElement[] Elements)
{
    public string Layout { get; init; } = "tiles";
    public bool ShowManagementActions { get; init; } = true;
    public bool ShowSettingsShortcut { get; init; } = true;
    public string HeaderPlacement { get; init; } = "title";
    public bool FullContent { get; init; }
    public PageElement[] HeaderElements { get; init; } = [];
}
public sealed record PluginNavigation(string? Title = null, string? Glyph = null, string? Command = null)
{
    public string? Placement { get; init; }
    public PluginNavigationSwitch? Switch { get; init; }
    public string? PathData { get; init; }
}
public sealed record PluginNavigationSwitch(string StateKey, string OnCommand, string OffCommand);
public sealed record BatteryTelemetry(string RefreshCommand, int CacheSeconds = 120);
public sealed record DeviceOverview(string? Name = null, string? Category = null, string? Connection = null,
    string? RefreshCommand = null, string? ApplyCommand = null)
{
    public string? IntegrationOptionsKey { get; init; }
    public string? IntegrationCurrentKey { get; init; }
    public string? IntegrationEndpointKey { get; init; }
}
public sealed record PluginComponent(string Name, string Version, string ProjectUrl, string License, string LicenseFile, string Attribution);
public sealed record PluginRuntimeRequirement(string Name, string Version, string Architecture, string Provision, string Description);
public sealed record PluginDisclosure
{
    public int SchemaVersion { get; init; } = 1;
    public required string License { get; init; }
    public required string LicenseFile { get; init; }
    public PluginRuntimeRequirement[] Runtimes { get; init; } = [];
    public PluginComponent[] OpenSourceProjects { get; init; } = [];
    public PluginComponent[] Dependencies { get; init; } = [];
}

public sealed record PluginManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string MinimumHostVersion { get; init; } = "0.5.0";
    public string[] RequiredCapabilities { get; init; } = [];
    public int UiApiVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "utility";
    public required string EntryPoint { get; init; }
    public string Transport { get; init; } = "standalone";
    public string[] ActivationPages { get; init; } = [];
    public bool SupportsCommandResults { get; init; }
    public bool SupportsBackgroundActivity { get; init; }
    public string? PersistentTaskReason { get; init; }
    public PluginDisclosure? Disclosure { get; init; }
    public BatteryTelemetry? BatteryTelemetry { get; init; }
    public DeviceOverview? DeviceOverview { get; init; }
    public bool LatencySensitive { get; init; }
    public double ShutdownTimeoutSeconds { get; init; } = 2.5;
    public int SnapshotMaxAgeSeconds { get; init; } = 10;
    public string[] Arguments { get; init; } = [];
    public string[] Permissions { get; init; } = [];
    public PluginCommand[] Commands { get; init; } = [];
    public DeviceMatch[] DeviceMatches { get; init; } = [];
    public string[] DeviceNames { get; init; } = [];
    public DevicePresentation[] DevicePresentations { get; init; } = [];
    public PluginPage? Page { get; init; }
    public string? UiAssembly { get; init; }
    public PluginPage? SettingsPage { get; init; }
    public PluginNavigation? Navigation { get; init; }
    public PluginIntegration[] Integrations { get; init; } = [];
}

public sealed record InstalledPlugin(PluginManifest Manifest, string Directory);
public sealed record LocalPluginPackage(PluginManifest Manifest, string Path, string Sha256);
public sealed record PluginUserState(string[] Disabled)
{
    public Dictionary<string, string> Modes { get; init; } = [];
    public string[] Removed { get; init; } = [];
}
public sealed record SnapshotFrame(DateTimeOffset ReceivedAtUtc, DateTimeOffset? CapturedAtUtc,
    IReadOnlyDictionary<string, JsonElement> Values);
public sealed record CatalogResult(IReadOnlyList<InstalledPlugin> Plugins, IReadOnlyList<string> Errors);
public sealed record DeviceRecord(string InstanceId, string Name, string Category, string Connection, string? Vid, string? Pid, string InterfacePath,
    string? Location = null, string? ContainerId = null)
{
    public string[] MemberInstanceIds { get; init; } = [];
    public string[] InterfacePaths { get; init; } = [];
    public string[] Categories { get; init; } = [];
    public string? LocationPath { get; init; }
    public string? ClassificationNote { get; init; }
    public string? ReportedName { get; init; }
    public bool IsInternal { get; init; }
    public int? AudioFormFactor { get; init; }
    public bool Has35mmJack { get; init; }
    public string[] AudioRenderEndpointIds { get; init; } = [];
    public bool ContainsInstance(string? id) => string.Equals(InstanceId, id, StringComparison.OrdinalIgnoreCase)
        || MemberInstanceIds.Contains(id, StringComparer.OrdinalIgnoreCase);
}

public sealed record SceneRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public required string Application { get; init; }
    public string Condition { get; init; } = "foreground";
    public int Priority { get; init; } = 10;
    public string PluginId { get; init; } = "";
    public string Command { get; init; } = "";
    public string Resource { get; init; } = "";
    public string? SceneId { get; init; }
    public Dictionary<string, JsonElement> Values { get; init; } = [];
}

public sealed record ActivitySnapshot(string? ForegroundApplication, IReadOnlySet<string> RunningApplications);
public sealed record UserPreferences(string Theme = "system", bool SidebarCollapsed = false,
    string? PrimaryDeviceId = null, string CurrentSceneId = "daily", string? MonitorPluginId = null)
{
    public bool AutomaticScenes { get; init; } = true;
    public int ScenePresetVersion { get; init; }
    public bool WatchDevices { get; init; } = true;
    public int MonitorRefreshSeconds { get; init; } = 1;
    public bool TranslucentBackground { get; init; } = true;
    public int SceneIntervalSeconds { get; init; } = 2;
    public string DefaultPluginMode { get; init; } = "onDemand";
    public string PluginMarketUrl { get; init; } = "";
    public string[]? PluginMarketUrls { get; init; }
    public bool OfficialPluginMarketAdded { get; init; }
    public string UpdateChannel { get; init; } = "stable";
    public bool AutomaticUpdates { get; init; }
    public string? SelectedDiskId { get; init; }
    public bool StartToTray { get; init; } = true;
    public int StartupDelaySeconds { get; init; } = 15;
    public bool MinimizeToTray { get; init; } = true;
    public bool CloseToTray { get; init; } = true;
    public bool EfficiencyInTray { get; init; } = true;
    public bool TrayBattery { get; init; } = true;
    public bool SwitchSceneOnDeviceSelect { get; init; } = true;
}
// Glyph remains readable for older scene files; current scene presentation uses names only.
public sealed record SceneProfile(string Id, string Name, string Glyph = "")
{
    public SceneAction[] Actions { get; init; } = [];
}
public sealed record SceneAction(string PluginId, string Command, string Resource, Dictionary<string, JsonElement> Values);
public sealed record DeviceSceneBinding(string DeviceId, string SceneId, string PluginId, string Command,
    Dictionary<string, JsonElement> Values);

public sealed class AppStorage
{
    public string Root { get; }
    public string Plugins { get; }
    public static string DefaultRoot => Environment.GetEnvironmentVariable("HHUB_PROFILE_ROOT") is { Length: > 0 } profile
        ? Path.GetFullPath(profile)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hhub");

    public AppStorage(string? root = null)
    {
        Root = root ?? DefaultRoot;
        Plugins = Path.Combine(Root, "plugins");
        Directory.CreateDirectory(Plugins);
    }

    public T Load<T>(string name, T fallback) => File.Exists(Path.Combine(Root, name))
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Root, name)), JsonFormat.Options) ?? throw new InvalidDataException("配置内容为空")
        : fallback;

    public void Save<T>(string name, T value)
    {
        string path = Path.Combine(Root, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, JsonFormat.Options));
        File.Move(path + ".tmp", path, true);
    }
}
