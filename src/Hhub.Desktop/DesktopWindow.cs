using System.Runtime.InteropServices;
using Hhub.Core;
using Hhub.Desktop.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace Hhub.Desktop;

internal sealed partial class DesktopWindow : Window, ISettingsHost
{
    private const uint RememberPage = 0x8004;
    private const uint ExitWithDesktop = 0x8005;
    private static string TrayWindowClass => HostInstallation.TrayWindowClass;
    private const string TrayExitProperty = "Hhub.Rebuild.Tray.Exiting";
    private const string DesktopProperty = "Hhub.Rebuild.Desktop";
    private const double SidebarLeftInset = 6;
    private const double SidebarRightInset = 4;
    private const double SidebarIconSlot = 36;
    private const double SidebarNavigationIconSize = 18;
    private const double SidebarLogoSize = 28;
    private const double SidebarLabelGap = 6;
    private const double SidebarExpandedWidth = 204;
    private const double SidebarMinimumWidth = 160;
    private const double SidebarMaximizedWidth = 280;
    private const double SidebarExpandedWidthRatio = 0.20;
    private const double SidebarRowHeight = 40;
    private const double SidebarCompactWidth = 49;
    private const double SidebarLabelOffset = SidebarLeftInset + SidebarIconSlot + SidebarLabelGap;
    private readonly Grid _layout = new();
    private readonly bool _previewPlugins;
    private readonly PluginLab? _pluginLab;
    private PluginPreview? _pluginPreview;
    private readonly ScrollViewer _viewport = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly ColumnDefinition _sidebarColumn = new() { Width = new GridLength(SidebarExpandedWidth) };
    private readonly List<TextBlock> _navigationLabels = [];
    private readonly TextBlock _brand = new() { Text = "HHub", VerticalAlignment = VerticalAlignment.Center };
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hhub-Rebuild", "theme.txt");
    private bool _closing;
    private bool _sidebarCollapsed;
    private bool _minimizingToTray;
    private bool _closeToTray = true;
    private bool _minimizeToTray = true;
    private bool _efficiencyInTray = true;
    private bool _translucentBackground = true;
    private bool _automaticScenes = true;
    private Button? _sidebarToggle;
    private IReadOnlyList<ScenePreview> _scenePreviews = [];
    private readonly TextBlock _currentScene = new()
    {
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };
    private string _currentSceneName = SceneCatalog.DefaultScene;
    private readonly SceneCatalog _scenes = new();
    private readonly ForegroundScenes _foregroundScenes;
    private readonly SceneDeviceConfigurations _sceneConfigurations;
    private CancellationTokenSource? _sceneActivation;
    private string? _sceneStatus;
    private ScenesPage? _sceneReturnPage;
    private readonly PeripheralInventory _inventory;
    private readonly HostUpdates _updates = new();
    private readonly Action _previewUpdateNotice;
    private int _sceneRevision;

    public bool ExitWithWindow => !_minimizingToTray && !_closeToTray;

