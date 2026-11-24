using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Hhub.Desktop.Pages;

internal interface ISettingsHost
{
    void SaveTheme(ElementTheme theme);
    void ChangeCloseBehavior(bool toTray);
    void SetTranslucentBackground(bool translucent);
    void SetSidebarCollapsed(bool collapsed);
    void SetAutomaticScenes(bool enabled);
    void SetMinimizeToTray(bool enabled);
    void SetEfficiencyInTray(bool enabled);
    void OpenPluginDirectory();
}

internal sealed class SettingsPage : StackPanel
{
    private const string SponsorUrl = "https://afdian.com/a/Hhub123?utm_source=copylink&utm_medium=link";
    private const double ChoiceWidth = 150;
    private readonly StackPanel _content = new() { Spacing = PageLayout.SettingsRowGap, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleButton _system = PageLayout.SecondaryTab("常规");
    private readonly ToggleButton _plugins = PageLayout.SecondaryTab("插件");
    private readonly ToggleButton _scenes = PageLayout.SecondaryTab("场景");
    private readonly ToggleButton _about = PageLayout.SecondaryTab("关于");
    private readonly ISettingsHost _host;
    private readonly ElementTheme _theme;
    private readonly ToggleButton[] _tabs;
    private int _selected = -1;
    private UserPreferences _preferences;
    private readonly HostUpdates _hostUpdates;
    private readonly PluginLab? _pluginLab;
    private Action? _refreshUpdate;
    private readonly Action _previewUpdateNotice;

    public SettingsPage(ElementTheme current, ISettingsHost host, HostUpdates hostUpdates, PluginLab? pluginLab, Action previewUpdateNotice, bool showUpdates = false)
    {
        _theme = current;
        _host = host;
        _hostUpdates = hostUpdates;
        _pluginLab = pluginLab;
        _previewUpdateNotice = previewUpdateNotice;
        _tabs = [_system, _plugins, _scenes, _about];
        try { _preferences = PluginPreferences.Load(); }
        catch (Exception error) when (IsSettingsError(error)) { _preferences = new UserPreferences(); }
        Margin = PageLayout.Inset;
        Spacing = 5;
        HorizontalAlignment = HorizontalAlignment.Center;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(PageLayout.Heading("设置"));
        var sponsor = SponsorButton();
        Grid.SetColumn(sponsor, 1);
        header.Children.Add(sponsor);
        Children.Add(header);
        var categories = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AutomationProperties.SetName(categories, "设置分类");
        foreach (var tab in _tabs) categories.Children.Add(tab);
        for (int i = 0; i < _tabs.Length; i++)
        {
            int index = i;
            _tabs[i].Click += (_, _) => ShowTab(index);
        }
        Children.Add(new ScrollViewer { Content = categories, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled });
        Children.Add(_content);
        ShowTab(showUpdates ? 3 : 0);
    }

    private Button SponsorButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = "\uEB51", FontSize = 16 });
        content.Children.Add(new TextBlock { Text = "赞助", VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content, Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, "赞助");
        button.Click += (_, _) => ShowSponsor();
        return button;
    }

    public void ClosePage()
    {
        _selected = -1;
        DetachUpdatePage();
        _content.Children.Clear();
    }

    public void ResizeToViewport(double width)
    {
        if (width > 0)
        {
            double inset = PageLayout.WideInset(width);
            Margin = new Thickness(inset, 0, inset, PageLayout.Inset.Bottom);
            Width = Math.Max(0, width - 2 * inset);
        }
    }

    private void ShowTab(int index)
    {
        for (int i = 0; i < _tabs.Length; i++) _tabs[i].IsChecked = i == index;
        if (_selected == index) return;
        _selected = index;
        DetachUpdatePage();
        _content.Children.Clear();
        switch (index)
        {
            case 0: AddSystemSettings(); break;
            case 1: AddPluginSettings(); break;
            case 2: AddSceneSettings(); break;
            case 3: AddAboutSettings(); break;
        }
        if (_content.IsLoaded) PageLayout.AnimateEntry(_content);
    }

    private void AddSystemSettings()
    {
        var tray = Path.Combine(AppContext.BaseDirectory, "Hhub.Tray.exe");
        var startupError = ErrorText();
        bool autoStart = false;
        try { autoStart = File.Exists(tray) && PluginPreferences.IsStartupEnabled(tray, _preferences); }
        catch (Exception error) when (IsSettingsError(error)) { ShowError(startupError, "无法读取开机自启动设置。"); }

        ToggleSwitch? startToTrayToggle = null;
        ToggleSwitch? startupToggle = null;
        bool restoringStartup = false;
        startupToggle = Toggle(autoStart, enabled =>
        {
            if (restoringStartup) return;
            try
            {
                PluginPreferences.SetStartup(tray, _preferences, enabled);
                autoStart = enabled;
                startupError.Visibility = Visibility.Collapsed;
            }
            catch (Exception error) when (IsSettingsError(error))
            {
                restoringStartup = true;
                startupToggle!.IsOn = !enabled;
                restoringStartup = false;
                ShowError(startupError, "无法更改开机自启动。");
            }
        });
        startupToggle.IsEnabled = File.Exists(tray);
        _content.Children.Add(Row("开机自启动", startupToggle, startupError));

        var startToTrayError = ErrorText();
        startToTrayToggle = Toggle(_preferences.StartToTray, enabled =>
        {
            if (!restoringStartup && !SaveStartup(_preferences with { StartToTray = enabled }, startToTrayError))
            {
                restoringStartup = true;
                startToTrayToggle!.IsOn = !enabled;
                restoringStartup = false;
            }
        });
        var delayError = ErrorText();
        int[] delays = PluginPreferences.StartupDelays;
        var delay = new ComboBox
        {
            ItemsSource = delays.Select(seconds => $"{seconds} 秒").ToArray(),
            SelectedIndex = Array.IndexOf(delays, PluginPreferences.StartupDelay(_preferences))
        };
        delay.SelectionChanged += (_, _) =>
        {
            if (restoringStartup || delay.SelectedIndex < 0) return;
            if (SaveStartup(_preferences with { StartupDelaySeconds = delays[delay.SelectedIndex] }, delayError)) return;
            restoringStartup = true;
            delay.SelectedIndex = Array.IndexOf(delays, PluginPreferences.StartupDelay(_preferences));
            restoringStartup = false;
        };
        _content.Children.Add(Row("开机启动延迟", delay, delayError));
        _content.Children.Add(Row("启动后进入托盘", startToTrayToggle, startToTrayError));

        bool SaveStartup(UserPreferences updated, TextBlock errorText)
        {
            try
            {
                if (autoStart) PluginPreferences.SetStartup(tray, updated, true);
                if (Save(updated, errorText)) return true;
                if (autoStart) PluginPreferences.SetStartup(tray, _preferences, true);
            }
            catch (Exception error) when (IsSettingsError(error))
            {
                ShowError(errorText, "无法更新开机启动方式。");
            }
            return false;
        }

        var minimizeError = ErrorText();
        _content.Children.Add(Row("最小化到托盘", Toggle(_preferences.MinimizeToTray, enabled =>
        {
            if (!Save(_preferences with { MinimizeToTray = enabled }, minimizeError)) return;
            _host.SetMinimizeToTray(enabled);
        }), minimizeError));

        var closeError = ErrorText();
        var close = new ComboBox
        {
            ItemsSource = new[] { "收起到托盘", "退出应用" },
            SelectedIndex = _preferences.CloseToTray ? 0 : 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        bool restoringClose = false;
        close.SelectionChanged += (_, _) =>
        {
            if (restoringClose || close.SelectedIndex < 0) return;
            bool toTray = close.SelectedIndex == 0;
            if (!Save(_preferences with { CloseToTray = toTray }, closeError))
            {
                restoringClose = true;
                close.SelectedIndex = _preferences.CloseToTray ? 0 : 1;
                restoringClose = false;
                return;
            }
            _host.ChangeCloseBehavior(toTray);
        };
        _content.Children.Add(Row("关闭窗口", close, closeError));

        var efficiencyError = ErrorText();
        _content.Children.Add(Row("效率模式", Toggle(_preferences.EfficiencyInTray, enabled =>
        {
            try
            {
                PluginPreferences.SaveTrayEfficiency(enabled);
                if (Save(_preferences with { EfficiencyInTray = enabled }, efficiencyError)) _host.SetEfficiencyInTray(enabled);
                else PluginPreferences.SaveTrayEfficiency(_preferences.EfficiencyInTray);
            }
            catch (Exception error) when (IsSettingsError(error)) { ShowError(efficiencyError, "无法设置效率模式。"); }
        }), efficiencyError));

        var batteryError = ErrorText();
        _content.Children.Add(Row("托盘显示设备电量", Toggle(_preferences.TrayBattery, enabled =>
        {
            if (Save(_preferences with { TrayBattery = enabled }, batteryError)) TrayBattery.SetGlobalVisibility(enabled);
        }), batteryError));

        _content.Children.Add(PageLayout.SectionHeading("外观"));
        AddAppearanceSettings();
    }

    private void AddAppearanceSettings()
    {
        var themeError = ErrorText();
        var picker = new ComboBox
        {
            ItemsSource = new[] { "跟随系统", "浅色", "深色" },
            SelectedIndex = (int)_theme,
            VerticalAlignment = VerticalAlignment.Center
        };
        bool restoringTheme = false;
        picker.SelectionChanged += (_, _) =>
        {
            if (restoringTheme || picker.SelectedIndex < 0) return;
            var requested = (ElementTheme)picker.SelectedIndex;
            try
            {
                _host.SaveTheme(requested);
                themeError.Visibility = Visibility.Collapsed;
            }
            catch (Exception error) when (IsSettingsError(error))
            {
                restoringTheme = true;
                picker.SelectedIndex = (int)_theme;
                restoringTheme = false;
                ShowError(themeError, "无法保存应用主题。");
            }
        };
        _content.Children.Add(Row("主题", picker, themeError));

        var translucentError = ErrorText();
        _content.Children.Add(Row("透明背景", Toggle(_preferences.TranslucentBackground, enabled =>
        {
            if (!Save(_preferences with { TranslucentBackground = enabled }, translucentError)) return;
            _host.SetTranslucentBackground(enabled);
        }), translucentError));

        var sidebarError = ErrorText();
        _content.Children.Add(Row("默认收起侧边栏", Toggle(_preferences.SidebarCollapsed, enabled =>
        {
            if (!Save(_preferences with { SidebarCollapsed = enabled }, sidebarError)) return;
            _host.SetSidebarCollapsed(enabled);
        }), sidebarError));
    }

    private void AddSceneSettings()
    {
        var automaticError = ErrorText();
        _content.Children.Add(Row("自动切换场景", Toggle(_preferences.AutomaticScenes, enabled =>
        {
            if (!Save(_preferences with { AutomaticScenes = enabled }, automaticError)) return;
            _host.SetAutomaticScenes(enabled);
        }), automaticError));

        var deviceSceneError = ErrorText();
        _content.Children.Add(Row("设备选择后切换场景", Toggle(_preferences.SwitchSceneOnDeviceSelect, enabled =>
        {
            Save(_preferences with { SwitchSceneOnDeviceSelect = enabled }, deviceSceneError);
        }), deviceSceneError));
    }

    private void AddPluginSettings()
    {
        var error = ErrorText();
        var mode = new ComboBox
        {
            ItemsSource = new[] { "按需启动", "禁用" },
            SelectedIndex = PluginPolicies.UnifiedMode(_preferences.DefaultPluginMode) == "disabled" ? 1 : 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        bool restoringMode = false;
        mode.SelectionChanged += (_, _) =>
        {
            if (restoringMode || mode.SelectedIndex < 0) return;
            if (Save(_preferences with { DefaultPluginMode = mode.SelectedIndex == 0 ? "onDemand" : "disabled" }, error))
            {
                PluginActivation.SyncDefaults(_preferences);
                return;
            }
            restoringMode = true;
            mode.SelectedIndex = PluginPolicies.UnifiedMode(_preferences.DefaultPluginMode) == "disabled" ? 1 : 0;
            restoringMode = false;
        };
        _content.Children.Add(Row("默认启动方式", mode, error));
        _content.Children.Add(PageLayout.SectionHeading("插件防病毒"));
        var reports = PageLayout.AccentButton("打开");
        var reportError = ErrorText();
        reports.Click += (_, _) =>
        {
            try { PluginProtection.OpenReports(); }
            catch (Exception exception) when (IsSettingsError(exception) || exception is System.ComponentModel.Win32Exception)
            { ShowError(reportError, "无法打开防病毒报告目录。"); }
        };
        _content.Children.Add(Row("本地扫描报告", reports, reportError));

        _content.Children.Add(PageLayout.SectionHeading("库源"));
        var urls = new StackPanel { Spacing = PageLayout.SettingsRowGap, HorizontalAlignment = HorizontalAlignment.Stretch };
        var addInput = AddressInput("");
        var add = PageLayout.AccentButton("添加");
        add.Click += (_, _) =>
        {
            var result = PluginMarkets.EditAddress(PluginMarkets.Configured(_preferences), null, addInput.Text);
            if (!result.Success) { ShowError(error, result.Error!); return; }
            if (!Save(_preferences with { PluginMarketUrls = result.Addresses }, error)) return;
            urls.Children.Add(SourceRow(result.Addresses[^1], error, urls));
            addInput.Text = "";
        };

        foreach (string address in PluginMarkets.Configured(_preferences)) urls.Children.Add(SourceRow(address, error, urls));
        _content.Children.Add(urls);
        _content.Children.Add(SettingsRow(addInput, add));

        var directory = PageLayout.AccentButton("打开");
        directory.Click += (_, _) => _host.OpenPluginDirectory();
        _content.Children.Add(Row("插件目录", directory, null));

        var record = MarketRecord.Load();
        var recordText = Detail(record is null ? "尚未检查" :
            $"上次检查：{record.CheckedAt:yyyy-MM-dd HH:mm} · {record.Count} 个插件"
            + (record.Errors.Length > 0 ? $" · {record.Errors.Length} 个错误" : ""));
        var check = PageLayout.AccentButton("检查");
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            try
            {
                var result = await PluginMarkets.ReadAsync(PluginMarkets.Configured(_preferences), default, OfficialPluginLibrary.ReadAsync);
                var saved = new MarketRecord(DateTimeOffset.Now, result.Plugins.Length, result.Errors);
                saved.Save();
                recordText.Text = $"上次检查：{saved.CheckedAt:yyyy-MM-dd HH:mm} · {saved.Count} 个插件"
                    + (saved.Errors.Length > 0 ? $" · {saved.Errors.Length} 个错误" : "");
            }
            catch (Exception exception) when (IsSettingsError(exception) || exception is HttpRequestException)
            {
                recordText.Text = "检查失败：" + exception.Message;
            }
            finally { check.IsEnabled = true; }
        };
        _content.Children.Add(Row("市场检查", check, null));
        _content.Children.Add(recordText);
    }

    private void ShowSponsor()
    {
        _selected = -2;
        DetachUpdatePage();
        foreach (var tab in _tabs) tab.IsChecked = false;
        _content.Children.Clear();
        _content.Children.Add(PageLayout.SectionHeading("赞助 Hhub"));
        _content.Children.Add(Paragraph("如果 HHub 对你有帮助，可以在这里支持它的开发。"));

        var identity = new StackPanel { Spacing = 4 };
        identity.Children.Add(new TextBlock
        {
            Text = "shengerq", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });
        identity.Children.Add(Paragraph("项目的奠基人"));
        identity.Children.Add(Paragraph("创建了 HHub，决定了其采用的技术以及最初的功能。"));
        var donate = new Button
        {
            Content = "捐赠", MinWidth = 88, VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        AutomationProperties.SetName(donate, "捐赠给 shengerq");
        donate.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(SponsorUrl) { UseShellExecute = true });
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                System.Diagnostics.Trace.WriteLine(error);
            }
        };
        _content.Children.Add(SettingsRow(identity, donate));
        _content.Children.Add(Paragraph("捐赠不会解锁额外功能，请在自己能力范围内支持。"));
    }

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["DashboardDetailStyle"]
    };

    private void DetachUpdatePage()
    {
        if (_refreshUpdate is { } refresh) _hostUpdates.Changed -= refresh;
        _refreshUpdate = null;
    }

    private void AddUpdateSettings()
    {
        _content.Children.Add(PageLayout.SectionHeading("软件更新"));

        var updateError = ErrorText();
        var updateLabel = new StackPanel { Spacing = 2 };
        updateLabel.Children.Add(Value("自动检查更新"));
        updateLabel.Children.Add(Detail("每次打开主窗口约 3 秒后检查一次"));
        updateLabel.Children.Add(updateError);
        var automatic = Toggle(_preferences.AutomaticUpdates, enabled =>
        {
            if (Save(_preferences with { AutomaticUpdates = enabled }, updateError) && enabled)
                _ = _hostUpdates.CheckAsync(_preferences.UpdateChannel);
        });
        AutomationProperties.SetName(automatic, "自动检查更新");
        _content.Children.Add(SettingsRow(updateLabel, automatic));

        var channelError = ErrorText();
        var channel = new ComboBox
        {
            ItemsSource = new[] { "稳定版", "预览版" },
            SelectedIndex = _preferences.UpdateChannel == "preview" ? 1 : 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        channel.SelectionChanged += (_, _) =>
        {
            if (channel.SelectedIndex < 0) return;
            if (!Save(_preferences with { UpdateChannel = channel.SelectedIndex == 1 ? "preview" : "stable" }, channelError)) return;
            _hostUpdates.Reset();
            if (_preferences.AutomaticUpdates) _ = _hostUpdates.CheckAsync(_preferences.UpdateChannel);
        };
        _content.Children.Add(Row("更新渠道", channel, channelError));


        var check = PageLayout.AccentButton("检查更新");
        check.Click += async (_, _) => await _hostUpdates.CheckAsync(_preferences.UpdateChannel);
        var status = Paragraph(_hostUpdates.Status);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        _content.Children.Add(SettingsRow(status, check));

        var release = new StackPanel { Spacing = PageLayout.SettingsRowGap, HorizontalAlignment = HorizontalAlignment.Stretch };
        var version = PageLayout.SectionHeading("");
        var download = PageLayout.AccentButton("下载");
        var downloadStatus = Paragraph("");
        AutomationProperties.SetLiveSetting(downloadStatus, AutomationLiveSetting.Polite);
        var notes = Paragraph("");
        var notesRow = SettingsRow(new ScrollViewer { MaxHeight = 300, Content = notes,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, null);
        release.Children.Add(version);
        var packageLabel = Value("");
        release.Children.Add(SettingsRow(packageLabel, download));
        release.Children.Add(downloadStatus);
        release.Children.Add(notesRow);
        _content.Children.Add(release);
        bool choosingFolder = false;
        _refreshUpdate = () =>
        {
            check.IsEnabled = !_hostUpdates.Checking && !_hostUpdates.Downloading;
            channel.IsEnabled = !_hostUpdates.Downloading;
            status.Text = _hostUpdates.Status;
            release.Visibility = _hostUpdates.Offer is null ? Visibility.Collapsed : Visibility.Visible;
            if (_hostUpdates.Offer is not { } offer) return;
            version.Text = $"HHub {offer.Tag.TrimStart('v')}";
            packageLabel.Text = offer.Installer ? "Windows x64 安装包" : "Windows x64 主体包";
            download.Content = $"下载 · {offer.Size / 1048576d:F1} MiB";
            download.IsEnabled = !choosingFolder && !_hostUpdates.Downloading;
            downloadStatus.Text = _hostUpdates.DownloadStatus;
            downloadStatus.Visibility = downloadStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            notesRow.Visibility = string.IsNullOrWhiteSpace(offer.Notes) ? Visibility.Collapsed : Visibility.Visible;
            notes.Text = offer.Notes;
        };
        _hostUpdates.Changed += _refreshUpdate;
        _refreshUpdate();

        download.Click += async (_, _) =>
        {
            if (choosingFolder || _hostUpdates.Offer is not { } offer
                || (Application.Current as App)?.MainWindow is not { } window) return;
            choosingFolder = true;
            download.IsEnabled = false;
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
                if (await picker.PickSingleFolderAsync() is { Path.Length: > 0 } folder)
                    await _hostUpdates.DownloadAsync(offer, folder.Path);
            }
            catch (Exception)
            {
                downloadStatus.Text = "无法打开保存目录，请重试。";
                downloadStatus.Visibility = Visibility.Visible;
            }
            finally { choosingFolder = false; download.IsEnabled = !_hostUpdates.Downloading; }
        };
    }

    private void AddAboutSettings()
    {
        var history = new Button { Content = "更新日志" };
        history.Click += async (_, _) => await ShowCurrentNotesAsync(XamlRoot);
        var preview = new Button { Content = "预览更新提示" };
        preview.Click += (_, _) => _previewUpdateNotice();
        var versionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { history, preview } };
        _content.Children.Add(Row($"版本 {HostReleaseFeed.CurrentVersion}", versionActions, null));
        _content.Children.Add(Row("协议", Value("hhub.plugin/1 · schemaVersion 2"), null));
        AddUpdateSettings();

        _content.Children.Add(PageLayout.SectionHeading("开源许可"));
        var dependencies = new StackPanel { Spacing = 4 };
        foreach (string item in new[]
        {
            ".NET 10 运行时与基础类库",
            "Windows App SDK / WinUI 3",
            "System.Security.Cryptography.Pkcs"
        })
        {
            dependencies.Children.Add(Detail("· " + item));
        }
        _content.Children.Add(SettingsRow(dependencies, null));
        if (_pluginLab is { } lab)
        {
            var pluginLicenses = new StackPanel { Spacing = PageLayout.SettingsRowGap };
            _content.Children.Add(pluginLicenses);
            _ = AddPluginLicensesAsync(lab, pluginLicenses);
        }
    }

    internal static Windows.Foundation.IAsyncOperation<ContentDialogResult> ShowCurrentNotesAsync(XamlRoot root) => new ContentDialog
    {
        XamlRoot = root, Title = "更新日志", CloseButtonText = "关闭",
        Content = new ScrollViewer { MaxHeight = 350, Content = Paragraph(HostReleaseFeed.ReadCurrentNotes()) }
    }.ShowAsync();

    private async Task AddPluginLicensesAsync(PluginLab lab, StackPanel section)
    {
        try
        {
            foreach (var notice in await lab.ReadLicensesAsync())
            {
                if (_selected != 3 || !_content.Children.Contains(section)) return;
                var panel = new Expander
                {
                    Header = notice.Name, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                var content = new StackPanel { Spacing = 8 };
                if (notice.Text.Length != 0) content.Children.Add(Paragraph(notice.Text));
                if (notice.Directory is { } directory)
                {
                    var open = new Button { Content = "打开完整许可文件" };
                    open.Click += async (_, _) =>
                    {
                        try
                        {
                            await Windows.System.Launcher.LaunchFolderAsync(
                                await Windows.Storage.StorageFolder.GetFolderFromPathAsync(directory));
                        }
                        catch (Exception error) when (IsSettingsError(error) || error is System.Runtime.InteropServices.COMException)
                        { open.Content = "无法打开许可文件"; }
                    };
                    content.Children.Add(open);
                }
                panel.Content = content;
                section.Children.Add(panel);
            }
        }
        catch (Exception error) when (IsSettingsError(error) || error is System.Security.Cryptography.CryptographicException)
        { Trace.WriteLine(error); }
    }

    private Border SourceRow(string address, TextBlock error, StackPanel host)
    {
        var input = AddressInput(address);
        var update = PageLayout.AccentButton("保存");
        var remove = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Center };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(update);
        actions.Children.Add(remove);
        var row = SettingsRow(input, actions);
        update.Click += (_, _) =>
        {
            string[] existing = PluginMarkets.Configured(_preferences);
            var result = PluginMarkets.EditAddress(existing, Array.IndexOf(existing, address), input.Text);
            if (!result.Success) { ShowError(error, result.Error!); return; }
            int index = Array.IndexOf(existing, address);
            if (!Save(_preferences with { PluginMarketUrls = result.Addresses }, error)) return;
            address = result.Addresses[index];
            input.Text = address;
        };
        remove.Click += (_, _) =>
        {
            var remaining = PluginMarkets.Configured(_preferences).Where(item => item != address).ToArray();
            if (Save(_preferences with { PluginMarketUrls = remaining }, error)) host.Children.Remove(row);
        };
        return row;
    }

    private bool Save(UserPreferences updated, TextBlock error)
    {
        try
        {
            PluginPreferences.Save(updated);
            _preferences = updated;
            error.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception exception) when (IsSettingsError(exception))
        {
            ShowError(error, "无法保存设置。");
            return false;
        }
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> update)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => update(toggle.IsOn);
        return toggle;
    }

    internal static Border Row(string title, FrameworkElement control, TextBlock? error)
    {
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        labels.Children.Add(new TextBlock
        {
            Text = title, Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap
        });
        if (error is not null) labels.Children.Add(error);
        AutomationProperties.SetName(control, title);
        return SettingsRow(labels, control);
    }

    private static Border SettingsRow(FrameworkElement content, FrameworkElement? action)
    {
        if (action is ComboBox picker) picker.Width = ChoiceWidth;
        var row = PageLayout.CardRow(content, action);
        row.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Padding = new Thickness(PageLayout.SettingsRowPadding.Left, 5, PageLayout.SettingsRowPadding.Right, 5);
        return row;
    }

    private static TextBox AddressInput(string address) => new()
    {
        Text = address, VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock Detail(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["DashboardDetailStyle"]
    };

    private static TextBlock Value(string text) => new()
    {
        Text = text, VerticalAlignment = VerticalAlignment.Center,
        Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
    };

    private static TextBlock ErrorText()
    {
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        return error;
    }

    private static void ShowError(TextBlock error, string message)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
    }

    private static bool IsSettingsError(Exception error) => error is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or JsonException or InvalidDataException;

    private sealed record MarketRecord(DateTimeOffset CheckedAt, int Count, string[] Errors)
    {
        private static string Path => System.IO.Path.Combine(PluginPreferences.Root, "market-record.json");

        public static MarketRecord? Load()
        {
            try
            {
                return File.Exists(Path) ? JsonSerializer.Deserialize<MarketRecord>(File.ReadAllText(Path)) : null;
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                Trace.WriteLine(error);
                return null;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(PluginPreferences.Root);
                File.WriteAllText(Path, JsonSerializer.Serialize(this));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine(error);
            }
        }
    }
}
