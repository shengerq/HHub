using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private const string SponsorUrl = "https://afdian.com/a/Hhub123?utm_source=copylink&utm_medium=link";
    private sealed record DeveloperProfile(string Name, string Role, string Introduction, string? DonationUrl);
    private static readonly DeveloperProfile[] SponsorDevelopers =
    [
        new("shengerq", "项目的奠基人", "创建了 Hhub，决定了其采用的技术以及最初的功能。", SponsorUrl)
    ];

    private void RenderSponsorSettings(StackPanel panel)
    {
        panel.Spacing = 16;
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        TextBlock Paragraph(string value, bool muted = false)
        {
            var text = muted ? SettingsMutedText(value, 14) : Text(value, 14);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            text.LineHeight = 24;
            return text;
        }
        panel.Children.Add(Text("赞助 Hhub", 24, true));
        panel.Children.Add(Paragraph("欢迎抱着好奇心点进这个赞助页面。在这里，你可以投喂开发者，以此让他们获得更多的时间和精力，开发和维护这个软件，让软件的未来有更大的可能性。"));

        foreach (var developer in SponsorDevelopers)
        {
            var identity = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(Paragraph(developer.Name));
            ((TextBlock)identity.Children[0]).FontSize = 20;
            ((TextBlock)identity.Children[0]).FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            identity.Children.Add(Paragraph(developer.Role, muted: true));
            identity.Children.Add(Paragraph(developer.Introduction));
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(identity);
            if (developer.DonationUrl is { } address)
            {
                var donate = Button("捐赠", () => Process.Start(new ProcessStartInfo(address)
                {
                    UseShellExecute = true
                }), true);
                donate.MinWidth = 88;
                donate.VerticalAlignment = VerticalAlignment.Center;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(donate, "捐赠给 " + developer.Name);
                Grid.SetColumn(donate, 1);
                row.Children.Add(donate);
            }
            var card = Card(row, padding: new(16, 12, 16, 12));
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Children.Add(card);
        }

        panel.Children.Add(Paragraph("你的捐赠与否并不会影响你使用这个软件，捐赠再多的钱也不会让你获得特殊的功能。请在自己的能力范围内合理地支持。", muted: true));
    }
    private void RenderAboutSettings(Panel panel)
    {
        string version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.5";
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        identity.Children.Add(Text("Hhub", 28, true));
        identity.Children.Add(Text("版本 " + version, 14));
        panel.Children.Add(identity);
        panel.Children.Add(SettingRow("更新渠道", SettingsChoice(_preferences.UpdateChannel,
            new[] { ("stable", "稳定版"), ("beta", "测试版"), ("dev", "开发版") }, value =>
            {
                _preferences = _preferences with { UpdateChannel = value }; SavePreferences();
                SetAppUpdateOffer(null);
                return Task.CompletedTask;
            })));
        var automatic = SettingToggle(_preferences.AutomaticUpdates, value =>
        { _preferences = _preferences with { AutomaticUpdates = value }; SavePreferences(); });
        panel.Children.Add(SettingRow("自动更新", automatic));
        var runtime = new StackPanel { Spacing = 8 };
        runtime.Children.Add(Text(".NET " + Environment.Version, 14));
        string deps = Path.Combine(AppContext.BaseDirectory, "Hhub.App.deps.json");
        if (File.Exists(deps))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(deps));
            if (document.RootElement.TryGetProperty("libraries", out var libraries))
                foreach (var library in libraries.EnumerateObject().Where(item => item.Value.TryGetProperty("type", out var type) && type.GetString() == "package").OrderBy(item => item.Name))
                    runtime.Children.Add(Text(library.Name.Replace("/", "  "), 13));
        }
        var licenses = new List<(string Label, string Path)>();
        void Collect(string directory, string label)
        {
            if (Directory.Exists(directory)) licenses.AddRange(Directory.GetFiles(directory, "*.txt", SearchOption.AllDirectories)
                .Select(path => (label + " · " + Path.GetFileNameWithoutExtension(path), path)));
        }
        Collect(Path.Combine(AppContext.BaseDirectory, "licenses"), "Hhub");
        var licenseList = new StackPanel { Spacing = 8 };
        foreach (var license in licenses)
        {
            var expander = new Expander { Header = license.Label, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            expander.Expanding += (_, _) => expander.Content ??= new TextBox { Text = File.ReadAllText(license.Path),
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 260 };
            licenseList.Children.Add(expander);
        }
        panel.Children.Add(Text("开源与依赖", 18, true));
        panel.Children.Add(DisclosureCard("Hhub · " + version, () =>
        {
            var details = new StackPanel { Spacing = 12 };
            details.Children.Add(Text("运行环境与依赖", 16, true));
            details.Children.Add(runtime);
            details.Children.Add(Text("开源许可", 16, true));
            details.Children.Add(licenseList);
            return details;
        }));
        foreach (var plugin in _installed.Plugins)
        {
            panel.Children.Add(DisclosureCard(plugin.Manifest.Name + " · " + plugin.Manifest.Version, () => PluginDisclosureView(plugin)));
        }
    }

    private Border DisclosureCard(string title, Func<FrameworkElement> load)
    {
        var header = new Grid { MinHeight = 62 };
        var text = Text(title, 14); text.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(text);
        var details = new Expander { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (string key in new[] { "ExpanderBackground", "ExpanderHeaderBackground", "ExpanderHeaderBorderBrush",
            "ExpanderBorderBrush", "ExpanderContentBackground", "ExpanderContentBorderBrush", "ExpanderDropDownBackground", "ExpanderDropDownBorderBrush" })
            details.Resources[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        details.Expanding += (_, _) => details.Content ??= load();
        var card = Card(details, padding: new(0));
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.MinHeight = 64;
        return card;
    }

    private FrameworkElement PluginDisclosureView(InstalledPlugin plugin)
    {
        var body = new StackPanel { Spacing = 12 };
        void Label(string value, bool muted = false)
        {
            var text = muted ? SettingsMutedText(value) : Text(value);
            text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None;
            body.Children.Add(text);
        }
        if (plugin.Manifest.Disclosure is { } disclosure)
        {
            Label("插件许可 · " + disclosure.License);
            Label("运行环境", false);
            foreach (var item in disclosure.Runtimes)
            {
                string provision = item.Provision switch { "host" => "由 Hhub 提供", "bundled" => "插件自带", _ => "系统提供" };
                Label($"{item.Name} {item.Version} · {item.Architecture} · {provision}");
                Label(item.Description, true);
            }
            void Components(string heading, PluginComponent[] items)
            {
                Label(heading);
                if (items.Length == 0) Label("开发者声明无额外项目", true);
                foreach (var item in items)
                {
                    Label($"{item.Name} {item.Version} · {item.License}");
                    Label(item.Attribution, true);
                    body.Children.Add(new HyperlinkButton { Content = item.ProjectUrl, NavigateUri = new Uri(item.ProjectUrl),
                        HorizontalAlignment = HorizontalAlignment.Left, Padding = new(0) });
                }
            }
            Components("使用的开源项目", disclosure.OpenSourceProjects);
            Components("依赖", disclosure.Dependencies);
        }
        else Label("此版本尚未提供结构化的开源项目、许可和运行环境声明；下方展示包内已有记录。", true);

        if (plugin.Manifest.RequiredCapabilities.Contains("runtime.dotnet10.shared.v1"))
            Label("共享运行环境 · Hhub 提供的 .NET 10", true);
        foreach (string path in Directory.EnumerateFiles(plugin.Directory, "*.runtimeconfig.json", SearchOption.AllDirectories))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (!json.RootElement.TryGetProperty("runtimeOptions", out var options)) continue;
                if (options.TryGetProperty("tfm", out var tfm)) Label("目标运行环境 · " + tfm.GetString(), true);
                foreach (string key in new[] { "framework", "frameworks", "includedFrameworks" })
                {
                    if (!options.TryGetProperty(key, out var value)) continue;
                    var frameworks = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : new[] { value };
                    foreach (var framework in frameworks)
                        if (framework.ValueKind == JsonValueKind.Object && framework.TryGetProperty("name", out var name)
                            && framework.TryGetProperty("version", out var version))
                            Label($"{name.GetString()} {version.GetString()} · {(key == "includedFrameworks" ? "包内运行环境记录" : "运行环境要求")}", true);
                }
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            { Label("无法读取运行环境记录：" + Path.GetFileName(path), true); }
        }

        var packages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(plugin.Directory, "*.deps.json", SearchOption.AllDirectories))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("libraries", out var libraries))
                    foreach (var item in libraries.EnumerateObject())
                        if (item.Value.TryGetProperty("type", out var type) && type.GetString() == "package") packages.Add(item.Name.Replace("/", "  "));
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { Label("无法读取依赖记录：" + Path.GetFileName(path), true); }
        }
        if (packages.Count > 0)
        {
            var packageText = new TextBlock { Text = string.Join("\n", packages), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            body.Children.Add(new Expander { Header = "包内运行依赖", Content = packageText,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        var files = PluginDisclosures.LicenseFiles(plugin.Manifest)
            .Select(file => PluginCatalog.ResolveInside(plugin.Directory, file))
            .Concat(Directory.EnumerateFiles(plugin.Directory, "*", SearchOption.AllDirectories)
                .Where(path =>
                {
                    string name = Path.GetFileName(path);
                    bool notice = name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase) || name.Contains("NOTICE", StringComparison.OrdinalIgnoreCase);
                    string extension = Path.GetExtension(path);
                    return notice && (extension.Length == 0 || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".md", StringComparison.OrdinalIgnoreCase));
                })).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path);
        bool hasLicense = false;
        foreach (string path in files)
        {
            hasLicense = true;
            var license = new Expander { Header = Path.GetRelativePath(plugin.Directory, path),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            license.Expanding += (_, _) =>
            {
                if (license.Content is not null) return;
                try { license.Content = new TextBox { Text = File.ReadAllText(path), IsReadOnly = true, AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap, MaxHeight = 260 }; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { license.Content = Text("无法读取许可文件：" + error.Message); }
            };
            body.Children.Add(license);
        }
        if (!hasLicense) Label("此插件包尚未提供许可文件。", true);
        return body;
    }

    private void RenderPluginSettings(Panel panel)
    {
        panel.Children.Add(Text("管理", 18, true));
        panel.Children.Add(SettingRow("插件目录", Button("打开", OpenPluginFolder)));
        foreach (var plugin in _installed.Plugins)
        {
            string mode = plugin.Manifest.PersistentTaskReason is { } reason ? "持续任务 · " + reason : "按需启动";
            var toggle = SettingToggle(PluginMode(plugin.Manifest.Id) != "disabled", _ => { });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "启用" + plugin.Manifest.Name);
            toggle.Toggled += async (_, _) =>
            {
                bool enabled = toggle.IsOn; toggle.IsEnabled = false;
                try { SetSettingNotice(toggle, mode); await SetPluginModeAsync(plugin, enabled ? "onDemand" : "disabled"); }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
                { SetSettingNotice(toggle, error.Message); }
                finally { toggle.IsEnabled = true; }
            };
            panel.Children.Add(SettingRow(plugin.Manifest.Name, toggle, mode));
        }
        var heading = Text("市场", 18, true); heading.Margin = new(0, 8, 0, 0); panel.Children.Add(heading);
        var markets = new StackPanel { Spacing = 8 }; panel.Children.Add(markets);
        void SaveMarkets(IEnumerable<string> addresses)
        {
            var normalized = PluginMarkets.Normalize(addresses);
            var previous = _preferences;
            _preferences = _preferences with { PluginMarketUrls = normalized, PluginMarketUrl = "" };
            try { SavePreferences(); }
            catch { _preferences = previous; throw; }
            _remoteMarket = []; _marketErrors = []; _marketLoaded = false;
        }
        void PopulateMarkets()
        {
            markets.Children.Clear();
            string[] addresses = PluginMarkets.Configured(_preferences);
            void AddAddressRow(string address, int? index)
            {
                var input = new TextBox { Text = address, PlaceholderText = "https://…/index.json", MinWidth = 0,
                    Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, index is { } number ? "市场网址 " + (number + 1) : "新市场网址");
                var notice = SettingsMutedText(""); notice.TextWrapping = TextWrapping.Wrap; notice.Visibility = Visibility.Collapsed;
                var editor = new StackPanel { Spacing = 4 }; editor.Children.Add(input); editor.Children.Add(notice);
                void ShowFormError(string message)
                {
                    input.BorderBrush = ErrorBrush; notice.Text = message; notice.Visibility = Visibility.Visible;
                }
                void Commit(Action action)
                {
                    try { action(); PopulateMarkets(); }
                    catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
                    {
                        ShowFormError(error.Message);
                    }
                }
                var save = Button(index is null ? "添加" : "保存", () =>
                {
                    var change = PluginMarkets.EditAddress(PluginMarkets.Configured(_preferences), index, input.Text);
                    if (!change.Success) { ShowFormError(change.Error!); return; }
                    Commit(() => SaveMarkets(change.Addresses));
                }, true);
                save.Width = 88;
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(editor); Grid.SetColumn(save, 1); row.Children.Add(save);
                if (index is { } removeIndex)
                {
                    row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    var remove = Button("删除", () => Commit(() => SaveMarkets(PluginMarkets.Configured(_preferences).Where((_, position) => position != removeIndex))), true);
                    remove.Width = 88;
                    Grid.SetColumn(remove, 2); row.Children.Add(remove);
                }
                var marketCard = Card(row, padding: new(16, 10, 16, 10)); marketCard.MinHeight = 64;
                markets.Children.Add(marketCard);
            }
            for (int index = 0; index < addresses.Length; index++) AddAddressRow(addresses[index], index);
            AddAddressRow("", null);
        }
        PopulateMarkets();
    }
}


