using System.Text.Json;
using Hhub.Core;
using Hhub.PluginContracts;
using DeviceOverview = Hhub.PluginContracts.DeviceOverview;
using Hhub.UnifiedHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Hhub.Desktop.Pages;

internal sealed class PluginsPage : StackPanel
{
    private readonly ScrollViewer _viewport;
    private readonly PluginPreview? _preview;
    private readonly PluginLab? _lab;
    private readonly ConnectedPeripheral? _device;
    private readonly string _deviceParent;
    private readonly Action? _returnHome;
    private readonly Action<string>? _openMonitor;
    private readonly string? _openPluginId;
    private readonly StackPanel _header = new() { Spacing = 6 };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _body = new() { Spacing = PageLayout.SettingsRowGap };
    private readonly ToggleButton _manage = PageLayout.SecondaryTab("管理");
    private readonly ToggleButton _library = PageLayout.SecondaryTab("库");
    private CancellationTokenSource? _load;
    private Task _livePage = Task.CompletedTask;
    private bool _closed;
    private bool _nativeLayout;
    private StackPanel? _titleControls;
    private Border? _deviceConfigurationSlot;
    private TextBlock? _liveTitle;
    private readonly PeripheralInventory? _inventory;

    public PluginsPage(PluginPreview? preview, ScrollViewer viewport, PluginLab? lab = null,
        ConnectedPeripheral? device = null, Action? returnHome = null, Action<string>? openMonitor = null,
        string? openPluginId = null, PeripheralInventory? inventory = null, string deviceParent = "首页")
    {
        _preview = preview;
        _viewport = viewport;
        _lab = lab;
        _device = device;
        _deviceParent = deviceParent;
        _returnHome = returnHome;
        _openMonitor = openMonitor;
        _openPluginId = openPluginId;
        _inventory = inventory;
        Margin = PageLayout.Inset;
        HorizontalAlignment = HorizontalAlignment.Center;
        Spacing = 12;

        AutomationProperties.SetName(_tabs, "插件页面");
        _manage.IsChecked = true;
        _manage.Click += (_, _) => _ = SelectTabAsync(library: false);
        _library.Click += (_, _) => _ = SelectTabAsync(library: true);
        _tabs.Children.Add(_manage);
        _tabs.Children.Add(_library);
        if (_device is null) ShowMainHeader();
        else ShowLiveHeader(_device.Name, _returnHome!, _deviceParent, devicePlugin: true);
        Children.Add(_header);
        Children.Add(_body);
        Loaded += LoadInitialTab;
    }

    internal Task RefreshManagedPluginsAsync() =>
        !_closed && _device is null && _body.Children.Count > 0 && _header.Children.Contains(_tabs)
            && _manage.IsChecked == true ? ShowTabAsync(library: false, _viewport.VerticalOffset) : Task.CompletedTask;

    public void ClosePage()
    {
        _closed = true;
        _load?.Cancel();
    }


    public void ResizeToViewport(double width)
    {
        if (width > 0)
        {
            double inset = PageLayout.WideInset(width);
            Margin = new Thickness(inset, _nativeLayout ? 8 : 0, inset, PageLayout.Inset.Bottom);
            Width = Math.Max(0, width - 2 * inset);
        }
    }

    private Task SelectTabAsync(bool library)
    {
        bool changed = (library ? _manage : _library).IsChecked == true;
        _manage.IsChecked = !library;
        _library.IsChecked = library;
        return changed ? ShowTabAsync(library) : Task.CompletedTask;
    }

    private async void LoadInitialTab(object sender, RoutedEventArgs args)
    {
        Loaded -= LoadInitialTab;
        if (_openPluginId is { } id && _lab is not null && _device is null)
        {
            await OpenPluginByIdAsync(id);
            return;
        }
        await ShowTabAsync(library: false);
    }

    private async Task OpenPluginByIdAsync(string id)
    {
        _body.Children.Add(Detail("正在启动插件…"));
        try
        {
            var (store, plugins) = await _lab!.RegisterAsync(definition => definition.Id == id);
            if (_closed) return;
            var plugin = plugins.FirstOrDefault(item => item.Definition.Id == id);
            if (plugin is null)
            {
                await ShowTabAsync(library: false);
                return;
            }
            ShowLivePlugin(store, plugin);
        }
        catch (Exception error) when (!_closed)
        {
            ShowStatus($"无法打开插件。{error.Message}");
        }
    }