    public DesktopWindow(bool previewPlugins, PluginLab? pluginLab, int? restoredPage)
    {
        _previewPlugins = previewPlugins;
        _pluginLab = pluginLab;
        _foregroundScenes = new(SetCurrentScene);
        _sceneConfigurations = new(pluginLab);
        _inventory = new(pluginLab);
        Title = "HHub";
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetProp(windowHandle, DesktopProperty, 1);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hhub.ico"));
        uint dpi = GetDpiForWindow(windowHandle);
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new SizeInt32(
            Math.Min((int)Math.Round(1120 * scale), workArea.Width),
            Math.Min((int)Math.Round(760 * scale), workArea.Height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(640 * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(520 * scale);
        }
        _layout.RequestedTheme = ReadTheme();
        try
        {
            var preferences = PluginPreferences.Load();
            _closeToTray = preferences.CloseToTray;
            _minimizeToTray = preferences.MinimizeToTray;
            _efficiencyInTray = preferences.EfficiencyInTray;
            _translucentBackground = preferences.TranslucentBackground;
            _sidebarCollapsed = preferences.SidebarCollapsed;
            _automaticScenes = preferences.AutomaticScenes;
            Program.SyncPreferences(preferences);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidDataException) { System.Diagnostics.Trace.WriteLine(error); }
        ApplyEfficiencyMode();
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        _layout.RowDefinitions.Add(new RowDefinition());
        _layout.ColumnDefinitions.Add(_sidebarColumn);
        _layout.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_viewport, 1);
        Grid.SetRow(_viewport, 1);
        _layout.Children.Add(_viewport);
        _viewport.SizeChanged += (_, _) => UpdateViewport();

        var navigation = NavigationList("页面");
        navigation.Items.Add(NavigationItem("首页", "\uE770"));
        navigation.Items.Add(NavigationItem("场景", "\uE7B8"));
        navigation.Items.Add(NavigationItem("插件", "\uE71D"));
        var shortcuts = NavigationList("插件快捷方式");
        shortcuts.Visibility = Visibility.Collapsed;
        ScrollViewer.SetVerticalScrollMode(shortcuts, ScrollMode.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(shortcuts, ScrollBarVisibility.Auto);
        var settings = NavigationList("设置");
        var settingsItem = NavigationItem("设置", "\uE713");
        settings.Items.Add(settingsItem);
        var updateIcon = new FontIcon
        {
            Glyph = "\uE896", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(29, 0, 0, 15),
            IsHitTestVisible = false, Visibility = Visibility.Collapsed
        };
        ((Grid)settingsItem.Content).Children.Add(updateIcon);
        var readNotes = new Button { Content = "查看更新日志" };
        var updateNotice = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsClosable = true, ActionButton = readNotes,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 680, Margin = new Thickness(16, 4, 16, 0)
        };
        Grid.SetColumn(updateNotice, 1);
        Grid.SetRow(updateNotice, 1);
        _layout.Children.Add(updateNotice);
        bool previewNotice = false;
        _previewUpdateNotice = () =>
        {
            previewNotice = _updates.Offer is null;
            updateNotice.Title = previewNotice ? $"预览 · HHub {HostReleaseFeed.CurrentVersion} 可用"
                : $"HHub {_updates.Offer!.Tag.TrimStart('v')} 可用";
            updateNotice.IsOpen = true;
        };
        readNotes.Click += async (_, _) =>
        {
            updateNotice.IsOpen = false;
            if (previewNotice && _layout.XamlRoot is { } root)
            {
                await SettingsPage.ShowCurrentNotesAsync(root);
                return;
            }
            if (settings.SelectedIndex == 0) ShowPage(3, showUpdates: true);
            else settings.SelectedIndex = 0;
        };
        string? shownVersion = null;
        void ShowUpdateIndicator()
        {
            bool available = _updates.Offer is not null;
            updateIcon.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            string label = available ? "设置，有可用更新" : "设置";
            ToolTipService.SetToolTip(settingsItem, label);
            AutomationProperties.SetName(settingsItem, label);
            if (_updates.Offer is not { } offer)
            {
                updateNotice.IsOpen = false;
                return;
            }
            if (shownVersion == offer.Tag) return;
            shownVersion = offer.Tag;
            previewNotice = false;
            updateNotice.Title = $"HHub {offer.Tag.TrimStart('v')} 可用";
            updateNotice.IsOpen = true;
        }
        _updates.Changed += ShowUpdateIndicator;
        var sidebar = new Grid { Margin = new Thickness(SidebarLeftInset, 0, SidebarRightInset, PageLayout.Inset.Bottom / 2) };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition());
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _currentScene.Style = (Style)Application.Current.Resources["DashboardDetailStyle"];
        _currentScene.Margin = new Thickness(8, 0, 8, 0);
        _currentScene.MinHeight = 24;
        SetCurrentScene(SceneCatalog.DefaultScene);
        Grid.SetRow(navigation, 0);
        sidebar.Children.Add(navigation);
        Grid.SetRow(shortcuts, 1);
        sidebar.Children.Add(shortcuts);
        Grid.SetRow(_currentScene, 2);
        sidebar.Children.Add(_currentScene);
        Grid.SetRow(settings, 3);
        sidebar.Children.Add(settings);
        var divider = (Style)Application.Current.Resources["SidebarDividerStyle"];
        sidebar.Children.Add(new Border { Style = divider, VerticalAlignment = VerticalAlignment.Bottom });
        var settingsDivider = new Border { Style = divider, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(settingsDivider, 3);
        sidebar.Children.Add(settingsDivider);
        Grid.SetRow(sidebar, 1);
        _layout.Children.Add(sidebar);

        navigation.SelectionChanged += (_, _) =>
        {
            int index = navigation.SelectedIndex;
            if (index < 0) return;
            shortcuts.SelectedIndex = -1;
            settings.SelectedIndex = -1;
            ShowPage(index);
        };
        shortcuts.SelectionChanged += (_, _) =>
        {
            int index = shortcuts.SelectedIndex;
            if (index < 0) return;
            navigation.SelectedIndex = -1;
            settings.SelectedIndex = -1;
            if (shortcuts.Items[index] is ListBoxItem { Tag: string pluginId }) ShowPluginPage(pluginId);
        };
        settings.SelectionChanged += (_, _) =>
        {
            if (settings.SelectedIndex < 0) return;
            navigation.SelectedIndex = -1;
            shortcuts.SelectedIndex = -1;
            ShowPage(3, showUpdates: _updates.Offer is not null);
        };

        ExtendsContentIntoTitleBar = true;
        var dragRegion = AddTitleBar();
        Content = _layout;
        EnablePluginDrop();
        ApplyBackdrop();
        SetTitleBar(dragRegion);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        ApplyCaptionTheme();
        int initialPage = restoredPage ?? (previewPlugins ? 2 : 0);
        if (initialPage == 3) settings.SelectedIndex = 0;
        else navigation.SelectedIndex = initialPage;
        SetSidebarCollapsed(_sidebarCollapsed, save: false);
        _foregroundScenes.SetEnabled(_automaticScenes);
        _ = LoadSceneRulesAsync();
        _ = LoadPluginShortcutsAsync(shortcuts);
        _ = CheckUpdatesOnLaunchAsync();

        AppWindow.Changed += (_, args) =>
        {
            if (_closing) return;
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                if (!_minimizeToTray) { _minimizingToTray = false; return; }
                _minimizingToTray = true;
                if (TrayAvailable())
                {
                    _closing = true;
                    DispatcherQueue.TryEnqueue(Close);
                    return;
                }
                _closing = true;
                DispatcherQueue.TryEnqueue(Close);
            }
            else if (args.DidPresenterChange || args.DidSizeChange)
            {
                _minimizingToTray = false;
                UpdateViewport();
            }
        };
        AppWindow.Closing += (_, args) =>
        {
            if (_closing || !_closeToTray || !TrayAvailable()) return;
            args.Cancel = true;
            _closing = true;
            DispatcherQueue.TryEnqueue(Close);
        };
        Closed += (_, _) =>
        {
            _closing = true;
            _foregroundScenes.Dispose();
            _sceneActivation?.Cancel();
            RemoveCurrentPage();
            _inventory.Dispose();
            _updates.Dispose();
        };
    }

