using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private MarketPlugin[] _remoteMarket = [];
    private bool _marketLoaded;
    private string[] _marketErrors = [];
    private sealed record PluginUpdateOffer(string Version, LocalPluginPackage? Local, MarketPlugin? Remote);

    private void OpenPluginManagementSettings()
    {
        _settingsSection = "插件"; Navigate("settings");
    }

    private async Task RefreshRemoteMarketAsync()
    {
        string[] addresses = PluginMarkets.Configured(_preferences);
        var result = await PluginMarkets.ReadAsync(addresses);
        if (!addresses.SequenceEqual(PluginMarkets.Configured(_preferences))) throw new InvalidOperationException("市场地址已更改，请重新检查。");
        _remoteMarket = result.Plugins; _marketErrors = result.Errors; _marketLoaded = true;
    }

    private StackPanel RemoteMarketRows()
    {
        var panel = new StackPanel { Spacing = 8 };
        var refresh = AsyncButton("刷新市场", async () => { await RefreshRemoteMarketAsync(); PopulatePluginRows(); }, true);
        refresh.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(refresh);
        if (!_marketLoaded) panel.Children.Add(Text("点击刷新读取插件市场", 14));
        else if (_remoteMarket.Length == 0 && _marketErrors.Length == 0) panel.Children.Add(Text("市场暂无插件", 14));
        foreach (string error in _marketErrors)
        {
            var notice = Text(error, 12); notice.Foreground = Muted; notice.TextWrapping = TextWrapping.Wrap;
            notice.TextTrimming = TextTrimming.None; panel.Children.Add(notice);
        }
        foreach (var entry in _remoteMarket.Where(entry => (entry.Name + " " + entry.Id + " " + entry.Description).Contains(_pluginSearch, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new Grid { ColumnSpacing = 16, MinHeight = 48 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var identity = new StackPanel { Spacing = 4 };
            identity.Children.Add(Text(entry.Name, 14));
            var details = Text(entry.Version + (entry.Description.Length > 0 ? " · " + entry.Description : ""), 12); details.Foreground = Muted;
            identity.Children.Add(details); row.Children.Add(identity);
            if (Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var source))
            {
                var origin = Text(source.Host, 12); origin.Foreground = Muted; identity.Children.Add(origin);
                ToolTipService.SetToolTip(origin, entry.SourceUrl);
            }
            var installed = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == entry.Id);
            bool current = installed is not null && Version.Parse(installed.Manifest.Version) >= Version.Parse(entry.Version);
            var install = AsyncButton(current ? "已安装" : installed is null ? "安装" : "更新", () => InstallRemotePluginAsync(entry), true);
            install.IsEnabled = !current; Grid.SetColumn(install, 1); row.Children.Add(install); panel.Children.Add(Card(row, padding: new(16)));
        }
        return panel;
    }

    private async Task InstallRemotePluginAsync(MarketPlugin entry)
    {
        string directory = Path.Combine(_storage.Root, "downloads"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".hhub");
        try
        {
            var package = await PluginMarket.DownloadPackageAsync(entry, path);
            string source = Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var index)
                ? index.GetLeftPart(UriPartial.Authority) : "插件市场";
            await InstallPluginBatchAsync(() => Task.FromResult(new PluginBatchPlan([package], [], [])), source);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private Button PluginUpdateButton(InstalledPlugin plugin)
    {
        var button = Button("检查更新", () => { }, true);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                if (button.Tag is PluginUpdateOffer offer)
                {
                    if (offer.Remote is { } remote) await InstallRemotePluginAsync(remote);
                    else if (offer.Local is { } local)
                        await InstallPluginBatchAsync(() => Task.FromResult(new PluginBatchPlan([local], [], [])), "Hhub 随附包");
                    return;
                }
                button.Content = "检查中…";
                var localPackage = _bundledPackages.Where(package => package.Manifest.Id == plugin.Manifest.Id)
                    .MaxBy(package => Version.Parse(package.Manifest.Version));
                MarketPlugin? online = null;
                bool onlineSource = PluginMarkets.Configured(_preferences).Length > 0;
                if (onlineSource)
                {
                    await RefreshRemoteMarketAsync(); online = _remoteMarket.FirstOrDefault(entry => entry.Id == plugin.Manifest.Id);
                }
                if (onlineSource && _marketErrors.Length > 0 && (online is null || Version.Parse(online.Version) <= Version.Parse(plugin.Manifest.Version))
                    && (localPackage is null || Version.Parse(localPackage.Manifest.Version) <= Version.Parse(plugin.Manifest.Version)))
                    throw new InvalidOperationException("部分市场检查失败，无法确认更新状态。" + string.Join("；", _marketErrors));
                if (localPackage is null && online is null) throw new InvalidOperationException(onlineSource ? "已配置的市场未提供该插件的更新。" : "请先在设置 → 插件中添加市场网址。");
                string latest = localPackage?.Manifest.Version ?? online!.Version;
                bool useOnline = online is not null && (localPackage is null || Version.Parse(online.Version) > Version.Parse(latest));
                if (useOnline) latest = online!.Version;
                if (Version.Parse(latest) > Version.Parse(plugin.Manifest.Version))
                {
                    button.Tag = new PluginUpdateOffer(latest, useOnline ? null : localPackage, useOnline ? online : null);
                    button.Content = "更新至 " + latest;
                }
                else button.Content = online is not null ? "未发现更新" : "随附包无更新";
                ToolTipService.SetToolTip(button, null);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Net.Http.HttpRequestException
                or OperationCanceledException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException)
            {
                button.Content = "检查失败"; ToolTipService.SetToolTip(button, error.Message); ShowError(error.Message);
            }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private async Task UninstallPluginAsync(InstalledPlugin plugin)
    {
        var confirmation = Dialog("卸载插件？", plugin.Manifest.Name + " 的程序文件和缓存将移除。插件设置和场景引用将保留。", "卸载");
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        if (_closing) throw new InvalidOperationException("Hhub 正在退出");
        string id = plugin.Manifest.Id;
        if (!_installingPlugins.Add(id)) throw new InvalidOperationException("此插件正在更新，请稍候。");
        try
        {
            await WaitForPluginUiStagingAsync(id);
            await _runtime.StopAsync(id);
            _pageStartedPlugins.Remove(id);
            if (_openPlugin?.Manifest.Id == id || _embeddedPluginIds.Contains(id))
            {
                _openPlugin = null; _openDevice = null; _pluginSettingsOpen = false;
                _page = "plugins"; _pluginTab = "manage"; Render();
            }
            bool addedRemoval = _removedPlugins.Add(id);
            string[] remaining;
            try
            {
                SavePluginState();
                remaining = await Task.Run(() => _catalog.Uninstall(plugin));
            }
            catch
            {
                if (addedRemoval)
                {
                    _removedPlugins.Remove(id);
                    try { SavePluginState(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { ShowError("插件卸载失败，移除记录也无法还原：" + error.Message); }
                }
                throw;
            }
            _pluginModes.Remove(id);
            _disabledPlugins.Remove(id);
            ResetSceneActionsForPlugin(id);
            _diagnosticJournal.Forget(id);
            _batteryRefreshTimes.Remove(id);
            ReloadPlugins(id); _page = "plugins"; _pluginTab = "manage"; Render();
            try { SavePluginState(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { ShowError("插件已卸载，但停用记录无法清理：" + error.Message); }
            if (remaining.Length > 0)
                ShowError("插件已卸载，部分文件仍待清理：\n" + string.Join("\n", remaining));
        }
        finally { _installingPlugins.Remove(id); QueueActivation(); }
    }
}
