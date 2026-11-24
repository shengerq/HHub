using Hhub.Core;

namespace Hhub.Desktop.Pages;

internal sealed record DeviceDisplayOptions(bool HomeDetails = true, bool TrayBattery = true);

internal static class PluginPreferences
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hhub-Rebuild");
    private const string FileName = "preferences.json";
    private const string StartupProfileFile = "startup-profile.json";
    private const string OfficialPluginMarket = OfficialPluginLibrary.Source;
    public static readonly int[] StartupDelays = [5, 10, 15];

    public static UserPreferences Load()
    {
        var preferences = new AppStorage(Root).Load(FileName, new UserPreferences());
        int delay = StartupDelay(preferences);
        var normalized = preferences.WatchDevices || preferences.StartupDelaySeconds != delay
            ? preferences with { WatchDevices = false, StartupDelaySeconds = delay } : preferences;
        if (!preferences.OfficialPluginMarketAdded)
        {
            string[] sources = PluginMarkets.Configured(preferences);
            if (sources.Contains(OfficialPluginMarket, StringComparer.OrdinalIgnoreCase))
                normalized = normalized with { OfficialPluginMarketAdded = true };
            else if (sources.Length < 32)
                normalized = normalized with { OfficialPluginMarketAdded = true, PluginMarketUrls = [.. sources, OfficialPluginMarket] };
        }
        if (ReferenceEquals(normalized, preferences)) return preferences;
        try { new AppStorage(Root).Save(FileName, normalized); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        return normalized;
    }

    public static void Save(UserPreferences preferences) => new AppStorage(Root).Save(FileName, preferences);

    private const string DeviceDisplayFile = "device-display.json";

    private static Dictionary<string, DeviceDisplayOptions> ReadDeviceDisplay() =>
        new AppStorage(Root).Load(DeviceDisplayFile, new Dictionary<string, DeviceDisplayOptions>(StringComparer.Ordinal));

    public static Dictionary<string, DeviceDisplayOptions> LoadDeviceDisplay()
    {
        try { return ReadDeviceDisplay(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            System.Diagnostics.Trace.WriteLine(error);
            return new(StringComparer.Ordinal);
        }
    }

    public static void SaveDeviceDisplay(string pluginId, DeviceDisplayOptions options)
    {
        var current = ReadDeviceDisplay();
        current[pluginId] = options;
        new AppStorage(Root).Save(DeviceDisplayFile, current);
    }

    private static StartupRegistration Startup(string arguments) => new(StartupRegistration.RunKey, "Hhub.Rebuild", arguments);

    private static StartupRegistration Startup(UserPreferences preferences, int seconds) => Startup(
        $"--startup --startup-delay {seconds}{(preferences.StartToTray ? " --tray" : "")} --startup-profile");

    public static int StartupDelay(UserPreferences preferences) => preferences.StartupDelaySeconds switch
    {
        0 or 5 => 5,
        10 => 10,
        _ => 15
    };

    private static bool IsLegacyStartupEnabled(string tray, UserPreferences preferences) =>
        Startup(preferences.StartToTray ? "--tray" : "").IsEnabled(tray)
        || Startup(preferences, 0).IsEnabled(tray) || Startup(preferences, 30).IsEnabled(tray)
        || Startup(preferences, 60).IsEnabled(tray);

    public static bool IsStartupEnabled(string tray, UserPreferences preferences) =>
        Startup(preferences, StartupDelay(preferences)).IsEnabled(tray) || IsLegacyStartupEnabled(tray, preferences);

    public static void MigrateStartup(string tray, UserPreferences preferences)
    {
        if (File.Exists(tray) && IsLegacyStartupEnabled(tray, preferences))
            Startup(preferences, StartupDelay(preferences)).SetEnabled(tray, true);
    }

    public static void SetStartup(string tray, UserPreferences preferences, bool enabled)
    {
        if (enabled)
        {
            var arguments = Environment.GetCommandLineArgs()[1..];
            if (arguments is ["--from-tray", _, .. var rest]) arguments = rest;
            if (!HostInstallation.IsInstalled && arguments is not ["--startup-profile"])
                new AppStorage(Root).Save(StartupProfileFile, arguments is ["--plugin-lab", ..] ? arguments : []);
        }
        Startup(preferences, StartupDelay(preferences)).SetEnabled(tray, enabled);
    }

    public static string[] LoadStartupProfile() => new AppStorage(Root).Load(StartupProfileFile, Array.Empty<string>());

    public static bool SaveTrayEfficiency(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\HHub\Desktop");
        int value = enabled ? 1 : 0;
        if (key.GetValue("TrayEfficiency") is int current && value == current) return false;
        key.SetValue("TrayEfficiency", value, Microsoft.Win32.RegistryValueKind.DWord);
        return true;
    }

    public static TimeSpan MonitorInterval()
    {
        try
        {
            int seconds = Load().MonitorRefreshSeconds;
            return TimeSpan.FromSeconds(seconds is 1 or 2 or 5 or 10 ? seconds : 1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidDataException)
        {
            System.Diagnostics.Trace.WriteLine(error);
            return TimeSpan.FromSeconds(1);
        }
    }
}
