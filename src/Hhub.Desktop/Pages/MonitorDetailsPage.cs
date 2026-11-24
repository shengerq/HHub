using System.Text.Json;
using Hhub.PluginContracts;
using Hhub.UnifiedHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Hhub.Desktop.Pages;

internal sealed class MonitorDetailsPage : StackPanel
{
    private static readonly int[] Intervals = [1, 2, 5, 10];
    private static readonly (string Name, string[] Fields)[] Groups =
    [
        ("CPU", [HomeMonitor.CpuName, HomeMonitor.CpuLoad, HomeMonitor.CpuCores, HomeMonitor.CpuThreads,
            HomeMonitor.CpuClock, HomeMonitor.CpuPower, HomeMonitor.CpuTemperature]),
        ("GPU", [HomeMonitor.GpuName, HomeMonitor.GpuLoad, HomeMonitor.GpuClock, HomeMonitor.GpuPower,
            HomeMonitor.GpuTemperature, HomeMonitor.GpuMemoryTemperature, HomeMonitor.GpuMemoryUsed, HomeMonitor.GpuMemoryTotal]),
        ("内存", [HomeMonitor.MemoryModel, HomeMonitor.MemoryManufacturer, HomeMonitor.MemoryLoad, HomeMonitor.MemoryUsed, HomeMonitor.MemoryTotal, HomeMonitor.MemoryClock, HomeMonitor.MemoryTemperature]),
        ("网络与磁盘", [HomeMonitor.NetworkDownload, HomeMonitor.NetworkUpload, HomeMonitor.Disks, HomeMonitor.Volumes])
    ];

    private readonly PluginLab _lab;
    private readonly string? _provider;
    private readonly Grid _header = new() { ColumnSpacing = 12 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly HyperlinkButton _driverDownload = new() { Visibility = Visibility.Collapsed };
    private readonly Button _authorize = new() { Content = "管理员授权", IsEnabled = false };
    private readonly DropDownButton _refresh = new();
    private readonly Grid _tabs = new() { ColumnSpacing = 4 };
    private readonly StackPanel _rows = new() { Spacing = PageLayout.SettingsRowGap };
    private readonly TextBlock _status = Detail("");
    private readonly TextBlock _refreshError = Detail("");
    private readonly Dictionary<string, TextBlock> _readouts = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, JsonElement> _values = new Dictionary<string, JsonElement>();
    private InstalledPackage? _plugin;
    private PluginSession? _session;
    private HashSet<string> _offered = new(StringComparer.Ordinal);
    private string? _driverDownloadLabel;
    private CancellationTokenSource? _load;
    private PeriodicTimer? _timer;
    private TimeSpan _interval;
    private int _selectedGroup;
    private bool _requesting;
    private bool _authorizationError;
    private bool _closed;

    public MonitorDetailsPage(PluginLab lab, Action back, string? provider = null)
    {
        _lab = lab;
        _provider = provider;
        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = PageLayout.Inset;
        Spacing = 10;
        _header.ColumnDefinitions.Add(new());
        _header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = PageLayout.HeadingText(provider is null ? "系统" : "系统监测");
        var breadcrumb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var returnButton = new Button
        {
            Content = PageLayout.BreadcrumbText(provider is null ? "首页" : "插件"),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Padding = new Thickness(0),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        returnButton.Click += (_, _) => back();
        breadcrumb.Children.Add(returnButton);
        breadcrumb.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center });
        breadcrumb.Children.Add(title);
        _header.Children.Add(breadcrumb);
        Grid.SetColumn(_actions, 1);
        _header.Children.Add(_actions);
        _actions.HorizontalAlignment = HorizontalAlignment.Right;
        _actions.Children.Add(_driverDownload);
        _actions.Children.Add(_authorize);
        _actions.Children.Add(_refresh);
        _authorize.Click += RequestAuthorization;
        _interval = PluginPreferences.MonitorInterval();
        var menu = new MenuFlyout();
        foreach (int seconds in Intervals)
        {
            var option = new RadioMenuFlyoutItem { Text = $"{seconds} 秒", GroupName = "MonitorRefresh", IsChecked = _interval.TotalSeconds == seconds };
            option.Click += (_, _) => SetInterval(seconds, menu);
            menu.Items.Add(option);
        }
        _refresh.Flyout = menu;
        _refresh.Content = $"刷新频率 · {_interval.TotalSeconds:0} 秒";
        _refreshError.Visibility = Visibility.Collapsed;
        _status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_refreshError, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Children.Add(_header);
        Children.Add(_status);
        Children.Add(_refreshError);
        Children.Add(_tabs);
        Children.Add(_rows);
        Loaded += Start;
    }

