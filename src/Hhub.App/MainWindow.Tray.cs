using Hhub.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private NativeTray? _tray;
    private bool _restoringFromTray;
    private bool _hiddenToTray;
    private bool _hiddenPageDirty;
    private readonly DispatcherTimer _trayPageReleaseTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly BackgroundEfficiencySession _backgroundEfficiency = new();
    private readonly ProcessEfficiency _processEfficiency = new();
    private readonly TrayBatteryCache _trayBatteries = new();
    private readonly HashSet<string> _batteryRefreshing = [];
    private readonly Dictionary<string, DateTimeOffset> _batteryRefreshTimes = [];

    private void InitializeTray()
    {
        if (_tray is not null) return;
        try
        {
            _tray = new NativeTray(WindowNative.GetWindowHandle(this), Path.Combine(AppContext.BaseDirectory, "Assets", "Hhub.ico"),
                action => DispatcherQueue.TryEnqueue(() => action()), RestoreFromTray, ExitFromTray)
            {
                BatteryText = TrayBatteryText,
                Hovered = () => _ = RunUiActionAsync(RefreshTrayBatteriesAsync)
            };
        }
        catch (System.ComponentModel.Win32Exception ex) { ShowError("托盘初始化失败：" + ex.Message); }
    }

    private void UpdateTrayBatteries()
    {
        if (_preferences.TrayBattery)
            foreach (var plugin in _installed.Plugins)
                if (PluginMode(plugin.Manifest.Id) != "disabled" && _runtime.IsFresh(plugin.Manifest) && _runtime.Latest(plugin.Manifest.Id) is { } frame)
                    _trayBatteries.Observe(plugin, _devices, frame);
        _tray?.UpdateBatteryTip();
    }

    private string TrayBatteryText()
    {
        if (!_preferences.TrayBattery) return "Hhub";
        var providers = _installed.Plugins.Where(plugin => plugin.Manifest.BatteryTelemetry is not null && PluginMode(plugin.Manifest.Id) != "disabled").ToArray();
        var enabled = providers.Select(plugin => plugin.Manifest.Id).ToHashSet();
        var devices = _devices.Where(device => providers.Any(plugin => PluginPolicies.MatchesDevice(plugin.Manifest, device)));
        var readings = _trayBatteries.Read(devices, enabled, device => Describe(device).Name, DateTimeOffset.UtcNow);
        if (readings.Count == 0) return "Hhub";
        var duplicateNames = readings.GroupBy(reading => reading.Name).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var numbers = new Dictionary<string, int>();
        return "Hhub · 设备电量\n" + string.Join("\n", readings.OrderByDescending(reading => reading.Battery.HasValue).Select(reading =>
        {
            string name = reading.Name;
            if (duplicateNames.Contains(name)) { numbers[name] = numbers.GetValueOrDefault(name) + 1; name += " · " + numbers[name]; }
            return name + "    " + (reading.Battery is { } battery ? $"{battery:0}%" : "—");
        }));
    }

    private async Task RefreshTrayBatteriesAsync()
    {
        if (!_preferences.TrayBattery || _closing || _localPackageInstallBusy) return;
        UpdateTrayBatteries();
        var requests = _installed.Plugins.Where(plugin => plugin.Manifest.BatteryTelemetry is not null
            && PluginMode(plugin.Manifest.Id) != "disabled" && _devices.Any(device => PluginPolicies.MatchesDevice(plugin.Manifest, device))).ToArray();
        await Task.WhenAll(requests.Select(async plugin =>
        {
            string id = plugin.Manifest.Id;
            if (_batteryRefreshing.Contains(id) || _batteryRefreshTimes.TryGetValue(id, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(Math.Min(30, plugin.Manifest.BatteryTelemetry!.CacheSeconds / 2d))) return;
            _batteryRefreshing.Add(id);
            _batteryRefreshTimes[id] = DateTimeOffset.UtcNow;
            try
            {
                await SendPluginCommandAsync(plugin, plugin.Manifest.BatteryTelemetry!.RefreshCommand);
                _batteryRefreshTimes[id] = DateTimeOffset.UtcNow;
                UpdateTrayBatteries();
            }
            finally { _batteryRefreshing.Remove(id); }
        }));
    }

    private void ApplyTrayEfficiency()
    {
        bool background = _hiddenToTray && _preferences.EfficiencyInTray;
        _backgroundEfficiency.SetEnabled(background);
        try { _processEfficiency.SetEnabled(background); }
        catch (System.ComponentModel.Win32Exception error) { ShowError("后台调度设置未生效：" + error.Message); }
    }

    private void HideToTray()
    {
        if (_closing || _hiddenToTray || _tray?.IsAvailable != true) return;
        _hiddenToTray = true;
        if (_cancelPageFirstFrame is { } cancel)
        {
            cancel();
            _hiddenPageDirty = true;
        }
        AppWindow.Hide();
        _trayPageReleaseTimer.Start();
        UpdateTrayBatteries();
        ApplyTrayEfficiency(); QueueActivation();
    }

    private void ReleaseHiddenPage()
    {
        if (_localPackageInstallBusy) return;
        _trayPageReleaseTimer.Stop();
        if (!_hiddenToTray || _closing) return;
        _captureSceneCreation?.Invoke();
        _captureSceneCreation = null;
        ClearPageViews();
        SetTitleBar(null);
        Content = null;
        SystemBackdrop = null;
        _appliedBackdrop = null;
        _hiddenPageDirty = true;
    }

    private void RestoreFromTray()
    {
        if (_closing || _restoringFromTray) return;
        _trayPageReleaseTimer.Stop();
        _restoringFromTray = true;
        try
        {
            // Restore the presenter before showing: showing a still-minimized window
            // can re-enter the minimize-to-tray handler and clear the restored page.
            _hiddenToTray = false;
            ApplyTrayEfficiency();
            if (Content is null)
            {
                Content = _shellContent;
                SetTitleBar(_titleBarDragRegion);
                ApplyBackdropPreference();
            }
            if (_hiddenPageDirty)
            {
                _hiddenPageDirty = false;
                Render();
            }
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
            AppWindow.Show(); Activate();
            NativeWindowActivation.BringToFront(WindowNative.GetWindowHandle(this));
            QueueActivation();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_closing || _hiddenToTray) return;
                if (ThemeRoot.RequestedTheme != RequestedElementTheme || (_root.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark) != _dark)
                    RequestVisualRefresh(applyThemePreference: true);
                else if (_visualRefreshPending) RequestVisualRefresh();
                FlushRuntimeChanges();
            });
        }
        finally
        {
            // Keep the guard through queued presenter/window-state notifications.
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _restoringFromTray = false)) _restoringFromTray = false;
        }
    }

    private async void ExitFromTray() => await ShutdownAsync();
}
