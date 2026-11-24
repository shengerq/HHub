using System.Text.Json;
using Hhub.PluginContracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Hhub.Desktop.Pages;

internal sealed partial class DevicePluginSettings : StackPanel
{
    private sealed record Layout(Section[] Sections, Row[]? Header = null, Artwork? Artwork = null,
        string? Overview = null, string? OverviewCategory = null, string[]? OverviewVariants = null,
        KeyCell[][]? KeyLayout = null, Notice? Notice = null, bool KeepActive = false, bool HideTabs = false,
        string? Title = null, int? ShortcutOrder = null, bool UniformTabs = false);
    private sealed record Artwork(string ValueFrom, string[] Variants);
    private sealed record KeyCell(string Label, int? Value = null, double Width = 1);
    private sealed record Notice(string Text, string HideWhen);
    private sealed record Section(string Name, Row[] Rows, string? Selector = null, string? Illustration = null,
        bool ShowArtwork = false, DeviceKeyTest.Key[][]? KeyboardTest = null, string? Hint = null, Subpage[]? Subpages = null,
        Row[]? Toolbar = null, Row[]? Aside = null, CommandMenu? Menu = null, bool IllustrationSide = false,
        int[]? ToolbarGroups = null, Row? Status = null, string? PanelStyle = null, string? ResponseFrom = null, bool InlineDisplayOptions = false);
    private sealed record CommandMenu(string Title, Row Status, Row[] Commands);
    private sealed record Subpage(string Name, Row[] Rows, string? EditLabel = null, string? Unavailable = null);
    private sealed record Row(string Id, string Control, Option[]? Options = null, string? OptionsFrom = null, double Step = 1,
        int Columns = 1, string? Hint = null, string? Glyph = null, Option? Alternate = null, double? Minimum = null,
        double? InitialValue = null, double? ControlWidth = null, bool AllowUnset = false,
        string? EnabledWhen = null, string? Context = null, bool ConfirmedWrite = false, Field[]? Fields = null,
        string? Title = null, bool Detail = false, bool Plain = false, bool HideLabel = false, bool Prompt = false,
        bool Curve = false, string? LimitsFrom = null, string? Unit = null, bool TightSpacing = false);
    private sealed record Option(int Value, string Name);
    private readonly FrameworkElement? _displayOptions;
    private readonly int _displayOptionsSection;
    private sealed record Field(string Id, string Label, Option[]? Options = null, string? OptionsFrom = null,
        double Step = 1, string? Placeholder = null, string? Control = null);

    private readonly Section[] _sections;
    private readonly Artwork? _artwork;
    private readonly KeyCell[][]? _keyLayout;
    private readonly Notice? _notice;
    private readonly TextBlock? _noticeText;
    private readonly Dictionary<string, CapabilityDefinition> _capabilities;
    private readonly Func<string, JsonElement, Task> _write;
    private readonly string _directory;
    private readonly string? _dataDirectory;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel _body = new() { Spacing = PageLayout.SettingsRowGap };
    private readonly Dictionary<string, Action> _updates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _headerUpdates = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, JsonElement> _values = new Dictionary<string, JsonElement>();
    private string? _target;
    private bool _busy;
    private Action? _updateArtwork;
    private Action? _updateResponse;
    private int? _previewArtwork;
    public bool KeepActive { get; }
    public string? PageTitle { get; }

