using Hhub.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private const long WorkingSetBudget = 384L * 1024 * 1024;
    private readonly DispatcherTimer _trayPageReleaseTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _trayMemoryTimer = new() { Interval = TimeSpan.FromMinutes(1) };
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
                BatteryText = TrayBatteryText, IsDark = () => _dark,
                Hovered = () => _ = RunUiActionAsync(RefreshTrayBatteriesAsync)
            };
        }
        catch (System.ComponentModel.Win32Exception ex) { ShowError("托盘初始化失败：" + ex.Message); }
    }

    private void UpdateTrayBatteries()
    {
        if (!_preferences.TrayBattery) return;
        foreach (var plugin in _installed.Plugins)
            if (PluginMode(plugin.Manifest.Id) != "disabled" && _runtime.IsFresh(plugin.Manifest) && _runtime.Latest(plugin.Manifest.Id) is { } frame)
                _trayBatteries.Observe(plugin, _devices, frame);
        _tray?.UpdateBatteryTip();
    }

    private string TrayBatteryText()
    {
        if (!_preferences.TrayBattery) return "Hhub";
        var enabled = _installed.Plugins.Where(plugin => PluginMode(plugin.Manifest.Id) != "disabled").Select(plugin => plugin.Manifest.Id).ToHashSet();
        var readings = _trayBatteries.Read(_devices, enabled, device => Describe(device).Name, DateTimeOffset.UtcNow);
        if (readings.Count == 0) return "Hhub\n未连接外设";
        var duplicateNames = readings.GroupBy(reading => reading.Name).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var numbers = new Dictionary<string, int>();
        return "Hhub · 设备电量\n" + string.Join("\n", readings.Select(reading =>
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
        // Retain the page for quick restores and release its UI after a longer hide.
        AppWindow.Hide();
        _trayPageReleaseTimer.Start();
        _trayMemoryTimer.Start();
        UpdateTrayBatteries();
        ApplyTrayEfficiency(); QueueActivation();
    }

    private void ReleaseHiddenPage(object? sender, object args)
    {
        if (_localPackageInstallBusy) return;
        _trayPageReleaseTimer.Stop();
        if (!_hiddenToTray || _closing) return;
        _captureSceneCreation?.Invoke();
        _captureSceneCreation = null;
        ClearPageViews();
        _hiddenPageDirty = true;
    }

    private void TrimTrayMemory(object? sender, object args)
    {
        if (!_hiddenToTray || _closing || _localPackageInstallBusy) return;
        using var process = Process.GetCurrentProcess();
        if (process.WorkingSet64 <= WorkingSetBudget) return;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        if (!SetProcessWorkingSetSizeEx(process.Handle, nuint.MaxValue, nuint.MaxValue, 0))
            Trace.WriteLine("Hhub working set trim: " + Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(nint process, nuint minimum, nuint maximum, uint flags);

    private void RestoreFromTray()
    {
        if (_closing || _restoringFromTray) return;
        _trayPageReleaseTimer.Stop();
        _trayMemoryTimer.Stop();
        _restoringFromTray = true;
        try
        {
            // Restore the presenter before showing: showing a still-minimized window
            // can re-enter the minimize-to-tray handler and clear the restored page.
            _hiddenToTray = false;
            ApplyTrayEfficiency();
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
