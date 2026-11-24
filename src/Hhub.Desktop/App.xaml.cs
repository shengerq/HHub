using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace Hhub.Desktop;

public sealed partial class App : Application
{
    private DesktopWindow? _window;
    internal Window? MainWindow => _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] argsList = Environment.GetCommandLineArgs()[1..];
        int? restoredPage = null;
        bool fromTray = false;
        if (argsList is ["--from-tray", var page, .. var desktopArgs])
        {
            fromTray = true;
            argsList = desktopArgs;
            if (int.TryParse(page, out int position) && position is >= 0 and <= 3)
                restoredPage = position;
        }

        bool previewPlugins = argsList.Contains("--preview-plugins", StringComparer.Ordinal);
        var lab = Program.GetPluginLab(argsList);
        var window = new DesktopWindow(previewPlugins, lab, restoredPage);
        _window = window;
        if (fromTray && lab is not null) _ = RegisterBatteryProviderAsync(lab, window);
        _window.Closed += async (_, _) =>
        {
            _window = null;
            try
            {
                if (lab is not null)
                {
                    try { await lab.WaitForPageSessionsAsync(); }
                    finally { await lab.StopRetainedSessionsAsync(); }
                }
            }
            catch (Exception error) { Trace.WriteLine(error); }
            finally
            {
                if (fromTray && window.ExitWithWindow) DesktopWindow.RequestTrayExit();
                Exit();
            }
        };
        _window.Activate();
    }

    private async Task RegisterBatteryProviderAsync(PluginLab lab, DesktopWindow window)
    {
        try
        {
            TrayBattery.LoadVisibility();
            if (await Program.FindBatteryProviderAsync(lab) is { } provider && ReferenceEquals(window, _window))
                TrayBattery.Register(provider.Entry, provider.Id, provider.Name, provider.Data);
        }
        catch (Exception error) { Trace.WriteLine(error); }
    }
}
