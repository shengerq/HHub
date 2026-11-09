using System.Reflection;
using System.Text.Json;
using Hhub.Core;
using Hhub.PluginContracts;
using Hhub.UnifiedHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace Hhub.App;

// New host path only. No legacy plugin conversion, bundled services or scene engine.
public sealed class UnifiedWindow : Window
{
    private readonly PluginStore _store;
    private readonly InstanceLease? _lease;
    private readonly NavigationView _navigation = new() { PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 208, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed };
    private readonly StackPanel _body = new() { Spacing = 16, Margin = new Thickness(28) };
    private readonly TextBlock _heading = new() { FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly StackPanel _items = new() { Spacing = 12 };
    private readonly Dictionary<string, PluginSession> _sessions = [];
    private readonly Dictionary<string, TextBlock> _readouts = [];
    private readonly Dictionary<string, TextBox> _editors = [];
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _page = "home";
    private string? _visiblePlugin;
    private bool _closing, _shutdownReady;
    private bool _polling;

    public UnifiedWindow()
    {
        Title = "Hhub · 主体预览";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hhub.ico"));
        AppWindow.Resize(new SizeInt32(1200, 800));
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hhub-Preview-v1");
#if HHUB_TEST_CERTIFICATION
        root = Path.Combine(root, "test-certification");
#endif
        root = HostProfileLocation.Resolve(AppContext.BaseDirectory, root);
        _lease = InstanceLease.TryAcquire(root);
        if (_lease is null) { _store = null!; DispatcherQueue.TryEnqueue(Close); return; }
        string[] trusted = [];
#if HHUB_TEST_CERTIFICATION
        trusted = typeof(UnifiedWindow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(item => item.Key == "Hhub.TestCertification" && !string.IsNullOrWhiteSpace(item.Value)).Select(item => item.Value!).ToArray();
#endif
        _store = new(root, new CertificationPolicy(trusted), Environment.ProcessPath!);
        _navigation.PaneTitle = "Hhub";
        AddNavigation("主页", "home", Symbol.Home);
        AddNavigation("设备", "devices", Symbol.View);
        AddNavigation("插件", "plugins", Symbol.AllApps);
        _navigation.SelectionChanged += async (_, args) =>
        {
            if (_closing) return;
            await RunAsync(async () =>
            {
                await ReleasePageAsync();
                _page = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";
                await RenderAsync();
            });
        };
        _body.Children.Add(_heading); _body.Children.Add(_notice); _body.Children.Add(_items);
        _navigation.Content = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = _navigation;
        AppWindow.Closing += async (_, args) =>
        {
            if (_shutdownReady) return;
            args.Cancel = true;
            if (_closing) return;
            _closing = true; _timer.Stop();
            await RunAsync(async () =>
            {
                foreach (var session in _sessions.Values) await session.DisposeAsync();
                _sessions.Clear(); _shutdownReady = true; Close();
            });
            if (!_shutdownReady) _closing = false;
        };
        Closed += (_, _) => { _timer.Stop(); _lease?.Dispose(); };
        var shutdownRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceLease.NameFor(root) + ".ExitForUpdate.v1");
        var listener = ThreadPool.RegisterWaitForSingleObject(shutdownRequest,
            (_, _) => DispatcherQueue.TryEnqueue(Close), null, Timeout.Infinite, executeOnlyOnce: false);
        Closed += (_, _) => { listener.Unregister(null); shutdownRequest.Dispose(); };
        _timer.Tick += async (_, _) => await PollVisibleAsync();
        _navigation.SelectedItem = _navigation.MenuItems[0];
    }

    private void AddNavigation(string title, string tag, Symbol symbol) => _navigation.MenuItems.Add(new NavigationViewItem
    { Content = title, Tag = tag, Icon = new SymbolIcon(symbol) });

    private static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static Border Card(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(18), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
        Background = Application.Current.Resources["HhubCardFillBrush"] as Brush,
        BorderBrush = Application.Current.Resources["HhubCardStrokeBrush"] as Brush
    };
    private static Button Button(string title, Func<Task> action)
    {
        var button = new Button { Content = title };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } finally { button.IsEnabled = true; } };
        return button;
    }

    private async Task RenderAsync()
    {
        _items.Children.Clear(); _readouts.Clear(); _editors.Clear(); _timer.Stop();
        var catalog = _store.Discover();
        if (catalog.Errors.Length > 0) ShowError(string.Join("\n", catalog.Errors));
        _heading.Text = _page switch { "plugins" => "插件", "devices" => "设备", "settings" => "设置", _ => "主页" };
        if (_page == "home")
        {
            _items.Children.Add(Card(Text("按需添加你需要的设备和功能", 20)));
            _items.Children.Add(Text($"已安装 {catalog.Plugins.Length} 个插件 · 正在运行 {_sessions.Values.Count(session => session.IsRunning)} 个"));
            _items.Children.Add(Text("主体不附带系统监测、EQ 或远控。设备支持和功能可分别安装。"));
#if HHUB_TEST_CERTIFICATION
            _items.Children.Add(Text("这是模拟插件验收构建，接受临时测试认证；测试设备和音效均不操作真实硬件。"));
#endif
        }
        else if (_page == "devices")
        {
            _items.Children.Add(Button("刷新设备", () => RunAsync(RenderDevicesAsync)));
            await RenderDevicesAsync();
        }
        else if (_page == "plugins")
        {
            _items.Children.Add(Button("安装认证插件", () => RunAsync(InstallAsync)));
            if (catalog.Plugins.Length == 0) _items.Children.Add(Text("尚未安装插件。"));
            foreach (var plugin in catalog.Plugins)
            {
                var panel = new StackPanel { Spacing = 10 };
                panel.Children.Add(Text(plugin.Definition.Name, 18));
                panel.Children.Add(Text($"{plugin.Definition.Version} · {(plugin.Definition.Kind == "device" ? "设备插件" : "功能插件")} · 认证有效"));
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                actions.Children.Add(Button("打开", () => RunAsync(() => OpenAsync(plugin))));
                actions.Children.Add(Button("停止", () => RunAsync(async () => { await StopAsync(plugin.Definition.Id); await RenderAsync(); })));
                actions.Children.Add(Button("卸载", () => RunAsync(() => UninstallAsync(plugin))));
                panel.Children.Add(actions); _items.Children.Add(Card(panel));
            }
        }
        else
        {
            _items.Children.Add(Text("主题", 18));
            var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = 0 };
            theme.SelectionChanged += (_, _) => _body.RequestedTheme = theme.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
            _items.Children.Add(theme);
            _items.Children.Add(Button("清理全部插件和缓存", () => RunAsync(CleanupProfileAsync)));
            _items.Children.Add(Text("主体预览使用独立数据目录，不读取或迁移旧版插件与个人配置。"));
            _items.Children.Add(Text("主体安装、自动更新与完整卸载仍在开发中。此预览尚未通过完整主体验收。"));
        }
    }

    private async Task RenderDevicesAsync()
    {
        var devices = await PlatformServices.EnumerateDevicesAsync();
        while (_items.Children.Count > 1) _items.Children.RemoveAt(1);
        foreach (var device in devices) _items.Children.Add(Card(Text(device.Name + " · " + device.Category)));
        if (devices.Count == 0) _items.Children.Add(Text("没有发现可显示的外设。安装对应设备插件后可调节其功能。"));
    }

    private async Task InstallAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".hhub");
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        if (_sessions.Values.Any(session => session.IsRunning)) throw new InvalidOperationException("请先停止正在运行的插件，再安装或更新。");
        await _store.InstallAsync(file.Path); await RenderAsync();
    }

    private async Task OpenAsync(InstalledPackage plugin, string? targetId = null)
    {
        foreach (var required in plugin.Definition.Requires.Where(service => !service.Optional))
            _ = ServiceCatalog.Resolve(required, _store.Discover().Plugins);
        if (_visiblePlugin != plugin.Definition.Id) await ReleasePageAsync();
        _timer.Stop();
        if (!_sessions.TryGetValue(plugin.Definition.Id, out var session) || !session.IsRunning)
        {
            if (session is not null) await session.DisposeAsync();
            session = await PluginSession.StartAsync(_store, plugin, Environment.ProcessPath!);
            _sessions[plugin.Definition.Id] = session;
        }
        _visiblePlugin = plugin.Definition.Id;
        _heading.Text = plugin.Definition.Name; _items.Children.Clear(); _readouts.Clear(); _editors.Clear();
        var online = session.Targets.Where(target => target.Online).ToArray();
        var targets = new ComboBox { ItemsSource = online, DisplayMemberPath = nameof(PluginTarget.Name), PlaceholderText = "选择要调节的目标" };
        string? selected = targetId ?? (online.Length == 1 ? online[0].Id : null);
        targets.SelectedItem = online.FirstOrDefault(target => target.Id == selected);
        targets.SelectionChanged += async (_, _) =>
        {
            if (targets.SelectedItem is PluginTarget target) await RunAsync(() => OpenAsync(plugin, target.Id));
        };
        _items.Children.Add(targets);
        if (selected is null)
        {
            _items.Children.Add(Text(online.Length == 0 ? "当前没有可用目标。" : "请选择具体目标，不会自动合并同型号设备。"));
            _items.Children.Add(Button("返回插件", () => RunAsync(async () => { await ReleasePageAsync(); _page = "plugins"; await RenderAsync(); })));
            return;
        }
        await session.ReadAsync(selected);
        if (online.First(target => target.Id == selected).Simulated) _items.Children.Add(Text("此目标为模拟实例，不操作真实硬件。"));
        foreach (var capability in plugin.Definition.Capabilities)
        {
            var row = new StackPanel { Spacing = 8 };
            row.Children.Add(Text(capability.Name, 18));
            var readout = Text(session.Values[capability.Id].GetRawText()); _readouts[capability.Id] = readout; row.Children.Add(readout);
            if (capability.Writable)
            {
                var editor = new TextBox { Text = session.Values[capability.Id].GetRawText(), AcceptsReturn = capability.Value.Type is "object" or "array", MaxLength = PluginApi.MaxMessageCharacters };
                _editors[capability.Id] = editor;
                row.Children.Add(editor);
                row.Children.Add(Button("应用", () => RunAsync(async () =>
                {
                    using var value = JsonDocument.Parse(editor.Text);
                    await session.SetAsync(capability.Id, value.RootElement.Clone()); UpdateReadouts(session);
                })));
            }
            _items.Children.Add(Card(row));
        }
        _items.Children.Add(Button("保存预设", () => RunAsync(() =>
        {
            var saved = session.Values.Where(pair => plugin.Definition.Capabilities.Any(capability =>
                capability.Id == pair.Key && capability.Writable && capability.Persistence != "session"))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
            _store.SavePreset(new(1, plugin.Definition.Id, "个人预设", saved, TargetId: session.SelectedTargetId));
            return Task.CompletedTask;
        })));
        _items.Children.Add(Button("读取已保存预设", () => RunAsync(() =>
        {
            var preset = _store.LoadPreset(plugin.Definition.Id, targetId: session.SelectedTargetId) ?? throw new InvalidOperationException("尚无此目标的已保存预设。");
            _notice.Text = JsonSerializer.Serialize(preset.Values); _notice.Visibility = Visibility.Visible; return Task.CompletedTask;
        })));
        _items.Children.Add(Button("应用已保存预设", () => RunAsync(async () =>
        {
            var preset = _store.LoadPreset(plugin.Definition.Id, targetId: session.SelectedTargetId)
                ?? throw new InvalidOperationException("尚无此目标的已保存预设。");
            await session.ApplyPresetAsync(preset); UpdateReadouts(session, updateEditors: true);
            _notice.Text = "预设已应用，并已读回确认。"; _notice.Visibility = Visibility.Visible;
        })));
        _items.Children.Add(Button("返回插件", () => RunAsync(async () => { await ReleasePageAsync(); _page = "plugins"; await RenderAsync(); })));
        _timer.Start();
    }

    private void UpdateReadouts(PluginSession session, bool updateEditors = false)
    {
        foreach (var (id, readout) in _readouts) if (session.Values.TryGetValue(id, out var value)) readout.Text = value.GetRawText();
        if (updateEditors)
            foreach (var (id, editor) in _editors) if (session.Values.TryGetValue(id, out var value)) editor.Text = value.GetRawText();
    }
    private async Task PollVisibleAsync()
    {
        if (_polling || _closing || _visiblePlugin is not { } id || !_sessions.TryGetValue(id, out var session)) return;
        _polling = true;
        try { await RunAsync(async () => { await session.ReadAsync(); UpdateReadouts(session); }); }
        finally { _polling = false; }
    }
    private async Task ReleasePageAsync()
    {
        _timer.Stop(); string? id = _visiblePlugin; _visiblePlugin = null;
        if (id is not null && _sessions.TryGetValue(id, out var session) && !session.BackgroundActivity) await StopAsync(id);
    }
    private async Task StopAsync(string id)
    {
        if (_sessions.TryGetValue(id, out var session)) { await session.DisposeAsync(); _sessions.Remove(id); }
        if (_visiblePlugin == id) { _visiblePlugin = null; _timer.Stop(); }
    }
    private async Task UninstallAsync(InstalledPackage plugin)
    {
        var keep = new CheckBox { Content = "保留个人预设和设置", IsChecked = false };
        var dialog = new ContentDialog { XamlRoot = _body.XamlRoot, Title = "卸载 " + plugin.Definition.Name,
            Content = keep, PrimaryButtonText = "卸载", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await StopAsync(plugin.Definition.Id); await _store.UninstallAsync(plugin.Definition.Id, keep.IsChecked == true); await RenderAsync();
    }
    private async Task CleanupProfileAsync()
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Text("将停止全部插件，移除已安装插件、缓存、日志和更新暂存。主程序仍然保留。"));
        var keep = new CheckBox { Content = "保留 Hhub 保存的个人预设", IsChecked = true };
        content.Children.Add(keep);
        content.Children.Add(Text("保留预设不会保留插件程序、私有文件或临时任务。"));
        var dialog = new ContentDialog { XamlRoot = _body.XamlRoot, Title = "清理插件与数据", Content = content,
            PrimaryButtonText = "清理", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _timer.Stop();
        foreach (string id in _sessions.Keys.ToArray()) await StopAsync(id);
        var result = await _store.CleanupAsync(keep.IsChecked == true);
        await RenderAsync();
        _notice.Text = $"已清理受管插件和缓存，保留 {result.PresetsKept} 份预设。主程序仍然保留。";
        _notice.Visibility = Visibility.Visible;
    }
    private void ShowError(string message) { _notice.Text = message; _notice.Visibility = Visibility.Visible; }
    private async Task RunAsync(Func<Task> action)
    {
        await _operation.WaitAsync();
        try { _notice.Visibility = Visibility.Collapsed; await action(); }
        catch (Exception error) { ShowError(error.Message); }
        finally { _operation.Release(); }
    }
}
