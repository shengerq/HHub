using Hhub.Core;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private readonly HashSet<string> _pageStartedPlugins = [];
    private Dictionary<string, string> _pluginModes = [];
    private readonly Dictionary<string, int> _commandDemands = [];
    private HashSet<string> _disabledPlugins = [];
    private HashSet<string> _removedPlugins = [];
    private LocalPluginPackage[] _bundledPackages = [];
    private bool _activationQueued;
    private readonly HashSet<string> _reconcilingPlugins = [];
    private readonly HashSet<string> _installingPlugins = [];
    private bool _monitorSetupPending = true;
    private readonly object _pluginStateWriteGate = new();
    private long _pluginStateVersion;
    private long _savedPluginStateVersion;

    private void LoadPluginState()
    {
        var state = _storage.Load("plugin-state.json", new PluginUserState([]));
        _disabledPlugins = state.Disabled.ToHashSet();
        _removedPlugins = state.Removed.ToHashSet();
        _pluginModes = state.Modes;
        string directory = Path.Combine(AppContext.BaseDirectory, "bundled-plugins");
        var bundled = PluginCatalog.ReadBundledPackages(directory);
        _bundledPackages = bundled.Packages;
        if (bundled.Errors.Length > 0) ShowError(string.Join("\n", bundled.Errors));
    }

    private PluginUserState CapturePluginState() => new(_disabledPlugins.ToArray())
    {
        Modes = new(_pluginModes), Removed = _removedPlugins.ToArray()
    };

    private void WritePluginState(PluginUserState state, long version)
    {
        lock (_pluginStateWriteGate)
        {
            if (version < _savedPluginStateVersion) return;
            _storage.Save("plugin-state.json", state);
            _savedPluginStateVersion = version;
        }
    }

    private void SavePluginState() => WritePluginState(CapturePluginState(), Interlocked.Increment(ref _pluginStateVersion));

    private void QueueActivation()
    {
        if (_closing || _activationQueued || _localPackageInstallBusy) return;
        _activationQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _activationQueued = false;
            foreach (var plugin in _installed.Plugins) _ = ReconcilePluginAsync(plugin);
        })) _activationQueued = false;
    }

    private async Task ReconcilePluginAsync(InstalledPlugin plugin)
    {
        string id = plugin.Manifest.Id;
        if (_closing || _localPackageInstallBusy || _installingPlugins.Contains(id)
            || _monitorSetupPending && plugin.Manifest.Category == "monitor" || !_reconcilingPlugins.Add(id)) return;
        bool transitioning = false;
        try
        {
            bool demand = PluginDemand(plugin);
            string mode = PluginMode(id);
            if (!_runtime.HasSession(id) && PluginPolicies.ShouldAutoStart(mode, demand, _runtime.State(id)))
            {
                transitioning = true;
                _pageStartedPlugins.Add(id);
                await _runtime.EnsureStartedAsync(plugin);
            }
            else if (!PluginPolicies.ShouldRun(mode, demand) && (_pageStartedPlugins.Remove(id) || _runtime.IsRunning(id)))
            {
                transitioning = true;
                await _runtime.StopAsync(id);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException
            or BadImageFormatException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            transitioning = false;
            ShowError(plugin.Manifest.Name + "：" + ex.Message);
        }
        finally
        {
            _reconcilingPlugins.Remove(id);
            if (transitioning) QueueActivation();
        }
    }

    private string PluginMode(string id) => PluginPolicies.UnifiedMode(_disabledPlugins.Contains(id) ? "disabled"
        : _pluginModes.GetValueOrDefault(id, "onDemand"));

    private bool PluginDemand(InstalledPlugin plugin)
    {
        string id = plugin.Manifest.Id;
        if (plugin.Manifest.Category == "monitor" && id == MonitorPlugin?.Manifest.Id && DateTimeOffset.UtcNow < _remoteMonitorUntil) return true;
        if (!_hiddenToTray && _embeddedPluginIds.Contains(id)) return true;
        if (!_hiddenToTray && _page == "home" && _primaryDevice is not null && plugin.Manifest.BatteryTelemetry is not null
            && _devices.Any(device => PluginPolicies.MatchesDevice(plugin.Manifest, device))) return true;
        if (!_hiddenToTray && _page == "home" && _primaryDevice is { } primary
            && OverviewPlugin(primary) is { } overview && OverviewCommand(overview)?.Relay is { } relay
            && IntegrationTarget(overview, relay.Integration)?.Manifest.Id == id) return true;
        var device = _hiddenToTray ? null : _openDevice
            ?? (_page == "home" && plugin.Manifest.DeviceOverview is not null ? _primaryDevice : null);
        return PluginPolicies.HasDemand(plugin.Manifest, _hiddenToTray ? "" : _page, _hiddenToTray ? null : _openPlugin?.Manifest.Id, MonitorPlugin?.Manifest.Id,
            device, _commandDemands.GetValueOrDefault(id) > 0, _runtime.HasBackgroundActivity(plugin.Manifest));
    }

    private async Task SendPluginCommandAsync(InstalledPlugin plugin, string command, IReadOnlyDictionary<string, JsonElement>? values = null)
    {
        if (_closing) throw new InvalidOperationException("Hhub 正在退出");
        string id = plugin.Manifest.Id;
        if (_installingPlugins.Contains(id)) throw new InvalidOperationException("插件正在安装或卸载");
        if (PluginMode(id) == "disabled") throw new InvalidOperationException("插件已停用，请先在设置 → 插件中启用");
        if (plugin.Manifest.Commands.FirstOrDefault(item => item.Id == command)?.Relay is { } relay)
        {
            if (values is null || !values.TryGetValue("deviceInstanceId", out var instance))
                throw new InvalidOperationException("设备配置缺少目标实例");
            var device = _devices.FirstOrDefault(device => device.ContainsInstance(instance.GetString()));
            if (device is null) throw new InvalidOperationException("设备已断开");
            await SendIntegrationAsync(plugin, relay.Integration, relay.Command, values, device);
            return;
        }
        _commandDemands[id] = _commandDemands.GetValueOrDefault(id) + 1;
        _pageStartedPlugins.Add(id);
        try { await _runtime.SendAsync(plugin, command, values); }
        finally
        {
            if (_commandDemands[id] == 1) _commandDemands.Remove(id); else _commandDemands[id]--;
            QueueActivation();
        }
    }

    private async Task SetPluginModeAsync(InstalledPlugin plugin, string mode, bool restart = false)
    {
        if (_closing) throw new InvalidOperationException("Hhub 正在退出");
        mode = PluginPolicies.UnifiedMode(mode);
        string id = plugin.Manifest.Id;
        if (!_installingPlugins.Add(id)) throw new InvalidOperationException("插件正在切换状态，请稍候。");
        try
        {
            if (restart) await _runtime.StopAsync(id);
            _pluginModes[id] = mode;
            if (mode == "disabled") _disabledPlugins.Add(id); else _disabledPlugins.Remove(id);
            var state = CapturePluginState();
            long version = Interlocked.Increment(ref _pluginStateVersion);
            await Task.Run(() => WritePluginState(state, version));
            ResetSceneActionsForPlugin(id);
            if (mode == "disabled" || !PluginPolicies.ShouldRun(mode, PluginDemand(plugin)))
            {
                _pageStartedPlugins.Remove(id);
                await _runtime.StopAsync(id);
            }
            else
            {
                _pageStartedPlugins.Add(id);
                await _runtime.EnsureStartedAsync(plugin, retry: true);
            }
            RefreshNavigation();
        }
        finally { _installingPlugins.Remove(id); QueueActivation(); }
    }

    private StackPanel BundledPluginRows()
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var package in _bundledPackages.Where(package =>
            (_pluginTargetDevice is null || PluginPolicies.MatchesDevice(package.Manifest, _pluginTargetDevice)) &&
            $"{package.Manifest.Name} {package.Manifest.Description} {string.Join(' ', package.Manifest.DeviceNames)} {string.Join(' ', package.Manifest.DeviceMatches.Select(match => match.Vid + " " + match.Pid))}"
                .Contains(_pluginSearch, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new Grid { ColumnSpacing = 16, MinHeight = 48 };
            row.ColumnDefinitions.Add(new() { Width = new(32) });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(Icon("\uE9D9", 24));
            var name = Text(package.Manifest.Name, 14);
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var installed = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == package.Manifest.Id);
            bool update = installed is not null && Version.Parse(package.Manifest.Version) > Version.Parse(installed.Manifest.Version);
            var install = AsyncButton(update ? "更新" : installed is not null ? "已安装" : "安装",
                () => InstallPluginBatchAsync(() => Task.FromResult(new PluginBatchPlan([package], [], [])), "Hhub 随附包"), true);
            install.IsEnabled = installed is null || update;
            Grid.SetColumn(install, 2); row.Children.Add(install);
            ToolTipService.SetToolTip(row, package.Manifest.Description + "\n随附本地插件包");
            list.Children.Add(Card(row, padding: new(20, 12, 20, 12)));
        }
        if (list.Children.Count == 0) list.Children.Add(Text("没有匹配的插件", BodyTextSize));
        return list;
    }

    private async Task<InstalledPlugin> InstallPackageAsync(LocalPluginPackage package)
    {
        if (_closing) throw new InvalidOperationException("Hhub 正在退出");
        string id = package.Manifest.Id;
        if (!_installingPlugins.Add(id)) throw new InvalidOperationException("此插件正在安装");
        try
        {
            var existing = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == id);
            bool wasRunning = false;
            bool hadSnapshot = false;
            if (existing is not null)
            {
                PluginCatalog.ValidateUpgrade(existing.Manifest, package.Manifest);
                await WaitForPluginUiStagingAsync(id);
                wasRunning = _runtime.IsRunning(id);
                hadSnapshot = _runtime.Latest(id) is not null;
                await _runtime.StopAsync(id);
                _pageStartedPlugins.Remove(id);
            }
            InstalledPlugin installed;
            try
            {
                installed = await Task.Run(() => _catalog.InstallAsync(package, replace: existing is not null,
                    wasRunning ? candidate => VerifyPluginUpdateAsync(candidate, hadSnapshot) : null));
            }
            catch
            {
                ReloadPlugins(id);
                if (wasRunning && _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == id) is { } restored
                    && restored.Manifest.Version == existing!.Manifest.Version)
                {
                    try
                    {
                        await _runtime.EnsureStartedAsync(restored, retry: true);
                        _pageStartedPlugins.Add(id);
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    { ShowError("旧版插件已恢复，重新启动失败：" + error.Message); }
                }
                throw;
            }
            if (wasRunning) _pageStartedPlugins.Add(id);
            if (_removedPlugins.Remove(id))
            {
                _pluginModes.Remove(id);
                _disabledPlugins.Remove(id);
                try { SavePluginState(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { ShowError("插件已安装，但移除记录无法清除：" + error.Message); }
            }
            if (_openPlugin?.Manifest.Id == id) _openPlugin = installed;
            ReloadPlugins(id);
            _sceneActionAttempts.Clear();
            return installed;
        }
        finally { _installingPlugins.Remove(id); QueueActivation(); }
    }

    private async Task VerifyPluginUpdateAsync(InstalledPlugin candidate, bool requireSnapshot)
    {
        string id = candidate.Manifest.Id;
        try
        {
            await _runtime.EnsureStartedAsync(candidate, retry: true).ConfigureAwait(false);
            var wait = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan limit = TimeSpan.FromSeconds(requireSnapshot ? 10 : 1);
            do
            {
                if (!_runtime.IsRunning(id) || _runtime.State(id) == PluginRunState.Faulted)
                    throw new IOException(_runtime.Error(id) ?? "新版插件启动后退出。");
                if (requireSnapshot && _runtime.Latest(id) is not null) return;
                await Task.Delay(100).ConfigureAwait(false);
            } while (wait.Elapsed < limit);
            if (requireSnapshot && _runtime.Latest(id) is null)
                throw new IOException("新版插件未在启动验证期间返回状态。");
            if (!_runtime.IsRunning(id) || _runtime.State(id) == PluginRunState.Faulted)
                throw new IOException(_runtime.Error(id) ?? "新版插件启动后退出。");
        }
        catch
        {
            await _runtime.StopAsync(id).ConfigureAwait(false);
            throw;
        }
    }
}