    public static int ReadShortcutOrder(string directory)
    {
        try
        {
            var declaration = new FileInfo(Path.Combine(directory, "device-page.json"));
            if (!declaration.Exists || declaration.Length > 64 * 1024) return int.MaxValue;
            using var stream = declaration.OpenRead();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("shortcutOrder", out var property)
                && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int order)
                ? order : int.MaxValue;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine(error);
            return int.MaxValue;
        }
    }

    public static (Func<string, string?>? Artwork, string? Category) ReadOverview(string directory, string dataDirectory)
    {
        var definition = new FileInfo(Path.Combine(directory, "device-page.json"));
        if (!definition.Exists || definition.Length > 64 * 1024) return default;
        var layout = PluginApi.Read<Layout>(File.ReadAllText(definition.FullName));
        string? category = layout.OverviewCategory?.Trim() is { Length: > 0 and <= 24 } name ? name : null;
        string? fallback = ImagePath(layout.Overview);
        if (layout.OverviewVariants is not { Length: > 0 and <= 8 } variants) return (_ => fallback, category);
        var images = variants.Select(ImagePath).ToArray();
        var state = new FileInfo(Path.Combine(dataDirectory, "device-overview.json"));
        Dictionary<string, int>? selections = null;
        try
        {
            if (state.Exists && state.Length <= 16 * 1024)
                selections = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(state.FullName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine(error);
        }
        return (target => selections?.GetValueOrDefault(target) is int choice && choice > 0 && choice <= images.Length
            ? images[choice - 1] ?? fallback : fallback, category);

        string? ImagePath(string? asset)
        {
            if (asset is null || !asset.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
            try { PluginApi.ValidatePath(asset); }
            catch (InvalidDataException) { return null; }
            var image = new FileInfo(Path.Combine(directory, asset));
            return image.Exists && image.Length <= 384 * 1024 ? image.FullName : null;
        }
    }

    public static DevicePluginSettings? Open(string directory, string? dataDirectory, PluginDefinition plugin,
        Func<string, JsonElement, Task> write, StackPanel? titleControls = null, FrameworkElement? displayOptions = null)
    {
        string file = Path.Combine(directory, "device-page.json");
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 64 * 1024) throw new InvalidDataException("设备界面定义过大。");
        var layout = PluginApi.Read<Layout>(File.ReadAllText(file));
        var sections = layout.Sections;
        var capabilities = plugin.Capabilities.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (sections.Length is < 1 or > 8 || sections.Any(section => section is null || section.Name.Length is < 1 or > 32
            || section.Rows.Length > 40 || section.Rows.Length == 0 && section.KeyboardTest is null && section.Subpages is null
            || section.Selector?.Length > 32
            || section.Hint?.Length > 128
            || section.Toolbar?.Length is > 6 || section.Aside?.Length is > 12
            || section.ToolbarGroups is { } groups && (section.Toolbar is null || groups.Length is < 1 or > 2
                || groups.Any(count => count is < 1 or > 6) || groups.Sum() != section.Toolbar.Length)
            || section.Status is { Control: not "readout" }
            || section.PanelStyle is not (null or "none" or "separate")
            || section.ResponseFrom is { } response && (!capabilities.TryGetValue(response, out var source)
                || source.Value is not { Type: "array", MaxItems: <= 128, Items.Type: "number" })
            || (section.Toolbar is not null || section.Aside is not null || section.Menu is not null || section.Status is not null || section.ResponseFrom is not null)
                && (section.Selector is not null || section.Subpages is not null)
            || section.Menu is { } menu && (menu.Title.Length is < 1 or > 24 || menu.Status.Control != "readout"
                || menu.Commands.Length is < 1 or > 4 || menu.Commands.Any(command => command?.Control != "command"))
            || section.Subpages is { } subpages && (subpages.Length is < 2 or > 5 || section.Rows.Length != 0
                || section.Selector is not null || section.KeyboardTest is not null
                || subpages.Any(page => page is null || page.Name.Length is < 1 or > 32
                    || page.Rows.Length > 40 || page.EditLabel?.Length is > 32
                    || page.Unavailable?.Length is < 1 or > 128 || (page.Rows.Length == 0) != (page.Unavailable is not null)
                    || page.Rows.Any(row => row?.Detail == true) && string.IsNullOrWhiteSpace(page.EditLabel)))
            || section.KeyboardTest is { } keys && (section.Selector is not null || !DeviceKeyTest.IsValid(keys)))
            || layout.Title is { Length: < 1 or > 64 }
            || (layout.Header?.Length ?? 0) > 4 || layout.HideTabs && sections.Length != 1
            || layout.KeyLayout is { } keyLayout && !ValidKeyLayout(keyLayout)
            || layout.Notice is { } notice && (notice.Text.Length is < 1 or > 128
                || !capabilities.TryGetValue(notice.HideWhen, out var hide) || hide.Value.Type != "boolean"))
            throw new InvalidDataException("设备界面定义无效。");
        Row[][] blocks = [layout.Header ?? [], .. sections.SelectMany(section => section.Subpages is { } pages
            ? pages.Select(page => page.Rows)
            : [(Row[])[.. (section.Toolbar ?? []), .. section.Rows, .. (section.Aside ?? []),
                .. (section.Status is { } status ? new[] { status } : []),
                .. (section.Menu is { } menu ? new[] { menu.Status }.Concat(menu.Commands) : [])]])];
        var rows = blocks.SelectMany(block => block);
        if (rows.Any(row => row is null || !capabilities.ContainsKey(row.Id)
            || row.Control is not ("readout" or "choice" or "toggle" or "number" or "slider" or "text" or "axis-table" or "key-binding" or "fields" or "key-grid" or "equalizer" or "command")
            || row.Control == "command" && (!capabilities[row.Id].Writable || capabilities[row.Id].Persistence != "session"
                || capabilities[row.Id].Value is not { Type: "integer", Minimum: 0, Maximum: 2147483647 })
            || row.Control == "equalizer" && (!capabilities[row.Id].Writable || !row.Curve && row.OptionsFrom is null
                || !BandArray(capabilities[row.Id].Value))
            || row.Prompt && row.Control != "text" || row.Curve && row.Control != "equalizer"
            || row.LimitsFrom is { } limits && (!row.Curve || !capabilities.TryGetValue(limits, out var bounds)
                || bounds.Value is not { Type: "array", MaxItems: <= 31, Items: { Type: "object" } item }
                || item.Properties.Count != 2 || !item.Properties.ContainsKey("minimum") || !item.Properties.ContainsKey("maximum"))
            || row.Control == "key-grid" && (layout.KeyLayout is null || row.OptionsFrom is null
                || !capabilities[row.Id].Writable || capabilities[row.Id].Value.Items?.Type != "integer")
            || (row.Options?.Length ?? 0) > 64 || row.Step is <= 0 or > 10000 || row.Columns is < 1 or > 3
            || row.Hint?.Length > 128 || row.Glyph?.Length > 1 || row.Title?.Length is < 1 or > 40
            || row.Unit?.Length > 8
            || row.ControlWidth is < 80 or > 300
            || row.Control == "axis-table" && (row.Step != Math.Truncate(row.Step) || row.Step > 450)
            || row.Alternate is { } alternate && (row.Control is not ("number" or "slider") || alternate.Name.Length is < 1 or > 40
                || !capabilities[row.Id].Value.Accepts(Element(new[] { alternate.Value })))
            || row.OptionsFrom is { } source && !capabilities.ContainsKey(source)
            || row.Context is { } context && !capabilities.ContainsKey(context)
            || row.EnabledWhen is { } condition && (!capabilities.TryGetValue(condition, out var gate) || gate.Value.Type != "boolean")
            || row.Control == "fields" && !ValidFields(row, capabilities)
            || row.Control != "fields" && row.Fields is not null)
            || blocks.Any(block => block.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != block.Length)
            || sections.Any(section => section.Rows.Any(row => row.Detail))
            || (layout.Header ?? []).Any(row => row.Detail || row.Control == "key-grid"))
            throw new InvalidDataException("设备设置行无效。");
        if (layout.Artwork is { } artwork)
        {
            if (!capabilities.ContainsKey(artwork.ValueFrom) || artwork.Variants.Length is < 1 or > 8)
                throw new InvalidDataException("设备示意图定义无效。");
        }
        else if (sections.Any(section => section.ShowArtwork))
            throw new InvalidDataException("设备示意图定义缺失。");
        var illustrations = sections.Select(section => section.Illustration).OfType<string>()
            .Concat(layout.Artwork?.Variants ?? []);
        foreach (string asset in illustrations)
        {
            PluginApi.ValidatePath(asset);
            if (asset.StartsWith("data/", StringComparison.Ordinal)) continue;
            long limit = Path.GetExtension(asset).ToLowerInvariant() switch
            {
                ".svg" => 64 * 1024,
                ".png" => 384 * 1024,
                _ => 0
            };
            if (limit == 0 || new FileInfo(Path.Combine(directory, asset)).Length > limit)
                throw new InvalidDataException("设备示意图无效。");
        }
        return new DevicePluginSettings(directory, dataDirectory, layout, plugin, capabilities, write, titleControls, displayOptions);
    }

    private static bool BandArray(ValueDefinition value) => value is { Type: "array", MaxItems: <= 31, Items: { Type: "object" } band }
        && band.Properties.Count == 2 && band.Properties.ContainsKey("frequency") && band.Properties.ContainsKey("gain");

    private static bool ValidKeyLayout(KeyCell[][] rows)
    {
        if (rows.Length is < 1 or > 8 || rows.Any(row => row is null || row.Length is < 1 or > 24))
            return false;
        var keys = rows.SelectMany(row => row).ToArray();
        if (keys.Any(key => key is null || string.IsNullOrWhiteSpace(key.Label) || key.Label.Length > 16
            || !double.IsFinite(key.Width) || key.Width is < 0.5 or > 12 || key.Value is < 0 or > 65535))
            return false;
        var values = keys.Select(key => key.Value).OfType<int>().ToArray();
        return values.Length > 0 && values.Distinct().Count() == values.Length;
    }

    private static bool ValidFields(Row row, Dictionary<string, CapabilityDefinition> capabilities)
    {
        var schema = capabilities[row.Id].Value;
        if (schema is not { Type: "array", MaxItems: 1, Items: { Type: "object" } item }
            || row.Fields is not { Length: >= 1 and <= 6 } fields || fields.Length != item.Properties.Count)
            return false;
        return fields.All(field => field is not null && field.Label.Length is >= 1 and <= 32
            && field.Placeholder?.Length is not > 40 && field.Step is > 0 and <= 10000
            && item.Properties.TryGetValue(field.Id, out var value)
            && value.Type is "number" or "integer" or "boolean" or "string"
            && (field.Control is null || field.Options is null && field.OptionsFrom is null
                && (field.Control == "slider" && value.Type is "number" or "integer"
                    || field.Control == "color" && value.Type == "string"))
            && ((field.Options?.Length ?? 0) <= 64)
            && (field.Options is null && field.OptionsFrom is null || value.Type == "integer")
            && (field.OptionsFrom is null || capabilities.ContainsKey(field.OptionsFrom)))
            && fields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() == fields.Length;
    }

    private DevicePluginSettings(string directory, string? dataDirectory, Layout layout, PluginDefinition plugin,
        Dictionary<string, CapabilityDefinition> capabilities, Func<string, JsonElement, Task> write, StackPanel? titleControls,
        FrameworkElement? displayOptions)
    {
        _sections = layout.Sections;
        Row[]? header = layout.Header;
        if ((header is null || header.Length == 0) && _sections.Length == 1
            && _sections[0].Rows is { Length: > 0 } firstRows && firstRows[0].Control == "toggle")
        {
            header = [firstRows[0]];
            _sections = [_sections[0] with { Rows = firstRows[1..] }];
        }
        _artwork = layout.Artwork;
        _keyLayout = layout.KeyLayout;
        _notice = layout.Notice;
        KeepActive = layout.KeepActive;
        PageTitle = layout.Title;
        _directory = directory;
        _dataDirectory = dataDirectory;
        _capabilities = capabilities;
        _write = write;
        Spacing = 8;
        if (header is { Length: > 0 } headerRows && titleControls is not null)
        {
            foreach (var row in headerRows)
                if (plugin.Kind != "device" || !SceneDeviceConfigurations.IsDeviceSelector(plugin, row.Id))
                    AddRow(row, titleControls, _headerUpdates, showLabel: true, compact: true);
        }
        var scroll = new ScrollViewer
        {
            Content = _tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled
        };
        if (layout.HideTabs || _sections.Length == 1) scroll.Visibility = Visibility.Collapsed;
        Children.Add(scroll);
        if (_notice is { } notice)
        {
            _noticeText = new TextBlock { Text = notice.Text, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
                Style = (Style)Application.Current.Resources["DashboardDetailStyle"] };
            Children.Add(_noticeText);
        }
        Children.Add(_body);
        _displayOptions = displayOptions;
        if (_displayOptions is not null)
        {
            int settings = Array.FindIndex(_sections, section => section.Name == $"{layout.OverviewCategory}设置");
            _displayOptionsSection = settings >= 0 ? settings : _sections.Length - 1;
            if (!_sections[_displayOptionsSection].InlineDisplayOptions) Children.Add(_displayOptions);
        }
        for (int index = 0; index < _sections.Length; index++)
        {
            int selected = index;
            var tab = PageLayout.SecondaryTab(_sections[index].Name);
            if (layout.UniformTabs) tab.MinWidth = 100;
            tab.Click += (_, _) => Select(selected);
            _tabs.Children.Add(tab);
        }
        Select(0);
    }

    public void Display(string? target, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (target != _target) _previewArtwork = null;
        _target = target;
        _values = values;
        UpdateControls();
    }

    public bool ControlsArtwork(string capabilityId) => _artwork?.ValueFrom == capabilityId;

    private void UpdateControls()
    {
        if (_noticeText is { } text && _notice is { } notice)
            text.Visibility = _target is not null && _values.GetValueOrDefault(notice.HideWhen).ValueKind == JsonValueKind.False
                ? Visibility.Visible : Visibility.Collapsed;
        _updateArtwork?.Invoke();
        _updateResponse?.Invoke();
        foreach (var update in _headerUpdates.Values) update();
        foreach (var update in _updates.Values) update();
    }

    private int ArtworkIndex()
    {
        if (_artwork is not { } artwork) return 0;
        return Math.Clamp(_previewArtwork ?? ArtworkChoice(_values.GetValueOrDefault(artwork.ValueFrom)) ?? 0,
            0, artwork.Variants.Length - 1);
    }

    private static int? ArtworkChoice(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1 && value[0].TryGetInt32(out int number)
            && number > 0 ? number - 1 : null;

    private void Select(int index)
    {
        var selected = (ToggleButton)_tabs.Children[index];
        bool showing = _body.Tag is int current && index == current;
        foreach (var child in _tabs.Children.Cast<ToggleButton>()) child.IsChecked = child == selected;
        if (showing) return;
        if (_displayOptions is not null)
            _displayOptions.Visibility = index == _displayOptionsSection ? Visibility.Visible : Visibility.Collapsed;
        _body.Tag = index;
        _updates.Clear();
        _updateArtwork = null;
        _updateResponse = null;
        _body.Children.Clear();
        var section = _sections[index];
        var content = new StackPanel { Spacing = PageLayout.SettingsRowGap };
        if (section.Hint is { Length: > 0 } hint)
            content.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["DashboardDetailStyle"] });
        bool hasArtwork = section.ShowArtwork || section.Illustration is not null;
        bool flat = section.PanelStyle is null && !hasArtwork && (section.Rows.Any(item => item.Plain)
            || section.Toolbar?.Any(item => item.Plain) == true
            || section.Aside?.Any(item => item.Plain) == true);
        if (section.Selector is not { Length: > 0 } selector)
        {
            FrameworkElement root = hasArtwork ? IllustrationAndControls(section, content) : content;
            _body.Children.Add(root);
            if (section.KeyboardTest is { } layout) content.Children.Add(new DeviceKeyTest(layout));
            if (section.Subpages is { } pages) AddSubpages(content, pages);
            else
            {
                if (section.Toolbar is { } toolbar)
                {
                    if (section.ToolbarGroups is { } groups) content.Children.Add(InlineToolbar(toolbar, groups));
                    else AddSectionRows(content, toolbar);
                }
                if (section.Status is { } status) AddRow(status, content, _updates, dense: true);
                if (section.ResponseFrom is { } response) content.Children.Add(PanelCard(ResponseGraph(response), new(12, 8, 12, 8)));
                var primary = content;
                if (section.Aside is { } aside)
                {
                    primary = new StackPanel { Spacing = PageLayout.SettingsRowGap };
                    var secondary = new StackPanel { Spacing = PageLayout.SettingsRowGap };
                    bool separate = section.PanelStyle == "separate";
                    var columns = new Grid { ColumnSpacing = 14, RowSpacing = 8 };
                    var primaryWidth = new ColumnDefinition { Width = new GridLength(320) };
                    columns.ColumnDefinitions.Add(primaryWidth);
                    columns.ColumnDefinitions.Add(new ColumnDefinition());
                    columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    FrameworkElement left = separate ? PanelCard(primary, new(16)) : primary;
                    FrameworkElement right = separate ? PanelCard(secondary, new(14)) : secondary;
                    columns.Children.Add(left);
                    columns.Children.Add(right);
                    Grid.SetColumn(right, 1);
                    columns.SizeChanged += (_, args) =>
                    {
                        bool narrow = args.NewSize.Width < 625;
                        primaryWidth.Width = narrow ? new GridLength(1, GridUnitType.Star)
                            : new GridLength(section.PanelStyle is null ? 320 : Math.Clamp(args.NewSize.Width * 0.34, 268, 320));
                        Grid.SetColumnSpan(left, narrow ? 2 : 1);
                        Grid.SetColumn(right, narrow ? 0 : 1);
                        Grid.SetColumnSpan(right, narrow ? 2 : 1);
                        Grid.SetRow(right, narrow ? 1 : 0);
                    };
                    content.Children.Add(columns);
                    AddSectionRows(secondary, aside);
                }
                AddSectionRows(primary, section.Rows);
                if (section.InlineDisplayOptions && index == _displayOptionsSection && _displayOptions is { } options)
                {
                    if (options.Parent is Panel previous) previous.Children.Remove(options);
                    primary.Children.Add(options);
                }
                if (section.Menu is { } menu)
                {
                    var choices = new StackPanel { Spacing = 8, MaxWidth = 340 };
                    AddRow(menu.Status, choices, _updates, showLabel: false, compact: true);
                    foreach (var command in menu.Commands) AddRow(command, choices, _updates, showLabel: false);
                    var button = new DropDownButton { Content = menu.Title, Flyout = new Flyout { Content = choices } };
                    primary.Children.Add(button);
                }
            }
            if (flat)
            {
                _body.Children.Remove(root);
                _body.Children.Add(new Border
                {
                    Child = root,
                    Style = (Style)Application.Current.Resources["DashboardCardStyle"],
                    Padding = new Thickness(16)
                });
            }
            return;
        }

        var keys = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        AutomationProperties.SetName(keys, selector);
        for (int i = 0; i < 2; i++) keys.ColumnDefinitions.Add(new ColumnDefinition());
        var editor = new StackPanel();
        if (hasArtwork) _body.Children.Add(IllustrationAndControls(section, content, keys));
        else
        {
            _body.Children.Add(content);
            content.Children.Add(keys);
        }
        content.Children.Add(editor);
        int active = -1;
        for (int i = 0; i < section.Rows.Length; i++)
        {
            int keyIndex = i;
            if (i % 2 == 0) keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var button = new ToggleButton { Content = _capabilities[section.Rows[i].Id].Name,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(button, i % 2);
            Grid.SetRow(button, i / 2);
            button.Click += (_, _) => SelectKey(keyIndex);
            keys.Children.Add(button);
        }
        SelectKey(0);
        void SelectKey(int index)
        {
            for (int i = 0; i < keys.Children.Count; i++)
                ((ToggleButton)keys.Children[i]).IsChecked = i == index;
            if (active == index) return;
            active = index;
            _updates.Clear();
            editor.Children.Clear();
            AddRow(section.Rows[index], editor, _updates, showLabel: false);
        }
    }

    private void AddSubpages(StackPanel parent, Subpage[] pages)
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var page = new StackPanel { Spacing = PageLayout.SettingsRowGap };
        parent.Children.Add(new ScrollViewer { Content = tabs, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled });
        parent.Children.Add(page);
        int active = -1;
        for (int i = 0; i < pages.Length; i++)
        {
            int index = i;
            var tab = PageLayout.SecondaryTab(pages[i].Name);
            tab.Click += (_, _) => Show(index);
            tabs.Children.Add(tab);
        }
        Show(0);

        void Show(int index)
        {
            for (int i = 0; i < tabs.Children.Count; i++)
                ((ToggleButton)tabs.Children[i]).IsChecked = i == index;
            if (index == active) return;
            active = index;
            _updates.Clear();
            page.Children.Clear();
            if (pages[index].Unavailable is { } unavailable)
                page.Children.Add(new TextBlock { Text = unavailable, TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["DashboardDetailStyle"] });
            else
                AddSectionRows(page, pages[index].Rows, pages[index].EditLabel);
        }
    }

    private void AddSectionRows(StackPanel content, Row[] rows, string? editLabel = null)
    {
        var details = new StackPanel { Spacing = PageLayout.SettingsRowGap };
        var editor = editLabel is null ? null : new Expander
        {
            Header = editLabel, Content = details, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        for (int i = 0; i < rows.Length;)
        {
            var row = rows[i];
            if (row.Detail && editor is not null && !content.Children.Contains(editor)) content.Children.Add(editor);
            var destination = row.Detail ? details : content;
            if (row.Columns == 1 || row.Detail || editor is not null)
            {
                AddRow(row, destination, _updates, openEditor: editor is null ? null : () => editor.IsExpanded = true);
                if (row.TightSpacing && destination.Children.LastOrDefault() is FrameworkElement element)
                    element.Margin = new Thickness(0, -PageLayout.SettingsRowGap / 2, 0, -PageLayout.SettingsRowGap / 2);
                i++;
                continue;
            }
            var group = new Grid { ColumnSpacing = 8 };
            int count = Math.Min(row.Columns, rows.Length - i);
            for (int j = 0; j < count; j++)
            {
                group.ColumnDefinitions.Add(new ColumnDefinition());
                group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var cell = new StackPanel();
                Grid.SetColumn(cell, j);
                group.Children.Add(cell);
                AddRow(rows[i + j], cell, _updates, dense: true);
            }
            group.SizeChanged += (_, args) =>
            {
                bool compact = args.NewSize.Width < count * 220;
                group.RowSpacing = compact ? PageLayout.SettingsRowGap : 0;
                for (int j = 0; j < count; j++)
                {
                    var cell = (StackPanel)group.Children[j];
                    Grid.SetColumn(cell, compact ? 0 : j);
                    Grid.SetColumnSpan(cell, compact ? count : 1);
                    Grid.SetRow(cell, compact ? j : 0);
                }
            };
            destination.Children.Add(group);
            i += count;
        }
    }

    private string? IllustrationPath(string asset)
    {
        if (asset.StartsWith("data/", StringComparison.Ordinal))
        {
            if (_dataDirectory is null) return null;
            string full = Path.GetFullPath(Path.Combine(_dataDirectory,
                asset["data/".Length..].Replace('/', Path.DirectorySeparatorChar)));
            string root = Path.GetFullPath(_dataDirectory) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        return Path.GetFullPath(Path.Combine(_directory, asset.Replace('/', Path.DirectorySeparatorChar)));
    }

    private Grid IllustrationAndControls(Section section, StackPanel content, Grid? side = null)
    {
        bool beside = section.IllustrationSide && side is null;
        var image = new Image { Height = 216, MaxWidth = beside ? 216 : 480, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAccessibilityView(image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var layout = new Grid { ColumnSpacing = 20, RowSpacing = 10 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(image);
        layout.Children.Add(content);
        if (beside)
        {
            image.Width = 216;
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(content, 1);
            layout.SizeChanged += (_, args) =>
            {
                bool narrow = args.NewSize.Width < 680;
                image.Width = narrow ? double.NaN : 216;
                image.MaxWidth = narrow ? 480 : 216;
                image.Height = narrow ? Math.Clamp(args.NewSize.Width * 0.30, 112, 216) : 216;
                Grid.SetColumn(content, narrow ? 0 : 1);
                Grid.SetColumnSpan(content, narrow ? 2 : 1);
                Grid.SetRow(content, narrow ? 1 : 0);
            };
        }
        else
        {
            Grid.SetRow(content, 1);
            if (side is not null)
            {
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                layout.ColumnDefinitions.Add(new ColumnDefinition());
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                side.VerticalAlignment = VerticalAlignment.Center;
                layout.Children.Insert(0, side);
                Grid.SetColumn(image, 1);
                Grid.SetColumnSpan(content, 2);
            }
            layout.SizeChanged += (_, args) =>
            {
                image.Height = Math.Clamp(args.NewSize.Width * 0.30, 112, 216);
                if (side is null) return;
                bool narrow = args.NewSize.Width < 475;
                side.Width = narrow ? double.NaN : 206;
                Grid.SetColumnSpan(side, narrow ? 2 : 1);
                Grid.SetColumn(image, narrow ? 0 : 1);
                Grid.SetColumnSpan(image, narrow ? 2 : 1);
                Grid.SetRow(image, narrow ? 1 : 0);
                Grid.SetRow(content, narrow ? 2 : 1);
            };
        }
        string? requested = null;
        long requestedStamp = 0;
        int revision = 0;
        async void UpdateArtwork()
        {
            if (!image.IsLoaded) return;
            string? asset = section.Illustration;
            if (section.ShowArtwork && _artwork is { } artwork)
                asset = artwork.Variants[ArtworkIndex()];
            if (asset is null || IllustrationPath(asset) is not { } path || !File.Exists(path))
            {
                requested = null;
                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                return;
            }
            long stamp;
            try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            if (asset == requested && stamp == requestedStamp) return;
            requested = asset;
            requestedStamp = stamp;
            int update = ++revision;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                if (update != revision) return;
                Microsoft.UI.Xaml.Media.ImageSource source;
                if (asset.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var svg = new SvgImageSource { RasterizePixelHeight = 256 };
                    if (await svg.SetSourceAsync(stream) != SvgImageSourceLoadStatus.Success)
                        throw new InvalidDataException("无法读取设备示意图。");
                    source = svg;
                }
                else
                {
                    var bitmap = new BitmapImage { DecodePixelHeight = 256, DecodePixelType = DecodePixelType.Logical };
                    await bitmap.SetSourceAsync(stream);
                    source = bitmap;
                }
                if (update == revision)
                {
                    image.Source = source;
                    image.Visibility = Visibility.Visible;
                }
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.WriteLine(error);
                requested = null;
                if (update == revision) image.Visibility = Visibility.Collapsed;
            }
        }
        _updateArtwork = UpdateArtwork;
        image.Loaded += (_, _) => UpdateArtwork();
        image.Unloaded += (_, _) =>
        {
            revision++;
            requested = null;
            requestedStamp = 0;
            image.Source = null;
        };
        return layout;
    }

    private void AddRow(Row row, StackPanel host, Dictionary<string, Action> updates, bool showLabel = true, bool dense = false,
        bool compact = false, Action? openEditor = null, bool toolbar = false)
    {
        var capability = _capabilities[row.Id];
        var label = new TextBlock { Text = row.Title ?? capability.Name, TextWrapping = TextWrapping.Wrap };
        var caption = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(label);
        if (!compact && row.Hint is { Length: > 0 } rowHint)
            caption.Children.Add(new TextBlock
            {
                Text = rowHint,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["DashboardDetailStyle"]
            });
        var status = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["DashboardDetailStyle"] };
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var controls = new StackPanel { Spacing = 6 };
        double? width = row.ControlWidth ?? (row.Control == "slider" ? 224d : null);
        if (width is { } requestedWidth) controls.Width = requestedWidth;
        bool showCaption = showLabel && !row.HideLabel && row.Control != "command" && !(row.Plain && row.Control == "text");
        bool inline = showCaption && row.Control is not ("axis-table" or "fields" or "key-grid" or "equalizer");
        if (inline)
        {
            var line = new Grid { ColumnSpacing = dense ? 6 : 12, MinHeight = 32 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = toolbar ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = toolbar ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            controls.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(caption);
            Grid.SetColumn(controls, 1);
            line.Children.Add(controls);
            content.Children.Add(line);
            if (!toolbar && width is { } preferredWidth)
                line.SizeChanged += (_, args) =>
                    controls.Width = Math.Clamp(args.NewSize.Width - 96, Math.Min(146, preferredWidth), preferredWidth);
        }
        else
        {
            if (showCaption) content.Children.Add(caption);
            content.Children.Add(controls);
        }
        if (!compact) content.Children.Add(status);
        host.Children.Add(compact || row.Plain ? content : new Border
        {
            Style = (Style)Application.Current.Resources["DashboardCardStyle"],
            MinHeight = PageLayout.SettingsRowHeight,
            Padding = PageLayout.SettingsRowPadding,
            Child = content
        });
        JsonElement last = default;
        JsonElement lastContext = default;
        string? lastTarget = null;
        bool updating = false;
        bool HasReading(JsonElement value) => value.ValueKind != JsonValueKind.Undefined
            && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 0);
        bool Ready() => !_busy && _target is not null && capability.Writable
            && (HasReading(last) || row.AllowUnset && last.ValueKind != JsonValueKind.Undefined)
            && (row.EnabledWhen is null || _values.GetValueOrDefault(row.EnabledWhen).ValueKind == JsonValueKind.True);
        async Task Save(JsonElement value)
        {
            if (!Ready() || updating || SettingsValueEquals(last, value)) return;
            if (!capability.Value.Accepts(value))
            {
                status.Text = "输入值超出允许范围。";
                status.Visibility = Visibility.Visible;
                return;
            }
            _busy = true;
            if (_artwork?.ValueFrom == row.Id) _previewArtwork = ArtworkChoice(value);
            status.Text = row.ConfirmedWrite ? "等待设备确认…" : "正在写入并回读…";
            status.Visibility = Visibility.Visible;
            if (compact) ToolTipService.SetToolTip(content, status.Text);
            UpdateControls();
            try
            {
                await _write(row.Id, value);
                status.Visibility = Visibility.Collapsed;
                if (compact) ToolTipService.SetToolTip(content, null);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.WriteLine(error);
                status.Text = error.Message == "transaction.rolled-back" ? "写入未生效，已恢复原值。"
                    : error.Message == "transaction.state-unknown" ? "设备状态未确认，请核对当前设置。"
                    : "写入失败，请检查设备连接和当前配置。";
                if (compact) ToolTipService.SetToolTip(content, status.Text);
            }
            finally
            {
                _busy = false;
                _previewArtwork = null;
                last = default;
                UpdateControls();
            }
        }

        Action<JsonElement> render;
        Action<bool>? updateEditable = null;
        switch (row.Control)
        {
            case "command":
                var run = new Button { Content = label.Text };
                controls.Children.Add(run);
                run.Click += async (_, _) =>
                {
                    if (last.ValueKind == JsonValueKind.Number && last.TryGetInt32(out int token) && token < int.MaxValue)
                        await Save(Element(token + 1));
                };
                render = _ => { };
                break;
            case "key-grid":
                (render, updateEditable) = KeyPicker(controls, row, Save, openEditor);
                break;
            case "equalizer":
                (render, updateEditable) = row.Curve ? CurveEqualizer(controls, row, Save) : Equalizer(controls, row, Save);
                break;
            case "choice":
                var picker = new ComboBox
                {
                    MinWidth = row.ControlWidth is null && !toolbar ? dense ? 98 : 125 : 0,
                    HorizontalAlignment = row.ControlWidth is null && !toolbar ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                    DisplayMemberPath = nameof(Option.Name)
                };
                AutomationProperties.SetName(picker, capability.Name);
                controls.Children.Add(picker);
                picker.SelectionChanged += async (_, _) =>
                {
                    if (picker.SelectedItem is Option option) await Save(Element(new[] { option.Value }));
                };
                render = value =>
                {
                    Option[] options = row.Options ?? ReadOptions(row.OptionsFrom);
                    if (picker.ItemsSource is not Option[] previous || !previous.SequenceEqual(options)) picker.ItemsSource = options;
                    int current = HasReading(value) ? value[0].GetInt32() : -1;
                    picker.PlaceholderText = current >= 0 && !options.Any(option => option.Value == current)
                        ? current.ToString() : row.HideLabel ? label.Text : compact ? "配置" : "—";
                    picker.SelectedIndex = Array.FindIndex(options, option => option.Value == current);
                };
                break;
            case "toggle":
                var toggle = NativeSwitch();
                toggle.HorizontalAlignment = HorizontalAlignment.Right;
                if (showCaption) AutomationProperties.SetLabeledBy(toggle, label);
                else AutomationProperties.SetName(toggle, label.Text);
                if (row.HideLabel) ToolTipService.SetToolTip(toggle, label.Text);
                controls.Children.Add(toggle);
                toggle.Toggled += async (_, _) => await Save(Element(new[] { toggle.IsOn }));
                render = value => toggle.IsOn = HasReading(value) && value[0].GetBoolean();
                break;
            case "number":
            case "slider":
                double minimum = row.Minimum ?? capability.Value.Items?.Minimum ?? 0;
                if (row.Alternate is { } alternate)
                    minimum = Math.Max(minimum, alternate.Value + 1);
                double maximum = capability.Value.Items?.Maximum ?? 65535;
                var inputRow = new Grid { ColumnSpacing = 8 };
                inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                inputRow.ColumnDefinitions.Add(new ColumnDefinition());
                controls.Children.Add(inputRow);
                var alternateValue = row.Alternate is not null ? NativeSwitch() : null;
                double lastNumeric = row.InitialValue ?? minimum;
                if (alternateValue is not null)
                {
                    var alternateLabel = new TextBlock { Text = row.Alternate!.Name, VerticalAlignment = VerticalAlignment.Center };
                    var mode = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    mode.Children.Add(alternateLabel);
                    mode.Children.Add(alternateValue);
                    AutomationProperties.SetLabeledBy(alternateValue, alternateLabel);
                    alternateValue.Toggled += async (_, _) =>
                    {
                        if (!updating) await Save(Element(new[] { alternateValue.IsOn ? row.Alternate.Value : lastNumeric }));
                    };
                    inputRow.Children.Add(mode);
                }
                var field = new Grid { ColumnSpacing = 6 };
                Grid.SetColumn(field, 1);
                inputRow.Children.Add(field);
                Control numericInput;
                Action<double> displayNumeric;
                var commit = Debounce(field, async () =>
                {
                    if (alternateValue?.IsOn != true) await Save(Element(new[] { Math.Round(lastNumeric, 6) }));
                });
                if (row.Control == "slider")
                {
                    field.ColumnDefinitions.Add(new ColumnDefinition());
                    field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var slider = new Slider
                    {
                        Minimum = minimum, Maximum = maximum, StepFrequency = row.Step, SmallChange = row.Step,
                        SnapsTo = SliderSnapsTo.StepValues, IsThumbToolTipEnabled = false
                    };
                    var sliderValue = new TextBlock { MinWidth = 28, VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Right, Style = (Style)Application.Current.Resources["DashboardDetailStyle"] };
                    Grid.SetColumn(sliderValue, 1);
                    field.Children.Add(slider);
                    field.Children.Add(sliderValue);
                    numericInput = slider;
                    bool dragging = false;
                    slider.ValueChanged += (_, args) =>
                    {
                        if (updating) return;
                        lastNumeric = args.NewValue;
                        sliderValue.Text = Math.Round(lastNumeric, 6).ToString("0.######") + row.Unit;
                        if (!dragging) commit();
                    };
                    slider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => dragging = true), true);
                    slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => FinishDrag()), true);
                    slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => FinishDrag()), true);
                    void FinishDrag()
                    {
                        if (!dragging) return;
                        dragging = false;
                        if (!updating) commit();
                    }
                    displayNumeric = value =>
                    {
                        slider.Value = double.IsFinite(value) ? value : lastNumeric;
                        sliderValue.Text = double.IsFinite(value) ? value.ToString("0.######") + row.Unit : "—";
                    };
                }
                else
                {
                    var number = NumberBox(minimum, maximum, row.Step);
                    number.MinWidth = 86;
                    field.Children.Add(number);
                    numericInput = number;
                    number.ValueChanged += (_, args) =>
                    {
                        if (updating || !double.IsFinite(args.NewValue)) return;
                        lastNumeric = args.NewValue;
                        commit();
                    };
                    displayNumeric = value => number.Value = value;
                }
                AutomationProperties.SetLabeledBy(numericInput, label);
                updateEditable = ready => numericInput.IsEnabled = ready && alternateValue?.IsOn != true;
                render = value =>
                {
                    double current = HasReading(value) ? value[0].GetDouble() : double.NaN;
                    bool useAlternate = row.Alternate is { } alternateOption && current == alternateOption.Value;
                    if (alternateValue is not null) alternateValue.IsOn = useAlternate;
                    if (!useAlternate && double.IsFinite(current)) lastNumeric = current;
                    displayNumeric(useAlternate ? double.NaN : current);
                };
                break;
            case "text":
                var input = new TextBox { MaxLength = capability.Value.MaxLength, MinWidth = row.Prompt ? 200 : row.Plain ? 110 : 165,
                    PlaceholderText = row.Prompt ? "输入名称" : row.Plain ? label.Text : "" };
                AutomationProperties.SetName(input, label.Text);
                if (row.Prompt)
                {
                    var editor = new StackPanel { Spacing = 8 };
                    var saveText = SaveButton(editor, input);
                    var flyout = new Flyout { Content = editor };
                    var open = new Button { Content = label.Text, Flyout = flyout };
                    controls.Children.Add(open);
                    flyout.Opened += (_, _) => { input.Text = ""; input.Focus(FocusState.Programmatic); };
                    saveText.Click += async (_, _) =>
                    {
                        if (string.IsNullOrWhiteSpace(input.Text)) return;
                        await Save(Element(input.Text.Trim()));
                        if (status.Visibility == Visibility.Collapsed) flyout.Hide();
                    };
                }
                else
                {
                    var saveText = SaveButton(controls, input);
                    saveText.Click += async (_, _) => await Save(Element(input.Text.Trim()));
                }
                render = value => input.Text = HasReading(value) ? value.GetString() ?? "" : "";
                break;
            case "axis-table":
                render = AxisTable(controls, Save, (int)row.Step, capability.Value);
                break;
            case "key-binding":
                render = KeyBinding(controls, Save);
                break;
            case "fields":
                render = EditFields(controls, row.Fields!, capability.Value.Items!, Save,
                    () => { status.Text = "请填写所有字段。"; status.Visibility = Visibility.Visible; });
                break;
            default:
                var reading = new TextBlock { Style = (Style)Application.Current.Resources["DashboardDetailStyle"],
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                if (row.Glyph is { } glyph)
                {
                    var icon = new FontIcon { Glyph = glyph, FontSize = 14 };
                    if (row.Hint is { } hint) ToolTipService.SetToolTip(icon, hint);
                    var display = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    display.Children.Add(reading);
                    display.Children.Add(icon);
                    controls.Children.Add(display);
                }
                else controls.Children.Add(reading);
                render = value =>
                {
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0) value = value[0];
                    reading.Text = value.ValueKind switch
                    {
                        JsonValueKind.True => "开启",
                        JsonValueKind.False => "关闭",
                        JsonValueKind.String when value.GetString() is "" && !row.HideLabel => "—",
                        JsonValueKind.Array or JsonValueKind.Undefined => "—",
                        _ => value.ToString()
                    };
                    if (row.HideLabel) reading.Visibility = string.IsNullOrEmpty(reading.Text) || value.ValueKind == JsonValueKind.Undefined
                        ? Visibility.Collapsed : Visibility.Visible;
                };
                break;
        }

        void Update()
        {
            var value = _values.GetValueOrDefault(row.Id);
            updating = true;
            try
            {
                JsonElement context = row.Context is { } scope ? _values.GetValueOrDefault(scope) : default;
                bool changed = !SettingsValueEquals(last, value) || !SettingsValueEquals(lastContext, context)
                    || lastTarget != _target;
                lastContext = context;
                lastTarget = _target;
                if (changed || row.OptionsFrom is not null && row.Control is not ("key-grid" or "equalizer"))
                {
                    last = value;
                    render(value);
                }
                if (capability.Writable)
                {
                    controls.IsHitTestVisible = Ready();
                    SetControlsEnabled(controls, Ready());
                    updateEditable?.Invoke(Ready());
                    if (!HasReading(value) && !_busy && !row.AllowUnset)
                    {
                        status.Text = _target is null ? "" : "当前设备没有返回可写入的读数。";
                        status.Visibility = _target is null ? Visibility.Collapsed : Visibility.Visible;
                    }
                    else if (HasReading(value) && status.Text == "当前设备没有返回可写入的读数。")
                        status.Visibility = Visibility.Collapsed;
                }
            }
            finally { updating = false; }
        }
        updates.Add(row.Id, Update);
        Update();
    }

    private static bool SettingsValueEquals(JsonElement left, JsonElement right) =>
        left.ValueKind == right.ValueKind
        && (left.ValueKind == JsonValueKind.Undefined || JsonElement.DeepEquals(left, right));

    private Option[] ReadOptions(string? id) => id is not null && _values.GetValueOrDefault(id) is { ValueKind: JsonValueKind.Array } value
        ? PluginApi.Read<Option[]>(value.GetRawText()) : [];

    private sealed record EqualizerBand(double Frequency, double Gain);

    private (Action<JsonElement> Display, Action<bool> SetEditable) Equalizer(StackPanel controls, Row row,
        Func<JsonElement, Task> save)
    {
        var counts = new ComboBox { PlaceholderText = "选择段数", MinWidth = 100 };
        var strips = new Grid();
        var viewport = ScrollBandBank(strips);
        var selector = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        selector.Children.Add(new TextBlock { Text = "频段数", VerticalAlignment = VerticalAlignment.Center });
        selector.Children.Add(counts);
        controls.Children.Add(selector);
        controls.Children.Add(viewport);
        AutomationProperties.SetName(counts, "频段数");
        AutomationProperties.SetName(strips, "均衡器频段");

        EqualizerBand[] bands = [];
        EqualizerBand[][] shapes = [];
        int[] countOptions = [];
        bool displaying = false;
        var editors = new List<(Slider Gain, Button Frequency, NumberBox Input, TextBlock Value)>();
        counts.SelectionChanged += async (_, _) =>
        {
            if (displaying || counts.SelectedItem is not int count || count == bands.Length) return;
            var shape = shapes.FirstOrDefault(shape => shape.Length == count);
            if (shape is not null) await save(Element(shape));
        };
        async Task EditBand(int index, double? frequency = null, double? gain = null)
        {
            if (displaying || (uint)index >= bands.Length) return;
            var edited = (EqualizerBand[])bands.Clone();
            edited[index] = new(frequency ?? edited[index].Frequency, gain ?? edited[index].Gain);
            await save(Element(edited));
        }

        void AddBand(int index)
        {
            var strip = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            var gain = new Slider { Minimum = -12, Maximum = 12, StepFrequency = 0.5, SmallChange = 0.5,
                SnapsTo = SliderSnapsTo.StepValues, Orientation = Orientation.Vertical, Height = 174,
                HorizontalAlignment = HorizontalAlignment.Center, IsThumbToolTipEnabled = false };
            var level = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
            var frequency = new Button { MinWidth = 0, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Center };
            var frequencyInput = NumberBox(20, 20000, 1, header: "中心频率（Hz）");
            var confirm = new Button { Content = "确定" };
            var editor = new StackPanel { Spacing = 8 };
            editor.Children.Add(frequencyInput);
            editor.Children.Add(confirm);
            var flyout = new Flyout { Content = editor };
            frequency.Flyout = flyout;
            flyout.Opening += (_, _) => frequencyInput.Value = bands[index].Frequency;
            confirm.Click += async (_, _) =>
            {
                if (!double.IsFinite(frequencyInput.Value)) return;
                flyout.Hide();
                await EditBand(index, frequency: frequencyInput.Value);
            };
            bool dragging = false;
            var commit = Debounce(gain, () => EditBand(index, gain: Math.Round(gain.Value, 1)));
            gain.ValueChanged += (_, args) =>
            {
                level.Text = $"{args.NewValue:+0.#;-0.#;0}";
                if (!displaying && !dragging) commit();
            };
            gain.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => dragging = true), true);
            gain.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => Finish()), true);
            gain.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => Finish()), true);
            void Finish()
            {
                if (!dragging) return;
                dragging = false;
                if (!displaying) commit();
            }
            strip.Children.Add(level);
            strip.Children.Add(gain);
            strip.Children.Add(frequency);
            strips.ColumnDefinitions.Add(new());
            Grid.SetColumn(strip, index);
            strips.Children.Add(strip);
            editors.Add((gain, frequency, frequencyInput, level));
        }
        return (value =>
        {
            displaying = true;
            try
            {
                var defaults = _values.GetValueOrDefault(row.OptionsFrom!);
                shapes = defaults.ValueKind == JsonValueKind.Array ? PluginApi.Read<EqualizerBand[][]>(defaults.GetRawText()) : [];
                int[] available = shapes.Select(shape => shape.Length).ToArray();
                if (!countOptions.SequenceEqual(available)) counts.ItemsSource = countOptions = available;
                bands = value.ValueKind == JsonValueKind.Array ? PluginApi.Read<EqualizerBand[]>(value.GetRawText()) : [];
                counts.SelectedIndex = Array.IndexOf(countOptions, bands.Length);
                if (editors.Count != bands.Length)
                {
                    strips.Children.Clear();
                    strips.ColumnDefinitions.Clear();
                    editors.Clear();
                    for (int index = 0; index < bands.Length; index++) AddBand(index);
                    SizeBandBank(viewport, strips, bands.Length);
                }
                for (int index = 0; index < bands.Length; index++)
                {
                    var band = bands[index];
                    var item = editors[index];
                    item.Gain.Value = band.Gain;
                    item.Value.Text = $"{band.Gain:+0.#;-0.#;0}";
                    item.Frequency.Content = band.Frequency >= 1000 ? $"{band.Frequency / 1000:0.#}k" : $"{band.Frequency:0.#}";
                    AutomationProperties.SetName(item.Gain, $"{band.Frequency:0.#} Hz 增益（dB）");
                    AutomationProperties.SetName(item.Frequency, $"修改中心频率 {band.Frequency:0.#} Hz");
                }
            }
            finally { displaying = false; }
        }, ready =>
        {
            foreach (var item in editors)
            {
                item.Gain.IsEnabled = ready;
                item.Frequency.IsEnabled = ready;
            }
        });
    }

    private (Action<JsonElement> Display, Action<bool> SetEditable) KeyPicker(StackPanel controls, Row row,
        Func<JsonElement, Task> save, Action? openEditor)
    {
        var keyboard = new StackPanel { Width = 760, Spacing = 4 };
        var keys = new Dictionary<int, ToggleButton>();
        var selectedKey = new TextBlock { Style = (Style)Application.Current.Resources["DashboardDetailStyle"] };
        int? selected = null;
        foreach (var keyRow in _keyLayout!)
        {
            var line = new Grid { ColumnSpacing = 4 };
            foreach (var key in keyRow)
            {
                var button = new ToggleButton { Content = key.Label, Height = 36, MinWidth = 0, Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsEnabled = false };
                AutomationProperties.SetName(button, key.Label);
                if (key.Value is { } value)
                {
                    keys.Add(value, button);
                    button.Click += async (_, _) =>
                    {
                        button.IsChecked = selected == value;
                        openEditor?.Invoke();
                        await save(Element(new[] { value }));
                    };
                }
                Grid.SetColumn(button, line.ColumnDefinitions.Count);
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(key.Width, GridUnitType.Star) });
                line.Children.Add(button);
            }
            keyboard.Children.Add(line);
        }
        var view = new Viewbox { MaxWidth = 760, StretchDirection = StretchDirection.DownOnly, Child = keyboard };
        controls.Children.Add(view);
        controls.Children.Add(selectedKey);
        return (value =>
        {
            selected = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1 ? value[0].GetInt32() : null;
            foreach (var pair in keys) pair.Value.IsChecked = pair.Key == selected;
            if (selected is { } code && keys.TryGetValue(code, out var key))
                selectedKey.Text = $"当前按键 · {key.Content}";
            else
                selectedKey.Text = "";
            selectedKey.Visibility = selectedKey.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }, ready =>
        {
            var available = ready ? ReadOptions(row.OptionsFrom).Select(option => option.Value).ToHashSet() : [];
            foreach (var pair in keys) pair.Value.IsEnabled = available.Contains(pair.Key);
        });
    }

    private Action<JsonElement> EditFields(StackPanel controls, Field[] fields, ValueDefinition schema,
        Func<JsonElement, Task> save, Action missing)
    {
        var bindings = new List<(string Id, Func<JsonElement> Read, Action<JsonElement> Display)>();
        foreach (var field in fields)
        {
            var spec = schema.Properties[field.Id];
            Control input;
            Func<JsonElement> read;
            Action<JsonElement> display;
            if (field.Options is not null || field.OptionsFrom is not null)
            {
                var picker = new ComboBox { MinWidth = 125, PlaceholderText = field.Placeholder ?? "选择",
                    DisplayMemberPath = nameof(Option.Name) };
                input = picker;
                read = () => picker.SelectedItem is Option selected ? Element(selected.Value) : default;
                display = value =>
                {
                    var options = field.Options ?? ReadOptions(field.OptionsFrom);
                    if (picker.ItemsSource is not Option[] old || !old.SequenceEqual(options)) picker.ItemsSource = options;
                    picker.SelectedIndex = value.ValueKind == JsonValueKind.Number
                        ? Array.FindIndex(options, option => option.Value == value.GetInt32()) : -1;
                };
            }
            else if (spec.Type == "boolean")
            {
                var toggle = NativeSwitch();
                input = toggle;
                read = () => Element(toggle.IsOn);
                display = value => toggle.IsOn = value.ValueKind == JsonValueKind.True;
            }
            else if (field.Control == "color")
            {
                var color = new ColorPicker { IsAlphaEnabled = false };
                var swatch = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 16, Height = 16, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Center
                };
                var code = new TextBlock { Text = "选择颜色", VerticalAlignment = VerticalAlignment.Center };
                var preview = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                preview.Children.Add(swatch);
                preview.Children.Add(code);
                var button = new DropDownButton { Content = preview };
                input = button;
                string? hex = null;
                bool rendering = false;
                color.ColorChanged += (_, args) =>
                {
                    if (!rendering) SetColor(args.NewColor);
                };
                var selectColor = new Button { Content = "选用此颜色" };
                selectColor.Click += (_, _) =>
                {
                    SetColor(color.Color);
                    button.Flyout.Hide();
                    button.Focus(FocusState.Programmatic);
                };
                var flyoutContent = new StackPanel { Spacing = 8 };
                flyoutContent.Children.Add(color);
                flyoutContent.Children.Add(selectColor);
                button.Flyout = new Flyout { Content = flyoutContent };
                read = () => hex is null ? default : Element(hex);
                display = value =>
                {
                    rendering = true;
                    try
                    {
                        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        if (text is { Length: 7 } && text[0] == '#' && uint.TryParse(text.AsSpan(1),
                            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint rgb))
                        {
                            color.Color = Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                            SetColor(color.Color);
                        }
                        else
                        {
                            hex = null;
                            swatch.Fill = null;
                            code.Text = "选择颜色";
                        }
                    }
                    finally { rendering = false; }
                };

                void SetColor(Windows.UI.Color value)
                {
                    hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
                    swatch.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(value);
                    code.Text = hex;
                }
            }
            else if (spec.Type == "string")
            {
                var text = new TextBox { MaxLength = spec.MaxLength, MinWidth = 125, PlaceholderText = field.Placeholder ?? "" };
                input = text;
                read = () => string.IsNullOrWhiteSpace(text.Text) ? default : Element(text.Text.Trim());
                display = value => text.Text = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            }
            else
            {
                var number = NumberBox(spec.Minimum ?? 0, spec.Maximum ?? 65535, field.Step, value: double.NaN);
                number.PlaceholderText = field.Placeholder ?? "—";
                input = number;
                read = () =>
                {
                    if (!double.IsFinite(number.Value)) return default;
                    if (spec.Type != "integer") return Element(Math.Round(number.Value, 6));
                    return number.Value == Math.Truncate(number.Value) ? Element((int)number.Value) : default;
                };
                display = value => number.Value = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;
                if (field.Control == "slider")
                {
                    var slider = new Slider { Minimum = number.Minimum, Maximum = number.Maximum, MinWidth = 80,
                        SmallChange = field.Step, StepFrequency = field.Step, SnapsTo = SliderSnapsTo.StepValues };
                    var syncNumber = display;
                    bool updatingSlider = false;
                    number.ValueChanged += (_, args) =>
                    {
                        if (!double.IsFinite(args.NewValue)) return;
                        updatingSlider = true;
                        slider.Value = args.NewValue;
                        updatingSlider = false;
                    };
                    slider.ValueChanged += (_, args) =>
                    {
                        if (!updatingSlider) number.Value = args.NewValue;
                    };
                    display = value =>
                    {
                        syncNumber(value);
                        if (value.ValueKind == JsonValueKind.Number) return;
                        updatingSlider = true;
                        slider.Value = slider.Minimum;
                        updatingSlider = false;
                    };
                    AutomationProperties.SetName(slider, field.Label);
                    controls.Children.Add(new TextBlock { Text = field.Label, TextWrapping = TextWrapping.Wrap });
                    var range = new Grid { ColumnSpacing = 8 };
                    range.ColumnDefinitions.Add(new ColumnDefinition());
                    range.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    range.Children.Add(slider);
                    Grid.SetColumn(number, 1);
                    range.Children.Add(number);
                    AutomationProperties.SetName(number, field.Label);
                    controls.Children.Add(range);
                    bindings.Add((field.Id, read, display));
                    continue;
                }
            }
            var name = new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetLabeledBy(input, name);
            var line = new Grid { ColumnSpacing = 12, MinHeight = 32 };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(input, 1);
            line.Children.Add(name);
            line.Children.Add(input);
            controls.Children.Add(line);
            bindings.Add((field.Id, read, display));
        }
        var apply = PageLayout.AccentButton("应用"); apply.HorizontalAlignment = HorizontalAlignment.Right;
        controls.Children.Add(apply);
        apply.Click += async (_, _) =>
        {
            var values = bindings.ToDictionary(field => field.Id, field => field.Read(), StringComparer.Ordinal);
            if (values.Values.Any(value => value.ValueKind == JsonValueKind.Undefined))
            {
                missing();
                return;
            }
            await save(Element(new[] { values }));
        };
        return value =>
        {
            var current = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1 ? value[0] : default;
            foreach (var field in bindings)
                field.Display(current.ValueKind == JsonValueKind.Object ? current.GetProperty(field.Id) : default);
        };
    }

    private static Action Debounce(FrameworkElement owner, Func<Task> apply)
    {
        var timer = owner.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(260);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) => await apply();
        owner.Unloaded += (_, _) => timer.Stop();
        return () => { timer.Stop(); timer.Start(); };
    }

    private static Action<JsonElement> AxisTable(StackPanel controls, Func<JsonElement, Task> save, int step, ValueDefinition schema)
    {
        var axes = schema.Items?.Properties;
        var xAxisSchema = axes?.GetValueOrDefault("x");
        var yAxisSchema = axes?.GetValueOrDefault("y");
        int minimum = (int)Math.Ceiling(Math.Max(xAxisSchema?.Items?.Minimum ?? 50, yAxisSchema?.Items?.Minimum ?? 50));
        int maximum = (int)Math.Floor(Math.Min(45000, Math.Min(xAxisSchema?.Items?.Maximum ?? 45000, yAxisSchema?.Items?.Maximum ?? 45000)));
        int stageLimit = Math.Min(7, Math.Min(xAxisSchema?.MaxItems ?? 7, yAxisSchema?.MaxItems ?? 7));
        int[] x = [], y = [];
        int selected = 0;
        bool rendering = false;
        var stageGrid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        var stageButtons = new ToggleButton[stageLimit];
        for (int i = 0; i < stageButtons.Length; i++)
        {
            int stage = i + 1;
            var button = new ToggleButton
            {
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                MinWidth = 0, Padding = new Thickness(4, 5, 4, 6), Visibility = Visibility.Collapsed
            };
            stageButtons[i] = button;
            stageGrid.Children.Add(button);
            button.Click += async (_, _) =>
            {
                button.IsChecked = selected == stage;
                if (selected == stage) return;
                await SaveTable(x, y, stage);
            };
        }
        var add = new Button { Content = new SymbolIcon(Symbol.Add), MinWidth = 0, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed };
        var remove = new Button { Content = new SymbolIcon(Symbol.Delete), Width = 32, Padding = new Thickness(0),
            Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(add, "添加 DPI 档位");
        AutomationProperties.SetName(remove, "删除当前 DPI 档位");
        ToolTipService.SetToolTip(add, "添加 DPI 档位");
        ToolTipService.SetToolTip(remove, "删除当前 DPI 档位");
        stageGrid.Children.Add(add);
        stageGrid.Children.Add(remove);
        FrameworkElement[] stageControls = [.. stageButtons, add, remove];
        stageGrid.SizeChanged += (_, args) => ReflowStages(args.NewSize.Width);
        void ReflowStages(double width)
        {
            const double minimumButtonWidth = 56;
            double spacing = stageGrid.ColumnSpacing;
            int previousColumns = width < 340 ? 2 : 4;
            double previousWidth = (width - (previousColumns - 1) * spacing) / previousColumns;
            var buttonWidth = new GridLength(Math.Max(minimumButtonWidth, previousWidth / 3));
            int columns = Math.Clamp((int)((width + spacing) / (buttonWidth.Value + spacing)), 1, stageControls.Length);
            while (stageGrid.ColumnDefinitions.Count < columns)
                stageGrid.ColumnDefinitions.Add(new ColumnDefinition());
            while (stageGrid.ColumnDefinitions.Count > columns)
                stageGrid.ColumnDefinitions.RemoveAt(stageGrid.ColumnDefinitions.Count - 1);
            foreach (var column in stageGrid.ColumnDefinitions)
                column.Width = buttonWidth;

            int active = 0;
            foreach (var control in stageControls)
            {
                if (control.Visibility != Visibility.Visible) continue;
                int row = active / columns;
                while (stageGrid.RowDefinitions.Count <= row)
                    stageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(control, row);
                Grid.SetColumn(control, active % columns);
                active++;
            }
            int rows = (active + columns - 1) / columns;
            while (stageGrid.RowDefinitions.Count > rows)
                stageGrid.RowDefinitions.RemoveAt(stageGrid.RowDefinitions.Count - 1);
        }
        controls.Children.Add(stageGrid);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var independent = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(independent, "独立设置 X／Y 轴");
        actions.Children.Add(new TextBlock { Text = "独立 X／Y 轴", VerticalAlignment = VerticalAlignment.Center });
        actions.Children.Add(independent);
        var heading = new Grid { ColumnSpacing = 10 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var limits = new TextBlock { Text = $"{minimum}–{maximum} DPI · 步进 {step}", VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap };
        heading.Children.Add(limits);
        Grid.SetColumn(actions, 1);
        heading.Children.Add(actions);
        controls.Children.Add(heading);

        var xValue = NumberBox(minimum, maximum, step);
        var yValue = NumberBox(minimum, maximum, step);
        var commit = Debounce(controls, SaveAxes);
        var xAxis = AddAxis("X 轴", xValue);
        var yAxis = AddAxis("Y 轴", yValue);
        controls.Children.Add(xAxis);
        controls.Children.Add(yAxis);

        Grid AddAxis(string axis, NumberBox entry)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var slider = new Slider { Minimum = minimum, Maximum = maximum, StepFrequency = step,
                SmallChange = step, SnapsTo = SliderSnapsTo.StepValues,
                VerticalAlignment = VerticalAlignment.Center };
            bool dragging = false;
            AutomationProperties.SetName(slider, axis + " DPI 滑块");
            AutomationProperties.SetName(entry, axis + " DPI 数值");
            entry.MinWidth = 105;
            Grid.SetColumn(entry, 1);
            row.Children.Add(slider);
            row.Children.Add(entry);
            entry.ValueChanged += (_, args) =>
            {
                if (!double.IsFinite(args.NewValue)) return;
                bool previous = rendering;
                rendering = true;
                slider.Value = args.NewValue;
                rendering = previous;
                if (!rendering && !dragging) commit();
            };
            slider.ValueChanged += (_, args) =>
            {
                if (rendering) return;
                entry.Value = args.NewValue;
            };
            slider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => dragging = true), true);
            slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag()), true);
            slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag()), true);
            void EndDrag()
            {
                if (!dragging) return;
                dragging = false;
                if (!rendering) commit();
            }
            return row;
        }

        independent.Toggled += async (_, _) =>
        {
            yAxis.Visibility = independent.IsOn ? Visibility.Visible : Visibility.Collapsed;
            if (!rendering && !independent.IsOn) await SaveAxes();
        };
        add.Click += async (_, _) =>
        {
            if (selected <= 0 || x.Length >= stageLimit) return;
            await SaveTable([.. x, x[selected - 1]], [.. y, y[selected - 1]], x.Length + 1);
        };
        remove.Click += async (_, _) =>
        {
            if (selected <= 0 || x.Length <= 1) return;
            await SaveTable(x.Where((_, index) => index != selected - 1).ToArray(),
                y.Where((_, index) => index != selected - 1).ToArray(), Math.Min(selected, x.Length - 1));
        };

        async Task SaveAxes()
        {
            double newY = independent.IsOn ? yValue.Value : xValue.Value;
            if (selected <= 0 || !double.IsFinite(xValue.Value) || !double.IsFinite(newY)) return;
            int[] newX = (int[])x.Clone(), newYs = (int[])y.Clone();
            newX[selected - 1] = Snap(xValue.Value, x[selected - 1]);
            newYs[selected - 1] = Snap(newY, y[selected - 1]);
            await SaveTable(newX, newYs, selected);
        }
        int Snap(double value, int original) => value == original ? original : Math.Min(maximum, minimum
            + (int)Math.Round((value - minimum) / step, MidpointRounding.AwayFromZero) * step);
        Task SaveTable(int[] newX, int[] newY, int stage) =>
            save(Element(new[] { new { x = newX, y = newY, stage } }));

        return value =>
        {
            rendering = true;
            try
            {
                bool available = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1;
                var state = available ? value[0] : default;
                x = available ? state.GetProperty("x").Deserialize<int[]>()! : [];
                y = available ? state.GetProperty("y").Deserialize<int[]>()! : [];
                selected = available ? state.GetProperty("stage").GetInt32() : 0;
                if (x.Length != y.Length || selected > x.Length) selected = 0;
                for (int i = 0; i < stageButtons.Length; i++)
                {
                    var button = stageButtons[i];
                    button.Visibility = i < Math.Min(x.Length, y.Length) ? Visibility.Visible : Visibility.Collapsed;
                    if (i >= Math.Min(x.Length, y.Length)) continue;
                    string dpi = x[i] == y[i] ? $"{x[i]}" : $"{x[i]} / {y[i]}";
                    ((TextBlock)button.Content).Text = $"{i + 1} · {dpi}";
                    AutomationProperties.SetName(button, $"DPI 档位 {i + 1}，{dpi}");
                    button.IsChecked = i + 1 == selected;
                }
                xValue.Value = selected > 0 ? x[selected - 1] : double.NaN;
                yValue.Value = selected > 0 ? y[selected - 1] : double.NaN;
                independent.IsOn = selected > 0 && xValue.Value != yValue.Value || independent.IsOn && selected > 0;
                yAxis.Visibility = independent.IsOn ? Visibility.Visible : Visibility.Collapsed;
                add.Visibility = selected > 0 && x.Length < stageLimit ? Visibility.Visible : Visibility.Collapsed;
                remove.Visibility = x.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
                ReflowStages(stageGrid.ActualWidth);
            }
            finally { rendering = false; }
        };
    }

    private static Action<JsonElement> KeyBinding(StackPanel controls, Func<JsonElement, Task> save)
    {
        string[] kinds = ["mouse", "dpi", "consumer", "keyboard", "combination", "disabled"];
        string[] names = ["常规", "DPI", "多媒体", "键盘", "组合键", "禁用"];
        var tabs = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        var choices = new ComboBox { PlaceholderText = "选择按键", DisplayMemberPath = nameof(Option.Name) };
        var modifiers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var keys = new[] { "Ctrl", "Shift", "Alt", "Win" }.Select(text => new CheckBox { Content = text, MinWidth = 0 }).ToArray();
        foreach (var key in keys) modifiers.Children.Add(key);
        var actions = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int i = 0; i < 3; i++)
        {
            tabs.ColumnDefinitions.Add(new ColumnDefinition());
            actions.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int i = 0; i < 2; i++) tabs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.Children.Add(tabs);
        controls.Children.Add(modifiers);
        controls.Children.Add(actions);
        controls.Children.Add(choices);
        int category = -1, committedCategory = -1, committedCode = -1;
        bool updating = false;
        Option[] keyboard = [.. Enumerable.Range(4, 26).Select(code => new Option(code, ((char)('A' + code - 4)).ToString())),
            .. Enumerable.Range(30, 10).Select(code => new Option(code, ((code - 29) % 10).ToString())),
            new(40, "Enter"), new(41, "Esc"), new(42, "Backspace"), new(43, "Tab"), new(44, "Space"),
            .. Enumerable.Range(58, 12).Select(code => new Option(code, $"F{code - 57}")),
            new(76, "Delete"), new(79, "→"), new(80, "←"), new(81, "↓"), new(82, "↑")];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var tab = PageLayout.Tab(names[i], compact: true);
            tab.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(tab, i % 3);
            Grid.SetRow(tab, i / 3);
            tab.Click += (_, _) => ShowCategory(index);
            tabs.Children.Add(tab);
        }

        void ShowCategory(int index, bool force = false)
        {
            for (int i = 0; i < tabs.Children.Count; i++)
                ((ToggleButton)tabs.Children[i]).IsChecked = i == index;
            if (category == index && !force) return;
            category = index;
            bool useList = index is 3 or 4;
            modifiers.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
            choices.Visibility = useList ? Visibility.Visible : Visibility.Collapsed;
            actions.Visibility = index >= 0 && !useList ? Visibility.Visible : Visibility.Collapsed;
            actions.Children.Clear();
            actions.RowDefinitions.Clear();
            if (index < 0) return;
            Option[] options = index switch
            {
                0 => [new(1, "左键"), new(4, "中键"), new(2, "右键"), new(16, "前进"), new(8, "后退"), new(10, "DIY 键")],
                1 => [new(3, "DPI 循环＋"), new(4, "DPI 循环－")],
                2 => [new(182, "上一曲"), new(181, "下一曲"), new(205, "播放／暂停"),
                    new(183, "停止"), new(233, "音量加"), new(234, "音量减"), new(226, "静音")],
                5 => [new(0, "禁用按键")],
                _ => keyboard
            };
            if (committedCategory == index && committedCode >= 0 && options.All(option => option.Value != committedCode))
                options = [.. options, new(committedCode, $"键码 {committedCode}")];
            bool previous = updating;
            updating = true;
            if (useList)
            {
                choices.ItemsSource = options;
                choices.SelectedIndex = index == committedCategory ? Array.FindIndex(options, option => option.Value == committedCode) : -1;
            }
            else for (int i = 0; i < options.Length; i++)
            {
                Option option = options[i];
                if (i % 3 == 0) actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var button = new ToggleButton { Content = option.Name, HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsChecked = index == committedCategory && option.Value == committedCode };
                Grid.SetColumn(button, i % 3);
                Grid.SetRow(button, i / 3);
                button.Click += async (_, _) =>
                {
                    button.IsChecked = index == committedCategory && option.Value == committedCode;
                    await SaveAction(option);
                };
                actions.Children.Add(button);
            }
            updating = previous;
        }

        async Task SaveAction(Option option)
        {
            if (updating || category < 0) return;
            int mask = 0;
            if (category == 4)
            {
                for (int i = 0; i < keys.Length; i++) if (keys[i].IsChecked == true) mask |= 1 << i;
                if (mask == 0) return;
            }
            await save(Element(new[] { new { kind = kinds[category], code = option.Value, modifiers = mask } }));
        }
        choices.SelectionChanged += async (_, _) =>
        {
            if (choices.SelectedItem is Option option) await SaveAction(option);
        };
        foreach (var key in keys)
            key.Click += async (_, _) =>
            {
                if (choices.SelectedItem is Option option) await SaveAction(option);
            };
        return value =>
        {
            bool available = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1;
            var binding = available ? value[0] : default;
            committedCategory = available ? Array.IndexOf(kinds, binding.GetProperty("kind").GetString()) : -1;
            committedCode = available ? binding.GetProperty("code").GetInt32() : -1;
            int mask = available ? binding.GetProperty("modifiers").GetInt32() : 0;
            for (int i = 0; i < keys.Length; i++) keys[i].IsChecked = (mask & (1 << i)) != 0;
            ShowCategory(committedCategory, force: true);
        };
    }

    private static Button SaveButton(StackPanel host, Control input)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(input);
        var button = new Button { Content = "保存", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        row.Children.Add(button);
        host.Children.Add(row);
        return button;
    }

    private static NumberBox NumberBox(double min, double max, double step, string? header = null, double value = double.NaN) =>
        new() { Header = header, Minimum = min, Maximum = max, Value = value, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = step, MinWidth = 125 };

    private static ToggleSwitch NativeSwitch() => new()
    {
        OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center
    };

    private static void SetControlsEnabled(Panel panel, bool enabled)
    {
        foreach (var element in panel.Children)
        {
            if (element is Control control) control.IsEnabled = enabled;
            else if (element is Panel child) SetControlsEnabled(child, enabled);
        }
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, PluginApi.Json);
}
