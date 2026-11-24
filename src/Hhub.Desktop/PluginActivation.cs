using Hhub.Core;
using Hhub.Desktop.Pages;

namespace Hhub.Desktop;

internal static class PluginActivation
{
    internal enum Mode { Default, OnDemand, Disabled, Removed }
    private static readonly AppStorage Storage = new(Path.Combine(PluginPreferences.Root, "plugin-activation"));

    public static Mode Read(string id)
    {
        try
        {
            var mode = Storage.Load(id + ".json", Mode.Default);
            return Enum.IsDefined(mode) ? mode : Mode.Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { System.Diagnostics.Trace.WriteLine(error); return Mode.Default; }
    }
    public static void Save(string id, Mode mode) => Storage.Save(id + ".json", mode);
    private static UserPreferences? _defaults;

    public static void SyncDefaults(UserPreferences preferences) => Volatile.Write(ref _defaults, preferences);

    public static bool Enabled(string id) => Read(id) switch
    {
        Mode.OnDemand => true,
        Mode.Disabled or Mode.Removed => false,
        _ => PluginPolicies.UnifiedMode((Volatile.Read(ref _defaults) ?? PluginPreferences.Load()).DefaultPluginMode) != "disabled"
    };
}