    private static bool TrayAvailable()
    {
        nint tray = FindWindow(TrayWindowClass, null);
        return tray != 0 && GetProp(tray, TrayExitProperty) == 0;
    }

    private void ApplyEfficiencyMode()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            ProcessEfficiency.SetWindowsMode(process, _efficiencyInTray);
        }
        catch (System.ComponentModel.Win32Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        _pluginLab?.SetEfficiencyMode(_efficiencyInTray);
    }

    private void ApplyBackdrop()
    {
        SystemBackdrop = _translucentBackground ? new MicaBackdrop() : null;
        _layout.Background = _translucentBackground
            ? new SolidColorBrush(Colors.Transparent)
            : (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
    }

    private void SetTranslucentBackground(bool translucent)
    {
        _translucentBackground = translucent;
        ApplyBackdrop();
    }

    private void SetAutomaticScenes(bool enabled)
    {
        _automaticScenes = enabled;
        _foregroundScenes.SetEnabled(enabled);
    }

    private void SetMinimizeToTray(bool enabled) => _minimizeToTray = enabled;

    void ISettingsHost.SaveTheme(ElementTheme theme) => SetTheme(theme);
    void ISettingsHost.ChangeCloseBehavior(bool toTray) => _closeToTray = toTray;
    void ISettingsHost.SetTranslucentBackground(bool translucent) => SetTranslucentBackground(translucent);
    void ISettingsHost.SetSidebarCollapsed(bool collapsed) => SetSidebarCollapsed(collapsed, save: false);
    void ISettingsHost.SetAutomaticScenes(bool enabled) => SetAutomaticScenes(enabled);
    void ISettingsHost.SetMinimizeToTray(bool enabled) => SetMinimizeToTray(enabled);
    void ISettingsHost.SetEfficiencyInTray(bool enabled) => SetEfficiencyInTray(enabled);

    void ISettingsHost.OpenPluginDirectory()
    {
        string directory = _pluginLab is { } lab
            ? Path.Combine(lab.Profile, "plugins")
            : Path.Combine(PluginPreferences.Root, "plugins");
        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                "\"" + directory + "\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
        }
    }

    private void SetEfficiencyInTray(bool enabled)
    {
        _efficiencyInTray = enabled;
        ApplyEfficiencyMode();
        TrayBattery.RefreshEfficiency();
    }

    private void SetSidebarCollapsed(bool collapsed, bool save)
    {
        _sidebarCollapsed = collapsed;
        var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _brand.Visibility = visibility;
        foreach (var label in _navigationLabels) label.Visibility = visibility;
        _currentScene.TextAlignment = collapsed ? TextAlignment.Center : TextAlignment.Left;
        _currentScene.Margin = collapsed ? new Thickness(0) : new Thickness(8, 0, 8, 0);
        UpdateSceneLabel();
        if (_sidebarToggle is { } toggle) SetToggleLabel(toggle);
        UpdateViewport();
        if (!save) return;
        try
        {
            var preferences = PluginPreferences.Load();
            PluginPreferences.Save(preferences with { SidebarCollapsed = collapsed });
        }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    private Grid AddTitleBar()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarLabelOffset) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var toggle = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = SidebarIconSlot,
            Height = SidebarIconSlot,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(SidebarLeftInset, 0, 0, 0),
            Padding = new Thickness(0),
            MinWidth = 0,
            Content = new Image
            {
                Width = SidebarLogoSize,
                Height = SidebarLogoSize,
                IsHitTestVisible = false,
                Source = new BitmapImage
                {
                    DecodePixelWidth = 96,
                    UriSource = new Uri("ms-appx:///Assets/Hhub.png")
                }
            }
        };
        _sidebarToggle = toggle;
        SetToggleLabel(toggle);
        toggle.Click += (_, _) => SetSidebarCollapsed(!_sidebarCollapsed, save: true);
        header.Children.Add(toggle);
        var drag = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        drag.Children.Add(_brand);
        Grid.SetColumn(drag, 1);
        header.Children.Add(drag);
        Grid.SetColumnSpan(header, 2);
        _layout.Children.Add(header);
        return drag;
    }

    private void SetToggleLabel(Button toggle)
    {
        var label = _sidebarCollapsed ? "展开侧栏" : "收起侧栏";
        AutomationProperties.SetName(toggle, label);
    }

    private static ListBox NavigationList(string name)
    {
        var list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0)
        };
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
        AutomationProperties.SetName(list, name);
        return list;
    }

    private ListBoxItem NavigationItem(string name, string glyph, string? pathData = null)
    {
        var label = new TextBlock
        {
            Text = name, TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible
        };
        var row = new Grid { ColumnSpacing = SidebarLabelGap };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarIconSlot) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(label, 1);
        _navigationLabels.Add(label);
        row.Children.Add(NavigationIcon(pathData, glyph));
        row.Children.Add(label);
        var item = new ListBoxItem
        {
            Content = row,
            Height = SidebarRowHeight,
            Style = (Style)Application.Current.Resources["SidebarItemStyle"]
        };
        AutomationProperties.SetName(item, name);
        ToolTipService.SetToolTip(item, name);
        return item;
    }

    private static Grid NavigationIcon(string? pathData, string glyph)
    {
        var slot = new Grid { Width = SidebarIconSlot, Height = 32 };
        if (!string.IsNullOrWhiteSpace(pathData))
        {
            try
            {
                slot.Children.Add(new PathIcon
                {
                    Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), pathData),
                    Width = SidebarNavigationIconSize,
                    Height = SidebarNavigationIconSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
                return slot;
            }
            catch (Exception error) when (error is ArgumentException or System.Runtime.InteropServices.COMException)
            { /* Invalid optional artwork does not prevent access to the plugin. */ }
        }
        slot.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = SidebarNavigationIconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return slot;
    }

    private async Task LoadPluginShortcutsAsync(ListBox shortcuts)
    {
        if (_pluginLab is null) return;
        try
        {
            var (_, plugins) = await _pluginLab.DiscoverManifestsAsync();
            var ordered = await Task.Run(() => plugins.Where(plugin => plugin.Definition.Navigation is not null)
                .OrderBy(plugin => DevicePluginSettings.ReadShortcutOrder(plugin.Directory)).ToArray());
            if (_closing) return;
            foreach (var plugin in ordered)
            {
                if (plugin.Definition.Navigation is not { } navigation) continue;
                string name = string.IsNullOrWhiteSpace(navigation.Title) ? plugin.Definition.Name : navigation.Title!;
                string glyph = string.IsNullOrWhiteSpace(navigation.Glyph) ? "\uEA86" : navigation.Glyph!;
                var item = NavigationItem(name, glyph, pathData: navigation.PathData);
                item.Tag = plugin.Definition.Id;
                shortcuts.Items.Add(item);
            }
            shortcuts.Visibility = shortcuts.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine(error);
        }
    }

    private void ShowPluginPage(string pluginId)
    {
        if (_closing || _pluginLab is null) return;
        nint tray = FindWindow(TrayWindowClass, null);
        if (tray != 0) PostMessage(tray, RememberPage, (nuint)Environment.ProcessId, 2);
        RemoveCurrentPage();
        _viewport.Content = new PluginsPage(null, _viewport, _pluginLab, openPluginId: pluginId, inventory: _inventory);
        FinishNavigation();
    }

    private void ApplyCaptionTheme()
    {
        AppWindow.TitleBar.PreferredTheme = _layout.RequestedTheme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode
        };
    }

    private void ShowPage(int index, int direction = 0, bool showUpdates = false)
    {
        if (_closing) return;
        nint tray = FindWindow(TrayWindowClass, null);
        if (tray != 0) PostMessage(tray, RememberPage, (nuint)Environment.ProcessId, index);
        RemoveCurrentPage();
        _viewport.Content = index switch
        {
            0 => new SystemPage(_pluginLab, _inventory, () => ShowMonitorDetails(), ShowDevicePlugin, ApplyDeviceScene, ShowDevicesPage),
            1 => new ScenesPage(() => _viewport.ChangeView(null, 0, null, disableAnimation: true), _inventory,
                _scenes, UpdateSceneRules, _foregroundScenes, _sceneConfigurations, ShowDevicePlugin,
                windowHandle: WinRT.Interop.WindowNative.GetWindowHandle(this)),
            2 => new PluginsPage(_previewPlugins ? _pluginPreview ??= new PluginPreview() : null, _viewport, _pluginLab,
                openMonitor: provider => ShowMonitorDetails(provider), inventory: _inventory),
            3 => new SettingsPage(_layout.RequestedTheme, this, _updates, _pluginLab, _previewUpdateNotice, showUpdates),
            _ => null
        };
        if (_viewport.Content is ScenesPage scenes) scenes.ShowActivationStatus(_sceneStatus);
        FinishNavigation(direction);
    }

    private void ShowDevicesPage()
    {
        if (_closing) return;
        RemoveCurrentPage();
        _viewport.Content = new DeviceManagementPage(_inventory, () => ShowPage(0, direction: -1));
        FinishNavigation(direction: 1);
    }

    private void ShowMonitorDetails(string? provider = null)
    {
        int parent = provider is null ? 0 : 2;
        if (_closing || _pluginLab is null || (parent == 0 ? _viewport.Content is not SystemPage : _viewport.Content is not PluginsPage))
            return;
        RemoveCurrentPage();
        _viewport.Content = new MonitorDetailsPage(_pluginLab, () => ShowPage(parent, direction: -1), provider);
        FinishNavigation(direction: 1);
    }

    private void ShowDevicePlugin(Hhub.PluginContracts.ConnectedPeripheral device)
    {
        if (_closing || _pluginLab is null || _viewport.Content is not (SystemPage or ScenesPage)) return;
        var scenes = _viewport.Content as ScenesPage;
        double offset = _viewport.VerticalOffset;
        if (scenes is null) RemoveCurrentPage();
        else
        {
            _sceneReturnPage = scenes;
            _viewport.Content = null;
        }
        void Return()
        {
            if (scenes is null)
            {
                ShowPage(0, direction: -1);
                return;
            }
            _sceneReturnPage = null;
            RemoveCurrentPage();
            _viewport.Content = scenes;
            UpdateViewport();
            _viewport.UpdateLayout();
            _viewport.ChangeView(null, offset, null, disableAnimation: true);
            scenes.ShowActivationStatus(_sceneStatus);
            scenes.RefreshDeviceConfigurations(device.Id);
            PageLayout.AnimateEntry(scenes, direction: -1);
        }
        _viewport.Content = new PluginsPage(null, _viewport, _pluginLab, device, Return, inventory: _inventory,
            deviceParent: scenes is null ? "首页" : "场景");
        FinishNavigation(direction: 1);
    }

    private void FinishNavigation(int direction = 0)
    {
        UpdateViewport();
        _viewport.ChangeView(null, 0, null, disableAnimation: true);
        if (_viewport.IsLoaded && _viewport.Content is UIElement page) PageLayout.AnimateEntry(page, direction);
    }

    private async Task CheckUpdatesOnLaunchAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (_closing || _updates.Checking) return;
            var preferences = PluginPreferences.Load();
            if (preferences.AutomaticUpdates) await _updates.CheckAsync(preferences.UpdateChannel);
        }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    private void RemoveCurrentPage()
    {
        _sceneReturnPage?.ClosePage();
        _sceneReturnPage = null;
        if (_viewport.Content is PluginsPage plugins) plugins.ClosePage();
        if (_viewport.Content is ScenesPage scenes) scenes.ClosePage();
        if (_viewport.Content is SystemPage home) home.ClosePage();
        if (_viewport.Content is MonitorDetailsPage monitor) monitor.ClosePage();
        if (_viewport.Content is SettingsPage settings) settings.ClosePage();
        if (_viewport.Content is DeviceManagementPage devices) devices.ClosePage();
        _viewport.Content = null;
    }

    private void UpdateViewport()
    {
        if (_closing) return;
        var presenter = AppWindow.Presenter;
        bool maximized = presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized }
            || presenter.Kind == AppWindowPresenterKind.FullScreen;
        double limit = maximized ? SidebarMaximizedWidth : SidebarExpandedWidth;
        double expanded = _layout.ActualWidth > 0
            ? Math.Clamp(Math.Round(_layout.ActualWidth * SidebarExpandedWidthRatio), SidebarMinimumWidth, limit) : SidebarExpandedWidth;
        double sidebarWidth = _sidebarCollapsed ? SidebarCompactWidth : expanded;
        if (_sidebarColumn.Width.Value != sidebarWidth)
            _sidebarColumn.Width = new GridLength(sidebarWidth);

        if (_viewport.Content is ScenesPage scenes) scenes.ResizeToViewport(_viewport.ActualWidth);
        if (_viewport.Content is PluginsPage plugins) plugins.ResizeToViewport(_viewport.ActualWidth);
        if (_viewport.Content is SettingsPage settings) settings.ResizeToViewport(_viewport.ActualWidth);
        if (_viewport.Content is MonitorDetailsPage monitor) monitor.ResizeToViewport(_viewport.ActualWidth);
        if (_viewport.Content is DeviceManagementPage devices) devices.ResizeToViewport(_viewport.ActualWidth);
        if (_viewport.Content is SystemPage page) page.ResizeToViewport(_viewport.ActualWidth, _viewport.ActualHeight);
    }

    private ElementTheme ReadTheme()
    {
        try
        {
            if (File.Exists(_settingsPath) && Enum.TryParse(File.ReadAllText(_settingsPath), out ElementTheme theme)
                && Enum.IsDefined(theme))
                return theme;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return ElementTheme.Default;
    }

    private async Task LoadSceneRulesAsync()
    {
        int revision = _sceneRevision;
        try
        {
            var scenes = await _scenes.LoadAsync(CancellationToken.None);
            if (!_closing && revision == _sceneRevision) UpdateSceneRules(scenes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidDataException) { System.Diagnostics.Trace.WriteLine(error); }
    }

    private void UpdateSceneRules(IReadOnlyList<ScenePreview> scenes, string? select = null)
    {
        _sceneRevision++;
        _scenePreviews = scenes.ToArray();
        _foregroundScenes.Update(_scenePreviews, select ?? _scenes.ReadCurrent(), selectPreferred: select is not null);
    }

    private void ApplyDeviceScene(Hhub.PluginContracts.ConnectedPeripheral device)
    {
        bool enabled;
        try { enabled = PluginPreferences.Load().SwitchSceneOnDeviceSelect; }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
            return;
        }
        if (!enabled) return;
        var scene = _scenePreviews.FirstOrDefault(item => item.DeviceConfigurations?.ContainsKey(device.Id) == true
            || item.Bindings.ContainsKey(device.Id));
        if (scene is null || scene.Name == _currentSceneName) return;
        try
        {
            _scenes.SetCurrent(scene.Name);
            _foregroundScenes.Select(scene.Name);
        }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    private void SetCurrentScene(string? name)
    {
        string scene = name ?? SceneCatalog.DefaultScene;
        _currentSceneName = scene;
        UpdateSceneLabel();
        _sceneActivation?.Cancel();
        _sceneActivation = null;
        var configurations = _scenePreviews.FirstOrDefault(item => item.Name == scene)?.DeviceConfigurations;
        if (configurations is null || configurations.Count == 0)
        {
            SetSceneStatus(null);
            return;
        }
        var activation = new CancellationTokenSource();
        _sceneActivation = activation;
        _ = ApplySceneConfigurationsAsync(scene, configurations, activation);
    }

    private async Task ApplySceneConfigurationsAsync(string scene,
        IReadOnlyDictionary<string, SceneDeviceBinding> configurations, CancellationTokenSource activation)
    {
        using (activation)
        {
            CancellationToken token = activation.Token;
            SetSceneStatus($"正在应用「{scene}」的设备配置…");
            var failed = new List<string>();
            try
            {
                foreach (var (target, binding) in configurations)
                {
                    token.ThrowIfCancellationRequested();
                    try { await _sceneConfigurations.ApplyAsync(target, binding, token); }
                    catch (Exception error) when (SceneDeviceConfigurations.IsDeviceError(error))
                    {
                        System.Diagnostics.Trace.WriteLine(error);
                        failed.Add(binding.DeviceName);
                    }
                }
                token.ThrowIfCancellationRequested();
                SetSceneStatus(failed.Count == 0 ? $"已应用「{scene}」的设备配置"
                    : $"「{scene}」未能应用以下设备的配置：{string.Join("、", failed)}。确认设备已连接并唤醒后，可重新应用。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { if (ReferenceEquals(_sceneActivation, activation)) _sceneActivation = null; }
        }
    }

    private void SetSceneStatus(string? message)
    {
        _sceneStatus = message;
        UpdateSceneLabel();
        if (_viewport.Content is ScenesPage page) page.ShowActivationStatus(message);
    }

    private void UpdateSceneLabel()
    {
        string description = $"当前场景 {_currentSceneName}";
        _currentScene.Text = _sidebarCollapsed ? _currentSceneName : description;
        AutomationProperties.SetName(_currentScene, description);
        ToolTipService.SetToolTip(_currentScene, _sceneStatus is null ? description : $"{description}\n{_sceneStatus}");
    }

    internal static void RequestTrayExit()
    {
        nint tray = FindWindow(TrayWindowClass, null);
        if (tray != 0) PostMessage(tray, ExitWithDesktop, (nuint)Environment.ProcessId, 0);
    }

    private void SetTheme(ElementTheme theme)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        string temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, theme.ToString());
        File.Move(temporaryPath, _settingsPath, overwrite: true);
        _layout.RequestedTheme = theme;
        ApplyCaptionTheme();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(nint window, string name, nint value);

    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

}