    public void ResizeToViewport(double width)
    {
        if (width <= 0) return;
        double inset = PageLayout.HorizontalInset(width);
        Margin = new(inset, 0, inset, PageLayout.Inset.Bottom);
        Width = PageLayout.ContentWidth(width);
        bool narrow = Width < 650;
        Grid.SetRow(_actions, narrow ? 1 : 0);
        Grid.SetColumn(_actions, narrow ? 0 : 1);
        Grid.SetColumnSpan(_actions, narrow ? 2 : 1);
        LayoutActions();
        LayoutTabs();
    }

    private void LayoutActions()
    {
        double horizontalMinimum = _driverDownload.Visibility == Visibility.Visible ? 420 : 280;
        _actions.Orientation = Width < horizontalMinimum ? Orientation.Vertical : Orientation.Horizontal;
    }

    public void ClosePage()
    {
        _closed = true;
        _load?.Cancel();
    }

    private async void Start(object sender, RoutedEventArgs args)
    {
        Loaded -= Start;
        if (_closed) return;
        using var load = new CancellationTokenSource();
        _load = load;
        try
        {
            var (_, plugins) = await _lab.DiscoverManifestsAsync().WaitAsync(load.Token);
            load.Token.ThrowIfCancellationRequested();
            var offer = ServiceCatalog.Resolve(new(HomeMonitor.Service, HomeMonitor.MajorVersion), plugins, _provider);
            if (offer is null)
            {
                SetStatus("未连接监测插件");
                return;
            }
            var (store, registered) = await _lab.RegisterAsync(item => item.Id == offer.ProviderId).WaitAsync(load.Token);
            if (registered is not [var plugin])
            {
                SetStatus("系统监测插件已禁用或卸载。");
                return;
            }
            _plugin = plugin;
            _offered = offer.CapabilityIds.ToHashSet(StringComparer.Ordinal);
            _driverDownloadLabel = plugin.Definition.Capabilities
                .FirstOrDefault(item => item.Id == HomeMonitor.DriverDownloadUrl && !item.Writable)?.Name;
            for (int i = 0; i < Groups.Length; i++)
            {
                int group = i;
                var tab = PageLayout.Tab(Groups[i].Name, compact: true);
                tab.MinWidth = i == 3 ? 88 : 52;
                tab.HorizontalAlignment = HorizontalAlignment.Stretch;
                tab.Click += (_, _) => SelectGroup(group);
                _tabs.Children.Add(tab);
            }
            LayoutTabs();
            SelectGroup(0);
            await _lab.RunPageAsync(plugin, async () =>
            {
                await using var session = await _lab.StartSessionAsync(store, plugin, load.Token);
                using var timer = new PeriodicTimer(_interval);
                _timer = timer;
                _session = session;
                try
                {
                    do
                    {
                        load.Token.ThrowIfCancellationRequested();
                        var response = await session.ReadAsync(token: load.Token);
                        load.Token.ThrowIfCancellationRequested();
                        _values = response.Values!;
                        UpdateReadouts();
                        UpdateAuthorization();
                    }
                    while (await timer.WaitForNextTickAsync(load.Token));
                }
                finally
                {
                    _timer = null;
                    _session = null;
                    _authorize.IsEnabled = false;
                }
            }, load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
            if (!_closed) SetStatus(MonitorStatus.ReadError(error));
        }
        finally { _load = null; }
    }

    private void SelectGroup(int group)
    {
        for (int i = 0; i < _tabs.Children.Count; i++) ((ToggleButton)_tabs.Children[i]).IsChecked = i == group;
        if (group == _selectedGroup && _rows.Children.Count != 0) return;
        _selectedGroup = group;
        _readouts.Clear();
        _rows.Children.Clear();
        foreach (string field in Groups[group].Fields.Where(_offered.Contains))
        {
            var capability = _plugin!.Definition.Capabilities.Single(item => item.Id == field);
            var value = Detail("—");
            _readouts.Add(field, value);
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock { Text = capability.Name, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(value);
            _rows.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["DashboardCardStyle"], Child = body
            });
        }
        if (_rows.Children.Count == 0) _rows.Children.Add(Detail("此插件未提供该类读数"));
        UpdateReadouts();
    }

