using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hhub.Core;
using Hhub.Desktop.Pages;

namespace Hhub.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        _ = SetCurrentProcessExplicitAppUserModelID("HHub.Desktop");
        if (arguments is ["--tray-services", .. var trayArguments])
        {
            InitializeTrayServicesAsync(trayArguments).GetAwaiter().GetResult();
            return;
        }
        if (arguments is not ["--from-tray", _, ..] && StartTray(arguments)) return;
        StartDesktop();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartDesktop()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }

    internal static void SyncPreferences(UserPreferences preferences)
    {
        PluginActivation.SyncDefaults(preferences);
        try
        {
            PluginPreferences.MigrateStartup(Path.Combine(AppContext.BaseDirectory, "Hhub.Tray.exe"), preferences);
            if (PluginPreferences.SaveTrayEfficiency(preferences.EfficiencyInTray)) TrayBattery.RefreshEfficiency();
        }
        catch (Exception error) { Trace.WriteLine(error); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    internal static PluginLab? GetPluginLab(string[] arguments)
    {
        if (arguments is ["--startup-profile"])
        {
            try { arguments = PluginPreferences.LoadStartupProfile(); }
            catch (Exception error) { Trace.WriteLine(error); return null; }
        }
        if (arguments is ["--preview-plugins"]) return null;
        return arguments is ["--plugin-lab", var profile, var package, var fingerprint, ..]
            ? new PluginLab(profile, [package, .. arguments.Skip(4)], fingerprint) : HostInstallation.LoadPlugins();
    }

    private static async Task InitializeTrayServicesAsync(string[] arguments)
    {
        try
        {
            var preferences = PluginPreferences.Load();
            SyncPreferences(preferences);
            TrayBattery.LoadVisibility();
            if (GetPluginLab(arguments) is not { } lab) return;
            lab.SetEfficiencyMode(preferences.EfficiencyInTray);
            try
            {
                if (await FindBatteryProviderAsync(lab) is { } provider)
                    TrayBattery.Register(provider.Entry, provider.Id, provider.Name, provider.Data);
            }
            catch (Exception error) { Trace.WriteLine(error); }
            await TrayDeviceStatus.PublishAsync(lab);
        }
        catch (Exception error) { Trace.WriteLine(error); }
    }

    internal static async Task<(string Entry, string Id, string Name, string? Data)?> FindBatteryProviderAsync(PluginLab lab)
    {
        var (store, plugins) = await lab.DiscoverManifestsAsync().ConfigureAwait(false);
        var candidates = plugins.Where(plugin => PluginActivation.Enabled(plugin.Definition.Id) && plugin.Definition.Permissions.Contains("device.battery.read")
            && plugin.Definition.Provides.Any(service => service.Contract == "tray.battery" && service.Version is "1.0" or "1.1"))
            .Take(2).ToArray();
        if (candidates.Length != 1) return null;
        var (_, registered) = await lab.RegisterAsync(definition => definition.Id == candidates[0].Definition.Id).ConfigureAwait(false);
        var provider = store.VerifyInstalled(registered.Single());
        string entry = Path.Combine(provider.Directory, "tray-battery.exe");
        bool withTarget = provider.Definition.Provides.Any(service => service.Contract == "tray.battery" && service.Version == "1.1");
        if (!File.Exists(entry)) return null;
        string? data = withTarget ? Directory.CreateDirectory(store.DataDirectory(provider.Definition.Id)).FullName : null;
        return (entry, provider.Definition.Id, provider.Definition.Name, data);
    }

    private static bool StartTray(string[] arguments)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Hhub.Tray.exe");
        if (!File.Exists(executable)) return false;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var tray = Process.Start(start);
            return tray is not null;
        }
        catch (Win32Exception error)
        {
            Trace.WriteLine(error);
            return false;
        }
    }
}
