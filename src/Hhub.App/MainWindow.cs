using System.Diagnostics;
using System.Text.Json;
using Hhub.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace Hhub.App;

public sealed partial class MainWindow : Window
{
    private const double ExpandedSidebarWidth = ShellLayout.SidebarExpandedWidth;
    private const double CollapsedSidebarWidth = ShellLayout.SidebarCollapsedWidth;
    private readonly AppStorage _storage = new();
    private readonly PluginCatalog _catalog;
    private readonly PluginRuntime _runtime = new() { SharedRuntimeHostPath = Environment.ProcessPath!, SharedRuntimeHostArguments = ["--plugin"], PreferSharedManagedBackends = true };
    private readonly Grid _root = new() { AllowDrop = true };
    private readonly Grid _content = new() { AllowDrop = true };
    private readonly Grid _heading = new();
    private readonly Grid _main = new() { AllowDrop = true };
    private readonly NavigationView _navigationView = new() { AllowDrop = true };
    private readonly Dictionary<string, NavigationViewItem> _navigationItems = [];
    private readonly TextBlock _brandName = Text("Hhub", 20, true);
    private readonly TextBlock _currentSceneStatus = Text("", 14);
    private readonly Border _currentSceneStatusHost = new() { MinHeight = 28 };
    private readonly TextBlock _title = Text("", SectionTextSize, true);
    private readonly StackPanel _titleControls = new() { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
    private Color _accentColor = Color.FromArgb(255, 0, 120, 215);
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly ColumnDefinition _brandColumn = new();
    private readonly Border _noticeBorder;
    private readonly TextBlock _notice = Text("", 15);
    private readonly DispatcherTimer _sceneTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<string> _appliedScenes = [];
    private readonly Dictionary<string, TextBlock> _metricViews = [];
    private CatalogResult _installed = new([], []);
    private IReadOnlyList<DeviceRecord> _devices = [];
    private List<SceneRule> _rules;
    private List<SceneProfile> _profiles;
    private List<DeviceSceneBinding> _bindings;
    private UserPreferences _preferences;
    private string _page = Environment.GetCommandLineArgs().Any(argument => argument is "--scene-preview" or "--scenes") ? "scenes"
        : Environment.GetCommandLineArgs().Contains("--plugins") ? "plugins"
        : Environment.GetCommandLineArgs().Any(argument => argument is "--settings" or "--sponsor" or "--about" or "--plugin-settings") ? "settings" : "home";
    private InstalledPlugin? _openPlugin;
    private bool _pluginSettingsOpen;
    private DeviceRecord? _openDevice;
    private string _deviceFilter = "全部";
    private string _pluginTab = Environment.GetCommandLineArgs().Contains("--plugins") ? "manage" : "market";
    private string _pluginSearch = "";
    private string _runtimeState = "";
    private Grid? _pluginRowsHost;
    private bool _dark;
    private bool _compact;
    private bool _summaryStacked;
    private bool _scenesPaused;
    private bool _sceneBusy;
    private bool _closing;
    private bool _shutdownReady;
    private bool _servicesInitialized;
    private Grid? _shellContent;
    private Grid? _titleBarDragRegion;

    public void Launch()
    {
        if (_preferences.StartToTray && Environment.GetCommandLineArgs().Contains("--startup"))
        {
            InitializeTray();
            if (_tray?.IsAvailable == true)
            {
                _hiddenToTray = true;
                AppWindow.Hide(); ApplyTrayEfficiency();
                ReleaseHiddenPage();
                _ = RunUiActionAsync(InitializeServicesAsync);
                return;
            }
        }
        Activate();
    }

    private async Task InitializeServicesAsync()
    {
        if (_servicesInitialized) return;
        _servicesInitialized = true;
        await RunUiActionAsync(EnsureBundledMonitorAsync);
        if (_preferences.WatchDevices) StartWatchingDevices();
        await RefreshDevicesAsync();
    }

    public MainWindow()
    {
        Title = "Hhub";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hhub.ico"));
        _catalog = new(_storage);
        _preferences = _storage.Load("preferences.json", new UserPreferences());
        _rules = _storage.Load("scenes.json", new List<SceneRule>());
        _profiles = _storage.Load("scene-profiles.json", new List<SceneProfile>());
        if (_preferences.ScenePresetVersion < ScenePresets.Version)
        {
            var presets = ScenePresets.Create(_profiles, _rules);
            string backup = Path.Combine(_storage.Root, "backups", "scene-presets-" + Guid.NewGuid().ToString("N"));
            foreach (string name in new[] { "scene-profiles.json", "scenes.json", "preferences.json" })
            {
                string source = Path.Combine(_storage.Root, name);
                if (!File.Exists(source)) continue;
                Directory.CreateDirectory(backup); File.Copy(source, Path.Combine(backup, name));
            }
            _storage.Save("scene-profiles.json", presets.Profiles);
            _storage.Save("scenes.json", presets.Rules);
            _preferences = _preferences with { ScenePresetVersion = ScenePresets.Version, AutomaticScenes = true, CurrentSceneId = presets.DailySceneId };
            SavePreferences(); _profiles = presets.Profiles; _rules = presets.Rules;
        }
        _bindings = _storage.Load("device-scenes.json", new List<DeviceSceneBinding>());
        _scenesPaused = !_preferences.AutomaticScenes;
        _sceneTimer.Interval = TimeSpan.FromSeconds(_preferences.SceneIntervalSeconds);
        _noticeBorder = new Border { CornerRadius = new(8), BorderThickness = new(1), Padding = new(14, 10, 14, 10), Child = _notice, Visibility = Visibility.Collapsed };
        _runtime.Dispatch = action => DispatcherQueue.TryEnqueue(() => action());
        _runtime.HostRequest = HandleRemoteHostRequestAsync;
        _runtime.Changed += RuntimeChanged;
        _runtime.FirstSnapshotReceived += RuntimeFirstSnapshotReceived;
        _runtimeRefreshTimer.Tick += OnRuntimeRefresh;
        ExtendsContentIntoTitleBar = true;
        double scale = PlatformServices.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min((int)(1280 * scale), workArea.Width), Math.Min((int)(820 * scale), workArea.Height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(1000 * scale), workArea.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(740 * scale), workArea.Height);
        }
        BuildShell();
        ApplyTheme();
        LoadPluginState();
        var recoveryErrors = _catalog.RecoverInterruptedUpdates();
        ReloadPlugins();
        Render();
        if (recoveryErrors.Length > 0) ShowError(string.Join("；", recoveryErrors));
        _root.Loaded += async (_, _) =>
        {
            _uiLoaded = true; InitializeTray();
            if (_navigationView.SettingsItem is NavigationViewItem settings)
            {
                settings.Icon = Icon("\uE713", ShellLayout.NavigationIconSize); settings.FontSize = ShellLayout.NavigationFontSize; settings.MinHeight = ShellLayout.NavigationRowHeight;
            }
            await RunUiActionAsync(InitializeServicesAsync);
            if (_visualRefreshPending) RequestVisualRefresh();
        };
        Closed += (_, _) => { _tray?.Dispose(); _backgroundEfficiency.Dispose(); _processEfficiency.Dispose(); };
        AppWindow.Changed += (_, _) =>
        {
            if (!_restoringFromTray && _preferences.MinimizeToTray
                && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) HideToTray();
        };
        _root.ActualThemeChanged += (_, _) => RequestVisualRefresh();
        _main.SizeChanged += (_, _) => ApplyPageInsets();
        _content.SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < 1000;
            bool stacked = compact;
            if (compact == _compact && stacked == _summaryStacked) return;
            _compact = compact;
            _summaryStacked = stacked;
            if (_page == "settings" && _openPlugin is null && _openDevice is null) return;
            if (_visualRefreshPending) RequestVisualRefresh();
            else Render();
        };
        _sceneTimer.Tick += async (_, _) =>
        {
            _tray?.UpdateBatteryTip();
            if (!_hiddenToTray) { UpdateMetricViews(); UpdateMonitoringData(); UpdateDeviceViews(); }
            QueueActivation();
            await EvaluateScenesAsync();
            UpdateSceneDetailApplications();
            if (!_hiddenToTray && _page == "scenes" && _sceneViewDirty && DateTimeOffset.UtcNow >= _sceneTapDeadline) Render();
        };
        _sceneTimer.Start();
        _trayPageReleaseTimer.Tick += (_, _) => ReleaseHiddenPage();
        AppWindow.Closing += async (_, args) =>
        {
            if (_shutdownReady) return;
            args.Cancel = true;
            if (_preferences.CloseToTray && _tray?.IsAvailable == true)
            {
                HideToTray(); return;
            }
            await ShutdownAsync();
        };
        var shutdownRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceLease.NameFor(_storage.Root) + ".ExitForUpdate.v1");
        var listener = ThreadPool.RegisterWaitForSingleObject(shutdownRequest,
            (_, _) => DispatcherQueue.TryEnqueue(ExitFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
        Closed += (_, _) => { listener.Unregister(null); shutdownRequest.Dispose(); };
    }

    private async Task ShutdownAsync()
    {
        if (_closing) return;
        if (_localPackageInstallBusy || _installingPlugins.Count > 0)
        {
            ShowError("插件操作尚未结束，完成后请重试退出。");
            return;
        }
        _closing = true;
        AppWindow.Hide();
        _hiddenToTray = true;
        _sceneTimer.Stop();
        _trayPageReleaseTimer.Stop();
        _runtimeRefreshTimer.Stop();
        _runtimeRefreshQueued = false;
        StopWatchingDevices();
        try
        {
            await _runtime.DisposeAsync();
            ClearPageViews();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _closing = false;
            _hiddenPageDirty = true;
            ShowError("插件退出失败：" + ex.Message);
            _sceneTimer.Start();
            if (_preferences.WatchDevices) StartWatchingDevices();
            RestoreFromTray();
            return;
        }
        _shutdownReady = true;
        Close();
    }

    private static TextBlock Text(string value, double size = 14, bool strong = false) => new()
    {
        Text = value, FontSize = size,
        LineHeight = size == BodyTextSize ? 20 : size == SectionTextSize ? 32 : 0,
        FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
    };

    private static FontIcon Icon(string glyph, double size = 20) => new()
        { Glyph = glyph, FontSize = size, FontFamily = new("Segoe Fluent Icons") };

    private Brush ColorBrush(byte r, byte g, byte b, byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
    private Brush Accent => new SolidColorBrush(_accentColor);
    private Brush Selection => _dark ? ColorBrush(255, 255, 255, 26) : ColorBrush(0, 0, 0, 10);
    private Brush Stroke => _dark ? ColorBrush(255, 255, 255, 20) : ColorBrush(0, 0, 0, 15);
    private Brush Surface => PanelSurface;
    private Brush QuietSurface => PanelSurface;
    private Brush Muted => _dark ? ColorBrush(207, 207, 207) : ColorBrush(96, 96, 96);
    private Brush ErrorBrush => _dark ? ColorBrush(255, 116, 116) : ColorBrush(190, 35, 35);

    private Button Button(string label, Action action, bool accent = true)
    {
        var button = new Button { Content = label, Height = 32, MinHeight = 32, MinWidth = 88, Padding = new(12, 0, 12, 0), CornerRadius = new(4), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => action();
        return button;
    }

    private Button AsyncButton(string label, Func<Task> action, bool accent = true)
    {
        var button = Button(label, () => { }, accent);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { SetSettingNotice(button, null); await action(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException)
            {
                if (_settingsRowNotices.ContainsKey(button)) SetSettingNotice(button, ex.Message);
                else ShowError(ex.Message);
            }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private Border Card(UIElement child, bool error = false, Thickness? padding = null)
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["HhubCardStyle"],
            Padding = padding ?? new Thickness(20), Child = child
        };
        if (error) card.BorderBrush = ErrorBrush;
        return card;
    }

    private Border Placeholder(string glyph) => new()
    {
        Width = 48, Height = 48,
        Child = new FontIcon { Glyph = glyph, FontSize = 24, FontFamily = new("Segoe Fluent Icons") }
    };

    private string DeviceGlyph(DeviceRecord device) => (Describe(device).Category ?? device.Category) switch
    {
        "鼠标" => "\uE962", "键盘" => "\uE765", "触控板" => "\uEFA5", "键鼠复合设备" => "\uE961",
        "音频" or "耳机" => "\uE7F6", "手柄" => "\uE7FC", "蓝牙" => "\uE702", "摄像头" => "\uE722", "磁盘" => "\uEDA2", "打印机" => "\uE749", _ => "\uE772"
    };

    private void BuildShell()
    {
        _root.RowDefinitions.Add(new() { Height = new(48) });
        _root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var titleBar = new Grid();
        titleBar.ColumnDefinitions.Add(_brandColumn);
        titleBar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var brand = new Grid();
        brand.ColumnDefinitions.Add(new() { Width = new(CollapsedSidebarWidth) });
        brand.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var logo = new Button
        {
            Width = 48, Height = 48, Padding = new(0), CornerRadius = new(11),
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new(0),
            Content = new Image
            {
                Width = ShellLayout.BrandIconSize, Height = ShellLayout.BrandIconSize, Stretch = Stretch.Uniform,
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                {
                    DecodePixelWidth = 96,
                    UriSource = new Uri("ms-appx:///Assets/Hhub.png")
                }
            },
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        logo.Click += (_, _) =>
        {
            _preferences = _preferences with { SidebarCollapsed = !_preferences.SidebarCollapsed };
            SavePreferences();
            RefreshNavigation();
        };
        ToolTipService.SetToolTip(logo, "展开 / 收起侧栏");
        AutomationProperties.SetName(logo, "展开或收起侧栏");
        brand.Children.Add(logo);
        _brandName.Margin = new(12, 0, 0, 0);
        Grid.SetColumn(_brandName, 1);
        brand.Children.Add(_brandName);
        _appUpdateIndicator.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AutomationProperties.SetName(_appUpdateIndicator, "有可用更新，查看软件更新");
        _appUpdateIndicator.Click += (_, _) => { _settingsSection = "更新"; Navigate("settings"); };
        Grid.SetColumn(_appUpdateIndicator, 2); brand.Children.Add(_appUpdateIndicator);
        titleBar.Children.Add(brand);
        var dragRegion = new Grid { Margin = new(0, 0, 148, 0), Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetColumn(dragRegion, 1);
        titleBar.Children.Add(dragRegion);
        _root.Children.Add(titleBar);
        _titleBarDragRegion = dragRegion;

        var body = new Grid();
        Grid.SetRow(body, 1);
        _root.Children.Add(body);
        BuildNavigation();
        body.Children.Add(_navigationView);

        var main = _main;
        main.Padding = new(ShellLayout.PageInset, ShellLayout.RowGap, ShellLayout.PageInset, ShellLayout.SectionGap);
        main.RowDefinitions.Add(new() { Height = GridLength.Auto });
        main.RowDefinitions.Add(new() { Height = GridLength.Auto });
        main.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _heading.MinHeight = ShellLayout.HeadingHeight;
        _heading.Margin = new(0, 0, 0, ShellLayout.HeadingGap);
        _heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        titleRow.Children.Add(_title); titleRow.Children.Add(_titleControls);
        _heading.Children.Add(titleRow);
        _actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_actions, 1);
        _heading.Children.Add(_actions);
        main.Children.Add(_heading);
        Grid.SetRow(_noticeBorder, 1);
        _noticeBorder.Margin = new(0, 0, 0, 12);
        main.Children.Add(_noticeBorder);
        Grid.SetRow(_content, 2);
        main.Children.Add(_content);
        _navigationView.Content = main;
        _shellContent = PluginDropZone(_root);
        Content = _shellContent;
        SetTitleBar(_titleBarDragRegion);
        RefreshNavigation();
    }

    private void BuildNavigation()
    {
        _navigationView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        // Use the same icon box and row height for built-in, plugin and footer entries.
        _navigationView.Resources["NavigationViewItemOnLeftMinHeight"] = ShellLayout.NavigationRowHeight;
        _navigationView.Resources["NavigationViewItemOnLeftIconBoxHeight"] = ShellLayout.NavigationIconSize;
        foreach (string resource in new[] { "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground", "NavigationViewTopPaneBackground", "NavigationViewContentBackground", "NavigationViewContentGridBorderBrush" })
            _navigationView.Resources[resource] = new SolidColorBrush(Colors.Transparent);
        _navigationView.CompactPaneLength = CollapsedSidebarWidth;
        _navigationView.OpenPaneLength = ExpandedSidebarWidth;
        _navigationView.IsPaneToggleButtonVisible = false;
        _navigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _navigationView.IsTitleBarAutoPaddingEnabled = false;
        _navigationView.IsSettingsVisible = true;
        _navigationView.PaneTitle = "";
        foreach (var (page, name, glyph) in new[]
        {
            ("home", "主页", "\uE80F"), ("scenes", "场景", "\uE81E"), ("plugins", "插件", "\uEA86")
        })
        {
            var item = new NavigationViewItem { Content = name, Tag = page, Icon = Icon(glyph, ShellLayout.NavigationIconSize), FontSize = ShellLayout.NavigationFontSize, MinHeight = ShellLayout.NavigationRowHeight };
            if (page == "plugins") item.Loaded += (_, _) => AttachMainMenuDivider(item);
            _navigationItems.Add(page, item); _navigationView.MenuItems.Add(item);
        }
        _currentSceneStatusHost.Child = _currentSceneStatus;
        BuildSceneStatusFrame();
        _navigationView.ItemInvoked += async (_, args) =>
        {
            if (args.IsSettingsInvoked) { Navigate("settings"); return; }
            if (args.InvokedItemContainer is NavigationViewItem { Tag: InstalledPlugin plugin })
            {
                await RunUiActionAsync(() => OpenPluginNavigationAsync(plugin)); return;
            }
            if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string page) return;
            if (page == "plugins") _pluginTargetDevice = null;
            Navigate(page);
        };
    }

    private void RefreshNavigation()
    {
        double width = _preferences.SidebarCollapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth;
        _brandColumn.Width = new(width);
        _brandName.Visibility = _preferences.SidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        RefreshAppUpdateIndicator();
        _navigationView.IsPaneOpen = !_preferences.SidebarCollapsed;
        string selected = _page switch { "system" or "devices" => "home", "rules" or "sceneDetail" or "sceneCreate" => "scenes", _ => _page };
        _navigationView.SelectedItem = _openPlugin is { } open && _pluginNavigationItems.TryGetValue(open.Manifest.Id, out var pluginItem)
            ? pluginItem : selected == "settings" ? _navigationView.SettingsItem : _navigationItems.GetValueOrDefault(selected);
        foreach (var item in _pluginNavigationItems.Values)
        {
            var plugin = (InstalledPlugin)item.Tag;
            item.IsEnabled = PluginMode(plugin.Manifest.Id) != "disabled";
            string name = string.IsNullOrWhiteSpace(plugin.Manifest.Navigation?.Title) ? plugin.Manifest.Name : plugin.Manifest.Navigation.Title;
            ToolTipService.SetToolTip(item, item.IsEnabled ? name : name + "（已停用）");
        }
        foreach (var refresh in _pluginNavigationSwitchUpdates) refresh();
        string sceneName = CurrentScene?.Name ?? "未选择";
        _currentSceneStatus.Text = _preferences.SidebarCollapsed ? sceneName : "当前场景：" + sceneName;
        _currentSceneStatus.FontSize = _preferences.SidebarCollapsed ? 12 : 14;
        _currentSceneStatus.Foreground = Muted;
        _currentSceneStatus.TextAlignment = _preferences.SidebarCollapsed ? TextAlignment.Center : TextAlignment.Left;
        // A compact PaneFooter measures to its content rather than the icon rail.
        // Give this status the exact rail width so its center matches the icons.
        _currentSceneStatusHost.Width = width;
        UpdateSidebarDividers(width);
        _currentSceneStatusHost.HorizontalAlignment = HorizontalAlignment.Left;
        _currentSceneStatusHost.Padding = _preferences.SidebarCollapsed ? new(0) : new(16, 0, 16, 0);
        ToolTipService.SetToolTip(_currentSceneStatus, "当前场景：" + sceneName);
        AutomationProperties.SetName(_currentSceneStatus, "当前场景：" + sceneName);
    }

    private SceneProfile? CurrentScene => _profiles.FirstOrDefault(scene => scene.Id == (_automaticSceneId ?? _preferences.CurrentSceneId));
    private void SavePreferences() => _storage.Save("preferences.json", _preferences);

    private void RefreshShellColors()
    {
        _root.Background = _preferences.TranslucentBackground ? new SolidColorBrush(Colors.Transparent) : (_dark ? ColorBrush(32, 32, 32) : ColorBrush(243, 243, 243));
        var colors = new Windows.UI.ViewManagement.UISettings();
        _accentColor = colors.GetColorValue(_dark
            ? Windows.UI.ViewManagement.UIColorType.AccentLight2
            : Windows.UI.ViewManagement.UIColorType.AccentDark1);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = _dark ? Colors.White : Colors.Black;
        RefreshNavigation();
    }

    private void Navigate(string page)
    {
        _page = page;
        _openPlugin = null;
        _pluginSettingsOpen = false;
        _openDevice = null;
        _noticeBorder.Visibility = Visibility.Collapsed;
        RefreshNavigation();
        Render();
        if (page is "home" or "devices" && _devices.Count == 0) _ = RefreshDevicesAsync();
    }

    private void ClearPageViews()
    {
        _cancelPageFirstFrame?.Invoke();
        ReleasePluginPages();
        _content.Children.Clear();
        _content.RowDefinitions.Clear();
        _content.RowSpacing = 0;
        _content.ColumnDefinitions.Clear();
        _actions.Children.Clear();
        _titleControls.Children.Clear();
        _metricViews.Clear();
        _cpuClockCaption = null;
        _summaryCards.Clear();
        _pluginRowsHost = null;
        _pluginCards.Clear();
        _settingsBody = null;
        _settingsTabs = null;
        _settingsMutedViews.Clear();
        _settingsRowNotices.Clear();
        _diagnosticsHost = null; _diagnosticsPluginId = null; _diagnosticsSignature = "";
        ClearMonitoringViews();
        ClearDeviceViews();
        _sceneForeground = null;
        _homeDeviceHost = null; _diskListHost = null; _diskRows.Clear(); _diskSignature = "";
        _sceneTiles.Clear();
        _sceneGlows.Clear();
    }

    private void ApplyPageInsets()
    {
        bool fullContent = (_pluginSettingsOpen ? _openPlugin?.Manifest.SettingsPage : _openPlugin?.Manifest.Page)?.FullContent == true;
        double inset = ShellLayout.PageInsetForWidth(_main.ActualWidth);
        var padding = fullContent ? new Thickness(0) : new(inset, ShellLayout.RowGap, inset, ShellLayout.SectionGap);
        if (!_main.Padding.Equals(padding)) _main.Padding = padding;
    }

    private void Render()
    {
        if (_closing) return;
        if (_hiddenToTray) { _hiddenPageDirty = true; return; }
        _pageRenderStarted = Stopwatch.GetTimestamp();
        _captureSceneCreation?.Invoke();
        _captureSceneCreation = null;
        PreparePageEntrance();
        ClearPageViews();
        QueueActivation();
        bool fullContent = (_pluginSettingsOpen ? _openPlugin?.Manifest.SettingsPage : _openPlugin?.Manifest.Page)?.FullContent == true;
        _heading.Visibility = fullContent || _page == "home" && _openPlugin is null && _openDevice is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyPageInsets();
        if (fullContent) _noticeBorder.Visibility = Visibility.Collapsed;
        if (_openPlugin is not null) { RenderPlugin(_openPlugin); return; }
        if (_openDevice is not null) { RenderDeviceDetail(_openDevice); return; }
        switch (_page)
        {
            case "home": RenderHome(); break;
            case "system": RenderSystem(); break;
            case "devices": RenderDevices(); break;
            case "plugins": RenderPlugins(); break;
            case "scenes": RenderScenes(); break;
            case "sceneDetail": RenderSceneDetail(); break;
            case "sceneCreate": RenderSceneCreation(); break;
            case "rules": RenderRules(); break;
            case "settings": RenderSettings(); break;
        }
    }

    private UIElement Empty(string glyph, string label, Button? button = null)
    {
        var panel = new StackPanel { Spacing = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Placeholder(glyph));
        panel.Children.Add(Text(label, BodyTextSize));
        if (button is not null) { button.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(button); }
        return Card(panel);
    }

    private Grid SectionHeading(string name, string page, UIElement? right = null)
    {
        var heading = new Grid { MinHeight = ShellLayout.HeadingHeight, Margin = new(0, 0, 0, ShellLayout.HeadingGap) };
        heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new Button { Content = Text(name, SectionTextSize, true), MinHeight = ShellLayout.HeadingHeight, Padding = new(0), BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Left };
        title.Click += (_, _) => Navigate(page);
        ToolTipService.SetToolTip(title, $"查看{name}");
        heading.Children.Add(title);
        if (right is not null) { Grid.SetColumn((FrameworkElement)right, 1); heading.Children.Add(right); }
        return heading;
    }

    private static ScrollViewer Scroll(UIElement child) => new()
    {
        Content = child, AllowDrop = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollMode.Enabled
    };

    private void ShowError(string message)
    {
        _noticeBorder.BorderBrush = ErrorBrush;
        _notice.Text = "⚠  " + message;
        ToolTipService.SetToolTip(_noticeBorder, message);
        _noticeBorder.Visibility = Visibility.Visible;
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or System.Net.Http.HttpRequestException or OperationCanceledException)
        { ShowError(ex.Message); }
    }

    private bool _runtimeRefreshQueued;
    private readonly DispatcherTimer _runtimeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void RuntimeChanged()
    {
        if (_closing || _runtimeRefreshQueued) return;
        _runtimeRefreshQueued = true;
        _runtimeRefreshTimer.Start();
    }

    private void OnRuntimeRefresh(object? sender, object args)
    {
        if (_runtimeRefreshQueued) RefreshRuntimeNow();
    }

    private void RuntimeFirstSnapshotReceived(string _)
    {
        if (_closing || _hiddenToTray || _selectorOpen) return;
        RefreshRuntimeNow();
    }

    private void RefreshRuntimeNow()
    {
        _runtimeRefreshTimer.Stop();
        _runtimeRefreshQueued = false;
        FlushRuntimeChanges();
    }

    private void FlushRuntimeChanges()
    {
        if (_closing || _selectorOpen) return;
        RememberMonitorFrame();
        UpdateTrayBatteries();
        if (_installed.Plugins.Any(plugin => plugin.Manifest.SupportsBackgroundActivity
            && _runtime.IsRunning(plugin.Manifest.Id) && !PluginDemand(plugin)))
            QueueActivation();
        if (_hiddenToTray) return;
        foreach (var refresh in _pluginNavigationSwitchUpdates) refresh();
        UpdateMetricViews();
        UpdateMonitoringData();
        UpdateDeviceViews();
        string state = string.Join('|', _installed.Plugins.Select(plugin =>
            $"{plugin.Manifest.Id}:{_runtime.IsRunning(plugin.Manifest.Id)}:{_runtime.Error(plugin.Manifest.Id)}"));
        if (state == _runtimeState) return;
        _runtimeState = state;
        if (_openPlugin is not null) return;
        if (_page == "plugins") UpdatePluginCardStates();
        // Telemetry and process-state changes update existing controls. They must
        // not tear down an open popup, its owner, or the current page.
    }
}
