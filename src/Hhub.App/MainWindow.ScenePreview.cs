using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private readonly bool _scenePreview = Environment.GetCommandLineArgs().Contains("--scene-preview");
    private int _previewScene;
    private readonly List<int> _previewConfigurations = [0, 1, 2];
    private readonly List<string> _previewSceneNames = ["日常", "游戏", "音乐"];

    private void RenderScenePreview()
    {
        _title.Text = "场景 · 预览";
        var scenes = _previewSceneNames;
        (string Name, int Dpi, int Polling)[] presets = [("办公", 800, 500), ("FPS", 1600, 1000), ("高速", 3200, 1000)];
        var page = new StackPanel { Spacing = 20 };
        var rail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new(6) };
        for (int i = 0; i < scenes.Count; i++)
        {
            int index = i;
            bool selected = index == _previewScene;
            var button = Button("", () => { _previewScene = index; Render(); });
            button.Content = SceneTileContent(scenes[i]);
            button.Width = SceneTileSize; button.Height = ShellLayout.SceneTileHeight;
            button.Padding = new(16);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.VerticalContentAlignment = VerticalAlignment.Stretch;
            var framed = FrameSceneTile(button);
            SetSceneTileAppearance(button, selected);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, scenes[i]);
            rail.Children.Add(framed);
        }
        rail.Children.Add(CreateAddSceneButton());
        page.Children.Add(new ScrollViewer
        {
            Content = rail, Margin = new(-6, 0, -6, 0), HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        page.Children.Add(Text("设备", SectionTextSize, true));

        var device = new StackPanel { Spacing = 16 };
        var header = new Grid { ColumnSpacing = 16, MinHeight = 56 };
        header.ColumnDefinitions.Add(new() { Width = new(40) });
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(Icon("\uE962", 28));
        var identity = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(Text("模拟无线鼠标", 14, true));
        var connection = Text("鼠标 · 2.4 GHz · 82%", 13); connection.Foreground = Muted; identity.Children.Add(connection);
        Grid.SetColumn(identity, 1); header.Children.Add(identity);
        var choice = new ComboBox
        {
            ItemsSource = presets.Select(preset => preset.Name).ToArray(),
            SelectedIndex = _previewConfigurations[_previewScene], Width = 128,
            VerticalAlignment = VerticalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choice, "当前场景的鼠标配置");
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex < 0 || choice.SelectedIndex == _previewConfigurations[_previewScene]) return;
            _previewConfigurations[_previewScene] = choice.SelectedIndex; Render();
        };
        Grid.SetColumn(choice, 2); header.Children.Add(choice);
        device.Children.Add(header);
        var configurations = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < presets.Length; i++)
        {
            int index = i;
            var preset = presets[i];
            configurations.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var details = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(Text(preset.Name, 14, true));
            var parameters = Text($"{preset.Dpi} DPI · {preset.Polling} Hz", 13); parameters.Foreground = Muted;
            details.Children.Add(parameters);
            var button = Button("", () => { _previewConfigurations[_previewScene] = index; Render(); });
            button.Content = details; button.Height = 88; button.Padding = new(16, 12, 16, 12);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            bool selected = index == _previewConfigurations[_previewScene];
            SetSceneTileAppearance(button, selected);
            if (selected) parameters.ClearValue(TextBlock.ForegroundProperty);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{preset.Name}，{preset.Dpi} DPI，{preset.Polling} Hz");
            Grid.SetColumn(button, i); configurations.Children.Add(button);
        }
        device.Children.Add(configurations);
        page.Children.Add(Card(device, padding: new(20)));
        _content.Children.Add(Scroll(page));
    }
}