    private void LayoutTabs()
    {
        int columns = Width < 280 ? 2 : 4;
        if (_tabs.Children.Count == 0 || _tabs.ColumnDefinitions.Count == columns) return;
        _tabs.ColumnDefinitions.Clear();
        _tabs.RowDefinitions.Clear();
        for (int column = 0; column < columns; column++)
            _tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int row = 0; row < Groups.Length / columns; row++)
            _tabs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < _tabs.Children.Count; i++)
        {
            var tab = (ToggleButton)_tabs.Children[i];
            Grid.SetColumn(tab, i % columns);
            Grid.SetRow(tab, i / columns);
        }
    }

    private void UpdateReadouts()
    {
        foreach (var (field, text) in _readouts)
        {
            string value = _values.TryGetValue(field, out var reading) ? FormatValue(field, reading) : "—";
            if (text.Text != value) text.Text = value;
        }
    }

    private void UpdateAuthorization()
    {
        string? status = MonitorStatus.Authorization(_values);
        bool supported = _plugin!.Definition.Capabilities.Any(item => item.Id == HomeMonitor.AuthorizationRequest && item.Writable);
        _authorize.IsEnabled = supported && !_requesting && _session is not null
            && status is not ("pending" or "granted" or "no-readings" or "not-required" or "driver-missing");
        if (status == "driver-missing" && _driverDownloadLabel is { } downloadLabel
            && _values.GetValueOrDefault(HomeMonitor.DriverDownloadUrl) is { ValueKind: JsonValueKind.String } link
            && Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            if (_driverDownload.NavigateUri != uri) _driverDownload.NavigateUri = uri;
            if (_driverDownload.Visibility != Visibility.Visible)
            {
                _driverDownload.Content = downloadLabel;
                _driverDownload.Visibility = Visibility.Visible;
                LayoutActions();
            }
        }
        else if (_driverDownload.Visibility != Visibility.Collapsed)
        {
            _driverDownload.Visibility = Visibility.Collapsed;
            _driverDownload.NavigateUri = null;
            LayoutActions();
        }
        SetStatus(_authorizationError ? "无法发起管理员授权。" : MonitorStatus.Error(status));
    }

    private void SetStatus(string text)
    {
        if (_status.Text == text) return;
        _status.Text = text;
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void RequestAuthorization(object sender, RoutedEventArgs args)
    {
        if (_session is not { } session || _requesting || _closed) return;
        _requesting = true;
        _authorizationError = false;
        _authorize.IsEnabled = false;
        try
        {
            var response = await session.SetAsync(HomeMonitor.AuthorizationRequest, JsonSerializer.SerializeToElement(true));
            if (!_closed) _values = response.Values!;
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
            _authorizationError = true;
        }
        finally
        {
            _requesting = false;
            if (!_closed && _session is not null) UpdateAuthorization();
        }
    }

    private void SetInterval(int seconds, MenuFlyout menu)
    {
        try
        {
            var preferences = PluginPreferences.Load();
            PluginPreferences.Save(preferences with { MonitorRefreshSeconds = seconds });
            _interval = TimeSpan.FromSeconds(seconds);
            if (_timer is { } timer) timer.Period = _interval;
            _refresh.Content = $"刷新频率 · {seconds} 秒";
            _refreshError.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ((RadioMenuFlyoutItem)menu.Items[Array.IndexOf(Intervals, (int)_interval.TotalSeconds)]).IsChecked = true;
            _refreshError.Text = "无法保存刷新频率。";
            _refreshError.Visibility = Visibility.Visible;
        }
    }

    private static TextBlock Detail(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["DashboardDetailStyle"]
    };

    private static string FormatValue(string field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() is { Length: > 0 } text ? text : "—";
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return "—";
        if (field is HomeMonitor.Disks or HomeMonitor.Volumes)
            return string.Join("\n", value.EnumerateArray().Select(item =>
            {
                string name = item.GetProperty("name").GetString() ?? "";
                string total = $"{item.GetProperty("total").GetDouble():0.#} GiB";
                if (item.TryGetProperty("used", out var used)) return $"{name}　{used.GetDouble():0.#} / {total}";
                if (item.TryGetProperty("temperature", out var degrees))
                {
                    if (degrees.ValueKind == JsonValueKind.Array && degrees.GetArrayLength() == 1) degrees = degrees[0];
                    if (degrees.ValueKind == JsonValueKind.Number) return $"{name}　{total}　{degrees.GetDouble():0.#} °C";
                }
                return $"{name}　{total}";
            }));
        if (value[0].ValueKind != JsonValueKind.Number) return "—";
        double number = value[0].GetDouble();
        return field is HomeMonitor.CpuLoad or HomeMonitor.CpuPower or HomeMonitor.GpuPower
            or HomeMonitor.MemoryLoad or HomeMonitor.MemoryUsed or HomeMonitor.MemoryTotal
            or HomeMonitor.NetworkDownload or HomeMonitor.NetworkUpload ? $"{number:0.0}" : $"{number:0.##}";
    }
}