    private async Task ShowTabAsync(bool library, double restoreOffset = 0)
    {
        if (_closed) return;
        _load?.Cancel();
        using var load = new CancellationTokenSource();
        _load = load;
        _body.Children.Clear();
        ScrollTo(0);

        try
        {
            if (!library)
            {
                if (_lab is not null)
                    await LoadLabPluginAsync(_lab, load.Token);
                else
                    await LoadManagementAsync(restoreOffset, load.Token);
            }
            else if (_preview is { } preview)
                ShowLibrary(preview.Plugins.Select(plugin => new LibraryEntry(
                    plugin.Manifest.Name, plugin.Manifest.Version, "", plugin)).ToArray());
            else
                await LoadLibraryAsync(load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_load, load)) _load = null;
        }
    }

    private async Task LoadLabPluginAsync(PluginLab lab, CancellationToken token)
    {
        _body.Children.Add(Detail("正在读取测试插件…"));
        try
        {
            if (_device is { } device)
            {
                var (store, providers) = await lab.RegisterAsync(definition => definition.Kind == "device"
                    && (device.DeviceProvider is null || definition.Id == device.DeviceProvider)).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                _body.Children.Clear();
                if (device.DeviceProvider is not null && providers is [var providerForDevice])
                {
                    ShowLivePlugin(store, providerForDevice);
                    return;
                }
                var matches = new List<InstalledPackage>();
                foreach (var provider in providers)
                {
                    await lab.RunPageAsync(provider, async () =>
                    {
                        await using var session = await lab.StartSessionAsync(store, provider, token);
                        if (session.Targets.Any(target => target.Online && !target.Simulated && target.Id == device.Id))
                            matches.Add(provider);
                    }, token);
                }
                token.ThrowIfCancellationRequested();
                if (matches.Count == 1) ShowLivePlugin(store, matches[0]);
                else if (matches.Count == 0) ShowStatus("暂无适配此设备的插件");
                else foreach (var plugin in matches)
                {
                    var open = PageLayout.AccentButton("打开");
                    open.Click += (_, _) => ShowLivePlugin(store, plugin);
                    _body.Children.Add(PluginRow(LibraryInfoForLocal(plugin), open));
                }
                return;
            }
            var (_, plugins) = await lab.DiscoverManifestsAsync().WaitAsync(token);
            token.ThrowIfCancellationRequested();
            _body.Children.Clear();
            if (lab.AcceptsImports) _body.Children.Add(PluginImportRow(lab));
            if (plugins.Length == 0) _body.Children.Add(Detail("暂无已安装插件。"));
            foreach (var plugin in plugins)
                _body.Children.Add(ManagedPluginRow(lab, plugin));
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not OperationCanceledException)
        {
            ShowStatus($"无法读取插件。{error.Message}");
        }
    }

    private Border PluginImportRow(PluginLab lab)
    {
        var install = PageLayout.AccentButton("安装");
        var feedback = Detail("");
        feedback.Visibility = Visibility.Collapsed;
        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(Detail("从文件安装插件 (.hhub)"));
        info.Children.Add(feedback);
        install.Click += async (_, _) =>
        {
            if ((Application.Current as App)?.MainWindow is not { } window) return;
            install.IsEnabled = false;
            feedback.Visibility = Visibility.Collapsed;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".hhub");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                if (await picker.PickSingleFileAsync() is not { Path.Length: > 0 } file || _closed) return;
                await lab.InstallPackageAsync(file.Path);
                if (!_closed && _manage.IsChecked == true) await ShowTabAsync(library: false, _viewport.VerticalOffset);
            }
            catch (Exception error) when (IsStorageError(error) || error is System.Security.Cryptography.CryptographicException
                or System.Runtime.InteropServices.COMException)
            {
                feedback.Text = error.Message == "package.signer-untrusted"
                    ? "插件签名的发布者不受信任。" : $"无法安装插件。{error.Message}";
                feedback.Visibility = Visibility.Visible;
            }
            finally { install.IsEnabled = true; }
        };
        return PluginRow(info, install);
    }

    private Border ManagedPluginRow(PluginLab lab, InstalledPackage plugin)
    {
        string id = plugin.Definition.Id;
        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(LibraryInfoForLocal(plugin));
        var status = Detail("");
        status.Visibility = Visibility.Collapsed;
        var report = Detail("");
        report.Visibility = Visibility.Collapsed;
        info.Children.Add(status);
        info.Children.Add(report);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var open = PageLayout.AccentButton("打开");
        var uninstall = new Button { Content = "卸载" };
        var mode = new ComboBox { ItemsSource = new[] { "使用默认", "按需启动", "禁用" } };
        AutomationProperties.SetName(mode, $"{plugin.Definition.Name} 启动方式");
        actions.Children.Add(mode);
        actions.Children.Add(open);
        actions.Children.Add(uninstall);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        bool applying = false;
        string? errorMessage = null;
        void Refresh()
        {
            if (applying || _closed) return;
            var activation = PluginActivation.Read(id);
            bool removed = activation == PluginActivation.Mode.Removed;
            bool enabled = PluginActivation.Enabled(id);
            applying = true;
            try
            {
                int selection = removed ? -1 : (int)activation;
                if (mode.SelectedIndex != selection) mode.SelectedIndex = selection;
                mode.IsEnabled = !removed;
                open.IsEnabled = (enabled && !PluginProtection.IsBlocked(plugin)) || removed;
                uninstall.IsEnabled = !removed;
                string action = removed ? "重新安装" : "打开";
                if (!Equals(open.Content, action)) open.Content = action;
                string current = errorMessage ?? (PluginProtection.IsBlocked(plugin) ? "防病毒已阻止" : "");
                status.Visibility = current.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                if (status.Text != current) status.Text = current;
                var latest = PluginProtection.Latest(id);
                string message = latest is null ? "" : $"{latest.Reason} · {latest.Verdict}";
                if (report.Text != message) report.Text = message;
                report.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            finally { applying = false; }
        }

        async Task ChangeAsync(PluginActivation.Mode selected)
        {
            applying = true;
            mode.IsEnabled = open.IsEnabled = uninstall.IsEnabled = false;
            errorMessage = null;
            try { await lab.SetActivationAsync(id, selected); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { errorMessage = $"无法更改插件状态。{error.Message}"; }
            finally { applying = false; Refresh(); }
            if (selected == PluginActivation.Mode.Removed && errorMessage is null && !lab.CanReinstall && !_closed
                && _manage.IsChecked == true)
                await ShowTabAsync(library: false, _viewport.VerticalOffset);
        }

        mode.SelectionChanged += async (_, _) =>
        {
            if (!applying && mode.SelectedIndex >= 0)
                await ChangeAsync((PluginActivation.Mode)mode.SelectedIndex);
        };
        open.Click += async (_, _) =>
        {
            if (PluginActivation.Read(id) == PluginActivation.Mode.Removed)
            {
                await ChangeAsync(PluginActivation.Mode.OnDemand);
                if (PluginActivation.Read(id) != PluginActivation.Mode.OnDemand) return;
            }
            if (_closed) return;
            _body.Children.Clear();
            await OpenPluginByIdAsync(id);
        };
        uninstall.Click += async (_, _) => await ChangeAsync(PluginActivation.Mode.Removed);
        var row = PluginRow(info, actions);
        timer.Tick += (_, _) => Refresh();
        row.Loaded += (_, _) => { Refresh(); timer.Start(); };
        row.Unloaded += (_, _) => timer.Stop();
        Refresh();
        return row;
    }

    private static TextBlock LibraryInfoForLocal(InstalledPackage plugin) =>
        new() { Text = $"{plugin.Definition.Name}  ·  {plugin.Definition.Version}", TextWrapping = TextWrapping.Wrap };

    private void ShowLivePlugin(PluginStore store, InstalledPackage plugin)
    {
        if (_openMonitor is { } openMonitor && plugin.Definition.Provides.Any(service =>
            service.Contract == HomeMonitor.Service && Version.Parse(service.Version).Major == HomeMonitor.MajorVersion))
        {
            openMonitor(plugin.Definition.Id);
            return;
        }
        ShowLiveHeader(_device?.Name ?? plugin.Definition.Name, Back, _device is null ? "管理" : _deviceParent,
            devicePlugin: plugin.Definition.Kind == "device");
        _body.Children.Clear();
        ScrollTo(0);
        _livePage = PollLivePluginAsync(store, plugin);

        async void Back()
        {
            if (_returnHome is { } back)
            {
                back();
                return;
            }
            _load?.Cancel();
            await _livePage;
            if (_closed) return;
            ShowMainHeader();
            await ShowTabAsync(library: false);
        }
    }

    private void ShowMainHeader()
    {
        _nativeLayout = false;
        ResizeToViewport(_viewport.ActualWidth);
        _header.Children.Clear();
        _header.Children.Add(PageLayout.Heading("插件"));
        _header.Children.Add(_tabs);
    }

    private void ShowLiveHeader(string name, Action onBack, string parent, bool devicePlugin)
    {
        _header.Children.Clear();
        var header = new Grid { ColumnSpacing = 10, MinHeight = PageLayout.HeadingRowHeight };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = new Button
        {
            Content = PageLayout.BreadcrumbText(parent),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Padding = new Thickness(0),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        back.Click += (_, _) => onBack();
        header.Children.Add(back);
        var separator = new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(separator, 1);
        header.Children.Add(separator);
        _liveTitle = PageLayout.HeadingText(name);
        _liveTitle.TextWrapping = TextWrapping.NoWrap;
        _liveTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_liveTitle, 2);
        header.Children.Add(_liveTitle);
        _titleControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _deviceConfigurationSlot = devicePlugin ? new Border { VerticalAlignment = VerticalAlignment.Center } : null;
        if (_deviceConfigurationSlot is not null) _titleControls.Children.Add(_deviceConfigurationSlot);
        Grid.SetColumn(_titleControls, 3);
        header.Children.Add(_titleControls);
        _header.Children.Add(header);
    }

    private async Task PollLivePluginAsync(PluginStore store, InstalledPackage plugin)
    {
        using var load = new CancellationTokenSource();
        _load = load;
        var notice = Detail("正在启动插件…");
        _body.Children.Add(notice);
        bool devicePlugin = plugin.Definition.Kind == "device";
        var trayCapabilities = TrayDeviceStatus.Capabilities(plugin.Definition);
        bool publishTray = devicePlugin && (trayCapabilities.Contains(DeviceOverview.BatteryPercent)
            || trayCapabilities.Contains(DeviceOverview.TrayVariables));
        PluginSession? activeSession = null;
        string? activeTarget = null;
        bool writing = false;
        DevicePluginSettings? settings = null;
        NativePluginPage? native = null;
        IReadOnlyDictionary<string, JsonElement> snapshot = new Dictionary<string, JsonElement>();
        bool fresh = false;
        var actionError = Detail("");
        actionError.Visibility = Visibility.Collapsed;
        var configurationSlot = _deviceConfigurationSlot;
        DeviceConfigurationMenu? configurationMenu = null;
        if (SceneDeviceConfigurations.CanConfigure(plugin.Definition))
        {
            configurationMenu = new DeviceConfigurationMenu(store, plugin,
                configuration => RunConfigurationAsync((session, target, values) =>
                    SceneDeviceConfigurations.ApplyAsync(store, plugin, session, target, configuration, values)),
                name => RunConfigurationAsync(async (session, target, values) =>
                {
                    await SceneDeviceConfigurations.CaptureAsync(store, plugin, session, target, name, values, load.Token);
                    return SceneDeviceConfigurations.RequireValues(await session.ReadAsync(target));
                }));
            if (configurationSlot is not null) configurationSlot.Child = configurationMenu;
        }
        var targets = new ComboBox { DisplayMemberPath = nameof(PluginTarget.Name), Visibility = Visibility.Collapsed };
        if (devicePlugin && _device is null)
        {
            AutomationProperties.SetName(targets, "选择设备");
            targets.SelectionChanged += (_, _) =>
            {
                if ((targets.SelectedItem as PluginTarget)?.Id == activeTarget) return;
                activeTarget = null;
                Display(null, new Dictionary<string, JsonElement>(), false);
            };
            _body.Children.Add(targets);
        }
        try
        {
            await _lab!.RunPageAsync(plugin, async () =>
            {
                native = NativePluginPage.Open(store, plugin, this, () => snapshot, () => fresh,
                    () => !load.IsCancellationRequested, WriteSetting, RunNativeAction);
                if (native is not null)
                {
                    _nativeLayout = true;
                    ResizeToViewport(_viewport.ActualWidth);
                    if (_liveTitle is not null && _header.Children.FirstOrDefault() is Grid header)
                    {
                        header.MinHeight = 40;
                        _liveTitle.FontSize = 24;
                        _liveTitle.LineHeight = 32;
                        if (_openPluginId is not null)
                        {
                            header.ColumnSpacing = 0;
                            header.Children[0].Visibility = Visibility.Collapsed;
                            header.Children[1].Visibility = Visibility.Collapsed;
                        }
                    }
                    foreach (var control in native.View.HeaderControls) _titleControls?.Children.Add(control);
                }
                else
                    settings = DevicePluginSettings.Open(plugin.Directory, store.DataDirectory(plugin.Definition.Id),
                        plugin.Definition, WriteSetting, _titleControls, devicePlugin ? BuildDeviceDisplayOptions(plugin.Definition) : null);
                if ((native?.Page.Title ?? settings?.PageTitle) is { } title && _liveTitle is not null) _liveTitle.Text = title;
                var session = await _lab.OpenInteractiveSessionAsync(store, plugin, (native?.KeepActive ?? settings?.KeepActive) == true, load.Token,
                    devicePlugin ? TimeSpan.FromSeconds(30) : null);
                activeSession = session;
                try
                {
                    await PollAsync(session);
                }
                finally { await _lab.ReleaseInteractiveSessionAsync(plugin, session); }
            }, load.Token);

            async Task PollAsync(PluginSession session)
            {
                var readouts = new Dictionary<string, TextBlock>();
                if (native is not null)
                {
                    _body.Children.Add(actionError);
                    _body.Children.Add(native.View.Root);
                }
                else if (settings is not null) _body.Children.Add(settings);
                else foreach (var capability in plugin.Definition.Capabilities)
                {
                    if (capability.Id is HomeMonitor.AuthorizationRequest or HomeMonitor.DriverDownloadUrl) continue;
                    var value = Detail("—");
                    readouts.Add(capability.Id, value);
                    var content = new StackPanel { Spacing = 4 };
                    content.Children.Add(new TextBlock { Text = capability.Name });
                    content.Children.Add(value);
                    _body.Children.Add(Card(content));
                }
                if (devicePlugin && settings is null && BuildDeviceDisplayOptions(plugin.Definition) is { } displayOptions)
                    _body.Children.Add(displayOptions);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(devicePlugin ? 3 : 1));
                bool firstRead = true;
                do
                {
                    load.Token.ThrowIfCancellationRequested();
                    if (writing) continue;
                    string? targetId = null;
                    if (devicePlugin)
                    {
                        if (!firstRead) await session.DiscoverAsync(load.Token);
                        firstRead = false;
                        load.Token.ThrowIfCancellationRequested();
                        if (writing) continue;
                        var available = session.Targets.Where(target => target.Online && !target.Simulated).ToArray();
                        if (publishTray && (available.Length == 0 || (activeTarget is { } previousTarget
                            && !available.Any(target => target.Id == previousTarget))))
                        {
                            if (available.Length == 0) TrayBattery.PublishStatus(plugin.Definition.Id, []);
                            else if (activeTarget is not null) TrayBattery.UpdateStatus(plugin.Definition.Id, activeTarget);
                        }
                        if (_device is { } chosen)
                            targetId = available.FirstOrDefault(target => target.Id == chosen.Id)?.Id;
                        else
                        {
                            string? previous = (targets.SelectedItem as PluginTarget)?.Id;
                            if (targets.ItemsSource is not PluginTarget[] old || !old.SequenceEqual(available))
                                targets.ItemsSource = available;
                            targets.Visibility = available.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
                            int index = Array.FindIndex(available, target => target.Id == previous);
                            targets.SelectedIndex = index >= 0 ? index : available.Length == 1 ? 0 : -1;
                            targetId = (targets.SelectedItem as PluginTarget)?.Id;
                        }
                        if (targetId is null)
                        {
                            activeTarget = null;
                            Display(null, new Dictionary<string, JsonElement>(), false);
                            notice.Text = available.Length > 1 && _device is null ? "请选择设备" : "设备未连接";
                            notice.Visibility = Visibility.Visible;
                            foreach (var value in readouts.Values) value.Text = "—";
                            continue;
                        }
                    }
                    var response = await session.ReadAsync(targetId, load.Token);
                    load.Token.ThrowIfCancellationRequested();
                    if (writing) continue;
                    if (devicePlugin && _device is null && (targets.SelectedItem as PluginTarget)?.Id != targetId)
                        continue;
                    activeTarget = response.TargetId ?? targetId;
                    if (publishTray && activeTarget is not null && session.Targets.FirstOrDefault(target => target.Id == activeTarget) is { } target)
                        TrayBattery.UpdateStatus(plugin.Definition.Id, TrayDeviceStatus.Row(activeTarget, target.Name,
                            plugin.Definition, trayCapabilities, response.Values!));
                    notice.Visibility = Visibility.Collapsed;
                    Display(activeTarget, response.Values!, true);
                    UpdateTitle(response.Values!);
                    foreach (var (id, value) in response.Values!)
                        if (readouts.TryGetValue(id, out var text))
                            text.Text = FormatReadout(value);
                }
                while (await timer.WaitForNextTickAsync(load.Token));
            }
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!load.IsCancellationRequested)
            {
                notice.Text = $"插件读取失败。{error.Message}";
                notice.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            activeSession = null;
            Display(null, new Dictionary<string, JsonElement>(), false);
            if (configurationSlot is not null) configurationSlot.Child = null;
            if (native is not null)
            {
                foreach (var control in native.View.HeaderControls) _titleControls?.Children.Remove(control);
                _body.Children.Remove(native.View.Root);
                _body.Children.Remove(actionError);
                native.Dispose();
            }
            if (ReferenceEquals(_load, load)) _load = null;
        }

        async Task WriteSetting(string id, JsonElement value)
        {
            if (writing) throw new InvalidOperationException("设备正在保存设置。");
            var session = activeSession ?? throw new InvalidOperationException("设备未连接");
            string target = activeTarget ?? throw new InvalidOperationException("设备未连接");
            writing = true;
            try
            {
                var reply = await session.SetAsync(id, value, target);
                if (settings?.ControlsArtwork(id) == true)
                    _inventory?.RefreshArtwork(plugin.Definition.Id, plugin.Directory, store.DataDirectory(plugin.Definition.Id));
                if (!load.IsCancellationRequested && activeTarget == target)
                {
                    Display(target, reply.Values!, true);
                    UpdateTitle(reply.Values!);
                }
            }
            catch
            {
                if (!load.IsCancellationRequested && session.IsRunning && activeTarget == target)
                {
                    try
                    {
                        var fresh = await session.ReadAsync(target);
                        if (!load.IsCancellationRequested && activeTarget == target) Display(target, fresh.Values!, true);
                    }
                    catch (Exception error)
                    {
                        System.Diagnostics.Trace.WriteLine(error);
                        Display(null, new Dictionary<string, JsonElement>(), false);
                    }
                }
                throw;
            }
            finally { writing = false; }
        }

        void Display(string? target, IReadOnlyDictionary<string, JsonElement> values, bool current)
        {
            snapshot = values;
            fresh = current;
            settings?.Display(target, values);
            native?.View.Refresh();
            configurationMenu?.Display(target, values);
        }

        async Task RunConfigurationAsync(
            Func<PluginSession, string, IReadOnlyDictionary<string, JsonElement>, Task<IReadOnlyDictionary<string, JsonElement>>> action)
        {
            if (writing || activeSession is not { } session || activeTarget is not { } target)
                throw new InvalidOperationException("设备暂时不可用。");
            writing = true;
            try
            {
                var baseline = SceneDeviceConfigurations.RequireValues(await session.ReadAsync(target));
                load.Token.ThrowIfCancellationRequested();
                if (activeTarget != target) throw new InvalidOperationException("所选设备已改变。");
                var confirmed = await action(session, target, baseline);
                load.Token.ThrowIfCancellationRequested();
                if (activeTarget == target)
                {
                    Display(target, confirmed, true);
                    UpdateTitle(confirmed);
                }
            }
            finally { writing = false; }
        }

        async Task RunNativeAction(Func<Task> action)
        {
            actionError.Visibility = Visibility.Collapsed;
            try { await action(); }
            catch (Exception error) when (!load.IsCancellationRequested)
            {
                actionError.Text = error.Message;
                actionError.Visibility = Visibility.Visible;
            }
            catch (Exception) when (load.IsCancellationRequested) { }
        }

        void UpdateTitle(IReadOnlyDictionary<string, JsonElement> values)
        {
            if (devicePlugin && _liveTitle is not null
                && values.GetValueOrDefault("device.name") is { ValueKind: JsonValueKind.String } name
                && name.GetString() is { Length: > 0 } text && _liveTitle.Text != text)
                _liveTitle.Text = text;
        }
    }

    private static StackPanel? BuildDeviceDisplayOptions(PluginDefinition plugin)
    {
        bool home = DeviceOverview.IsProvidedBy(plugin);
        bool battery = plugin.Permissions.Contains("device.battery.read") && plugin.Provides.Any(service =>
            service.Contract == "tray.battery" || service.Contract == DeviceOverview.Service
                && service.CapabilityIds.Contains(DeviceOverview.BatteryPercent));
        if (!home && !battery) return null;

        var options = PluginPreferences.LoadDeviceDisplay().GetValueOrDefault(plugin.Id) ?? new();
        var section = new StackPanel { Spacing = PageLayout.SettingsRowGap };
        if (home) AddToggle("首页显示详细参数", options.HomeDetails, enabled => options with { HomeDetails = enabled });
        if (battery) AddToggle("托盘显示电量", options.TrayBattery, enabled => options with { TrayBattery = enabled });
        return section;

        void AddToggle(string title, bool initial, Func<bool, DeviceDisplayOptions> update)
        {
            var toggle = new ToggleSwitch { IsOn = initial, OnContent = "", OffContent = "", MinWidth = 0 };
            var errorText = Detail("");
            errorText.Visibility = Visibility.Collapsed;
            bool restoring = false;
            toggle.Toggled += (_, _) =>
            {
                if (restoring) return;
                var next = update(toggle.IsOn);
                try { PluginPreferences.SaveDeviceDisplay(plugin.Id, next); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    restoring = true;
                    toggle.IsOn = !toggle.IsOn;
                    restoring = false;
                    errorText.Text = "无法保存显示选项";
                    errorText.Visibility = Visibility.Visible;
                    System.Diagnostics.Trace.WriteLine(error);
                    return;
                }
                if (options.TrayBattery != next.TrayBattery) TrayBattery.SetPluginVisibility(plugin.Id, next.TrayBattery);
                options = next;
                errorText.Visibility = Visibility.Collapsed;
            };
            section.Children.Add(SettingsPage.Row(title, toggle, errorText));
        }
    }


    private static string FormatReadout(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "—";
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() == 0) return "—";
            if (value.EnumerateArray().All(item => item.ValueKind is JsonValueKind.Number or JsonValueKind.String))
                return string.Join("　", value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString() : item.GetDouble().ToString("0.##")));
        }
        return value.GetRawText();
    }

    private async Task LoadManagementAsync(double restoreOffset, CancellationToken cancellationToken)
    {
        _body.Children.Add(Detail("正在读取已安装插件…"));
        try
        {
            var catalog = _preview is { } preview
                ? new CatalogResult(preview.Plugins.Where(preview.IsInstalled).ToArray(), [])
                : await Task.Run(() => ReadInstalled(cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _body.Children.Clear();
            if (catalog.Plugins.Count == 0) _body.Children.Add(Detail("暂无已安装插件。"));
            foreach (var plugin in catalog.Plugins)
            {
                var manifest = plugin.Manifest;
                var info = new StackPanel { Spacing = 3 };
                info.Children.Add(new TextBlock { Text = manifest.Name, TextWrapping = TextWrapping.Wrap });
                info.Children.Add(Detail($"版本 {manifest.Version}"));
                var details = new Button
                {
                    Content = info,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(0),
                    Style = (Style)Application.Current.Resources["SubtleButtonStyle"]
                };
                AutomationProperties.SetName(details, $"{manifest.Name}，查看详情");
                details.Click += (_, _) => ShowPlugin(plugin, details.FocusState == FocusState.Keyboard);
                var uninstall = new Button { Content = "卸载" };
                AutomationProperties.SetName(uninstall, $"卸载 {manifest.Name}");
                var row = PluginRow(details, uninstall);
                uninstall.Click += async (_, _) =>
                    {
                        bool restoreFocus = uninstall.FocusState == FocusState.Keyboard;
                        uninstall.IsEnabled = false;
                        try
                        {
                            if (_preview is { } previewActions) previewActions.Uninstall(plugin);
                            else if (Path.GetDirectoryName(Path.GetDirectoryName(plugin.Directory)) is { } root)
                                await Task.Run(() => new PluginCatalog(new AppStorage(root)).Uninstall(plugin));
                        }
                        catch (Exception error) when (IsStorageError(error))
                        {
                            uninstall.IsEnabled = true;
                            info.Children.Add(Detail("卸载失败，请重试。"));
                            return;
                        }
                        if (_closed || !_body.Children.Contains(row)) return;
                        int position = _body.Children.IndexOf(row);
                        _body.Children.Remove(row);
                        if (_body.Children.Count == 0)
                            ShowStatus("暂无已安装插件。");
                        if (!restoreFocus) return;
                        if (position >= 0 && _body.Children[Math.Min(position, _body.Children.Count - 1)]
                            is Border { Child: Grid nextRow } && nextRow.Children[0] is Button nextDetails)
                            nextDetails.Focus(FocusState.Programmatic);
                        else
                            _manage.Focus(FocusState.Programmatic);
                    };
                _body.Children.Add(row);
            }
            foreach (var error in catalog.Errors) _body.Children.Add(Detail(error));
            if (restoreOffset > 0)
            {
                _viewport.UpdateLayout();
                ScrollTo(restoreOffset);
            }
        }
        catch (Exception error) when (IsStorageError(error))
        {
            if (!cancellationToken.IsCancellationRequested) ShowStatus("无法读取已安装插件。");
        }
    }

    private static CatalogResult ReadInstalled(CancellationToken cancellationToken)
    {
        var plugins = new Dictionary<string, InstalledPlugin>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (string root in new[] { PluginPreferences.Root, AppStorage.DefaultRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(Path.Combine(root, "plugins"))) continue;
            var result = new PluginCatalog(new AppStorage(root)).Discover(cancellationToken);
            foreach (var plugin in result.Plugins) plugins.TryAdd(plugin.Manifest.Id, plugin);
            errors.AddRange(result.Errors);
        }
        return new(plugins.Values.OrderBy(plugin => plugin.Manifest.Name).ToArray(), errors);
    }

    private void ShowPlugin(InstalledPlugin plugin, bool keyboardNavigation)
    {
        _load?.Cancel();
        double previousOffset = _viewport.VerticalOffset;
        _body.Children.Clear();
        ScrollTo(0);
        var manifest = plugin.Manifest;
        var parentTitle = PageLayout.BreadcrumbText("管理");
        var back = new Button
        {
            Content = parentTitle,
            Padding = new Thickness(0),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"]
        };
        AutomationProperties.SetName(back, "管理，返回插件列表");
        back.Click += (_, _) =>
        {
            bool restoreFocus = back.FocusState == FocusState.Keyboard;
            ShowMainHeader();
            if (restoreFocus) _manage.Focus(FocusState.Keyboard);
            _ = ShowTabAsync(library: false, restoreOffset: previousOffset);
        };

        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAccessibilityView(chevron, AccessibilityView.Raw);
        var currentTitle = PageLayout.HeadingText(manifest.Name);
        currentTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        var breadcrumb = new Grid { ColumnSpacing = 12, MinHeight = PageLayout.HeadingRowHeight };
        breadcrumb.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        breadcrumb.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        breadcrumb.ColumnDefinitions.Add(new ColumnDefinition());
        breadcrumb.Children.Add(back);
        Grid.SetColumn(chevron, 1);
        breadcrumb.Children.Add(chevron);
        Grid.SetColumn(currentTitle, 2);
        breadcrumb.Children.Add(currentTitle);
        _header.Children.Clear();
        _header.Children.Add(breadcrumb);
        if (keyboardNavigation) back.Focus(FocusState.Keyboard);
        if (!string.IsNullOrWhiteSpace(manifest.Description)) _body.Children.Add(Detail(manifest.Description));
        _body.Children.Add(Detail($"版本 {manifest.Version}  ·  {manifest.Id}"));
        if (!string.IsNullOrWhiteSpace(manifest.Author)) _body.Children.Add(Detail($"开发者  {manifest.Author}"));

        AddSection("运行环境", manifest.Disclosure?.Runtimes.Select(runtime =>
            $"{runtime.Name} {runtime.Version}  ·  {runtime.Architecture}  ·  {Provision(runtime.Provision)}\n{runtime.Description}")
            ?? [RuntimeSummary(plugin)]);
        AddSection("兼容性", [$"最低主体版本  {manifest.MinimumHostVersion}", .. manifest.RequiredCapabilities]);
        AddSection("功能", manifest.Commands.Select(command => command.Name));
        AddSection("依赖", manifest.Disclosure?.Dependencies.Select(item => $"{item.Name} {item.Version}") ?? []);
        AddSection("权限", manifest.Permissions);
    }

    private void AddSection(string title, IEnumerable<string> values)
    {
        var content = new StackPanel { Spacing = 4 };
        foreach (string value in values) content.Children.Add(Detail(value));
        if (content.Children.Count == 0) return;
        _body.Children.Add(PageLayout.SectionHeading(title));
        _body.Children.Add(Card(content));
    }

    private static string RuntimeSummary(InstalledPlugin plugin)
    {
        var manifest = plugin.Manifest;
        if (manifest.Disclosure?.Runtimes is { Length: > 0 } runtimes)
            return string.Join("、", runtimes.Select(runtime => $"{runtime.Name} {runtime.Version}"));

        try
        {
            if (plugin.Directory.Length > 0)
            {
                string entry = PluginCatalog.ResolveInside(plugin.Directory, manifest.EntryPoint);
                string path = Path.ChangeExtension(entry, ".runtimeconfig.json");
                var config = new FileInfo(path);
                if (config.Exists && config.Length <= 128 * 1024)
                {
                    using var data = JsonDocument.Parse(File.ReadAllText(path));
                    if (data.RootElement.TryGetProperty("runtimeOptions", out var options)
                        && options.TryGetProperty("tfm", out var tfm)
                        && tfm.GetString() is { } target)
                    {
                        string name = target.StartsWith("net", StringComparison.Ordinal) ? $".NET {target[3..]}" : target;
                        return options.TryGetProperty("includedFrameworks", out _) ? $"{name}（随插件提供）" : name;
                    }
                }
            }
        }
        catch (Exception error) when (IsStorageError(error) || error is JsonException or InvalidOperationException) { }

        return manifest.RequiredCapabilities.Contains("runtime.dotnet10.shared.v1", StringComparer.Ordinal)
            ? ".NET 10（由主体提供）" : "运行环境未声明";
    }

    private static string Provision(string value) => value switch
    {
        "host" => "由主体提供",
        "bundled" => "随插件提供",
        _ => "由系统提供"
    };

    private sealed record LibraryEntry(string Name, string Version, string Description, InstalledPlugin? Preview = null, MarketPlugin? Market = null);

    private void ShowLibrary(LibraryEntry[] entries)
    {
        if (entries.Length == 0) return;
        var list = new ItemsRepeater
        {
            ItemsSource = entries,
            ItemTemplate = (DataTemplate)Application.Current.Resources["PluginLibraryRowTemplate"],
            Layout = new StackLayout { Spacing = PageLayout.SettingsRowGap },
            VerticalCacheLength = 1
        };
        list.ElementPrepared += (_, args) =>
        {
            var entry = entries[args.Index];
            var row = (FrameworkElement)args.Element;
            ((TextBlock)row.FindName("PluginName")).Text = entry.Name;
            ((TextBlock)row.FindName("Version")).Text = $"版本 {entry.Version}";
            var details = (Button)row.FindName("Details");
            details.Tag = entry;
            AutomationProperties.SetName(details, $"{entry.Name} 详情");
            details.Click += ShowLibraryDetails;
            var button = (Button)row.FindName("Download");
            bool available = _preview is not null && entry.Preview is not null;
            bool downloaded = entry.Preview is { } plugin && _preview?.IsDownloaded(plugin) == true;
            SetDownloadState(button, entry.Name, available, downloaded);
            if (_lab?.AcceptsImports == true && entry.Market is { } market)
            {
                button.Content = "下载";
                button.IsEnabled = !OfficialPluginLibrary.IsOfficial(market.SourceUrl) || market.Bytes is > 0;
            }
            ToolTipService.SetToolTip(button, null);
            button.Tag = (object?)entry.Preview ?? entry.Market;
            button.Click += DownloadPreview;
        };
        list.ElementClearing += (_, args) =>
        {
            var row = (FrameworkElement)args.Element;
            var details = (Button)row.FindName("Details");
            details.Click -= ShowLibraryDetails;
            details.Tag = null;
            var button = (Button)row.FindName("Download");
            button.Click -= DownloadPreview;
            button.Tag = null;
        };
        _body.Children.Add(list);
    }

    private async void ShowLibraryDetails(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: LibraryEntry entry } button || button.XamlRoot is not { } root) return;
        var market = entry.Market;
        var content = new StackPanel { Spacing = 6 };
        void Add(string label, string? value) =>
            content.Children.Add(Detail($"{label}　{(string.IsNullOrWhiteSpace(value) ? "未提供" : value)}"));
        Add("版本", entry.Version);
        Add("作者", market?.Author);
        Add("适用 HHub 版本", market?.MinimumHostVersion is { Length: > 0 } minimum ? $"{minimum} 或更新" : null);
        Add("系统", market?.Platform == "windows-x64" ? "Windows x64" : market?.Platform);
        Add("发布渠道", market?.Channel switch { "preview" => "预览", "stable" => "正式", var channel => channel });
        Add("介绍", entry.Description);
        await new ContentDialog
        {
            Title = entry.Name, XamlRoot = root, CloseButtonText = "关闭",
            Content = new ScrollViewer { MaxHeight = 350, Content = content }
        }.ShowAsync();
    }

    private async void DownloadPreview(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: MarketPlugin market } install && _lab is { AcceptsImports: true } lab)
        {
            string temporary = Path.Combine(Path.GetTempPath(), $"hhub-plugin-{Guid.NewGuid():N}.hhub");
            install.IsEnabled = false;
            install.Content = "正在安装…";
            try
            {
                await OfficialPluginLibrary.DownloadAsync(market, temporary, default);
                await lab.InstallPackageAsync(temporary, market.Id, market.Version);
                if (ReferenceEquals(install.Tag, market)) install.Content = "已安装";
            }
            catch (Exception error) when (IsStorageError(error) || error is System.Net.Http.HttpRequestException
                or System.Security.Cryptography.CryptographicException or OperationCanceledException)
            {
                if (ReferenceEquals(install.Tag, market))
                {
                    install.Content = "重试";
                    install.IsEnabled = true;
                    ToolTipService.SetToolTip(install, error.Message);
                }
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
            return;
        }
        if (sender is not Button { Tag: InstalledPlugin plugin } button || _preview is null) return;
        _preview.Download(plugin);
        SetDownloadState(button, plugin.Manifest.Name, available: true, downloaded: true);
    }

    private static void SetDownloadState(Button button, string name, bool available, bool downloaded)
    {
        button.Content = downloaded ? "已下载" : "下载";
        button.IsEnabled = available && !downloaded;
        AutomationProperties.SetName(button, $"{button.Content} {name}");
    }

    private async Task LoadLibraryAsync(CancellationToken cancellationToken)
    {
        _body.Children.Add(Detail("正在读取库…"));
        try
        {
            var sources = PluginMarkets.Configured(PluginPreferences.Load());
            if (sources.Length == 0)
            {
                ShowStatus("尚未连接插件源。");
                return;
            }

            var result = await PluginMarkets.ReadAsync(sources, cancellationToken, OfficialPluginLibrary.ReadAsync);
            cancellationToken.ThrowIfCancellationRequested();
            _body.Children.Clear();
            if (result.Plugins.Length == 0 && result.Errors.Length == 0)
                _body.Children.Add(Detail("库中暂无可下载的插件。"));
            ShowLibrary(result.Plugins.Select(plugin => new LibraryEntry(plugin.Name, plugin.Version, plugin.Description, Market: plugin)).ToArray());
            foreach (string error in result.Errors)
            {
                string message = error.StartsWith(OfficialPluginLibrary.Source + "：", StringComparison.Ordinal)
                    ? error[(OfficialPluginLibrary.Source.Length + 1)..] : "一个自定义插件源无法读取。";
                _body.Children.Add(Detail(message));
            }
        }
        catch (Exception error) when (IsStorageError(error) || error is JsonException or ArgumentException)
        {
            if (!cancellationToken.IsCancellationRequested) ShowStatus("无法读取插件源。");
        }
    }

    private void ShowStatus(string message)
    {
        _body.Children.Clear();
        _body.Children.Add(Detail(message));
    }

    private void ScrollTo(double offset) => _viewport.ChangeView(null, offset, null, disableAnimation: true);

    private static Border PluginRow(FrameworkElement info, FrameworkElement action)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(info);
        if (action is Button { Content: not "卸载" } button)
        {
            button.Style ??= (Style)Application.Current.Resources["AccentButtonStyle"];
            button.MinWidth = 72;
        }
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        row.Children.Add(action);
        var card = Card(row);
        card.MinHeight = PageLayout.SettingsRowHeight;
        card.Padding = PageLayout.SettingsRowPadding;
        return card;
    }

    private static Border Card(UIElement content) => new()
    {
        Child = content,
        Style = (Style)Application.Current.Resources["DashboardCardStyle"]
    };

    private static TextBlock Detail(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["DashboardDetailStyle"]
    };

    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException;
}
