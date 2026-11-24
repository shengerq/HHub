using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private sealed class SceneCreationDraft
    {
        public string Name = "";
        public string PendingApplication = "";
        public HashSet<string> Applications = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Choices = [];
        public Dictionary<string, Dictionary<string, JsonElement>> Values = [];
        public int PreviewConfiguration;
    }

    private sealed record SceneDeviceOption(string Key, string Label, string PluginId, PluginCommand Command,
        Dictionary<string, JsonElement>? Values = null);

    private SceneCreationDraft? _sceneCreation;
    private Action? _captureSceneCreation;

    private void RenderSceneCreation()
    {
        var draft = _sceneCreation ??= new();
        _title.Text = "添加场景";
        void Cancel()
        {
            _captureSceneCreation = null; _sceneCreation = null; Navigate("scenes");
        }
        _actions.Children.Add(IconButton("\uE72B", "返回场景", Cancel));
        var page = new StackPanel { Spacing = 20 };
        var name = new TextBox { Header = "名称", Text = draft.Name, PlaceholderText = "场景名称", MaxLength = 40 };
        name.TextChanged += (_, _) => { draft.Name = name.Text; name.ClearValue(Control.BorderBrushProperty); };
        page.Children.Add(name);

        var applicationPanel = new StackPanel { Spacing = 8 };
        applicationPanel.Children.Add(Text("前台应用", 14, true));
        var app = new AutoSuggestBox { PlaceholderText = "选择或输入应用名称", Text = draft.PendingApplication };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(app, "前台应用");
        var applicationNames = (_scenePreview ? new[] { "notepad", "game", "music" }
            : PlatformServices.CaptureActivity().RunningApplications.AsEnumerable())
            .Select(SceneApplicationName).Where(application => application.Length > 0 && !draft.Applications.Contains(application))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(application => application, StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedApplications = new StackPanel { Spacing = 4 };
        void RefreshApplications()
        {
            selectedApplications.Children.Clear();
            foreach (string application in draft.Applications.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(Text(application));
                var remove = IconButton("\uE711", "移除 " + application, () =>
                {
                    draft.Applications.Remove(application); RefreshApplications();
                });
                Grid.SetColumn(remove, 1); row.Children.Add(remove);
                selectedApplications.Children.Add(row);
            }
        }
        void AddApplication(string value)
        {
            string application = SceneApplicationName(value);
            if (application.Length == 0) return;
            draft.Applications.Add(application); draft.PendingApplication = ""; app.Text = "";
            RefreshApplications();
        }
        app.GotFocus += (_, _) => app.ItemsSource = applicationNames.Where(value => !draft.Applications.Contains(value)).ToArray();
        app.TextChanged += (_, args) =>
        {
            draft.PendingApplication = app.Text;
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                app.ItemsSource = applicationNames.Where(value => !draft.Applications.Contains(value)
                    && value.Contains(app.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        };
        app.QuerySubmitted += (_, args) => AddApplication(args.ChosenSuggestion as string ?? args.QueryText);
        var applicationEntry = new Grid { ColumnSpacing = 8 };
        applicationEntry.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        applicationEntry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        applicationEntry.Children.Add(app);
        var addApplication = IconButton("\uE710", "添加前台应用", () => AddApplication(app.Text));
        Grid.SetColumn(addApplication, 1); applicationEntry.Children.Add(addApplication);
        applicationPanel.Children.Add(applicationEntry); applicationPanel.Children.Add(selectedApplications);
        RefreshApplications(); page.Children.Add(applicationPanel);

        var devices = new StackPanel { Spacing = 8 };
        devices.Children.Add(Text("设备配置", 14, true));
        var readers = new List<Func<DeviceSceneBinding?>>();
        var capture = new List<Action>();
        if (_scenePreview)
        {
            var configuration = new ComboBox
            {
                Header = "模拟无线鼠标", ItemsSource = new[] { "办公 · 800 DPI / 500 Hz", "FPS · 1600 DPI / 1000 Hz", "高速 · 3200 DPI / 1000 Hz" },
                SelectedIndex = draft.PreviewConfiguration, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            configuration.SelectionChanged += (_, _) => draft.PreviewConfiguration = Math.Max(0, configuration.SelectedIndex);
            devices.Children.Add(Card(configuration, padding: new(16)));
        }
        else
        {
            foreach (var device in _devices)
            {
                var available = DeviceConfigurationCommands(device);
                var options = new List<SceneDeviceOption>();
                foreach (var binding in _bindings.Where(binding => device.ContainsInstance(binding.DeviceId)))
                {
                    var item = available.FirstOrDefault(item => item.plugin.Manifest.Id == binding.PluginId && item.command.Id == binding.Command);
                    if (item.plugin is null) continue;
                    string sceneName = _profiles.FirstOrDefault(profile => profile.Id == binding.SceneId)?.Name ?? "已有配置";
                    options.Add(new(binding.SceneId + "/" + binding.PluginId + "/" + binding.Command,
                        sceneName + " · " + item.command.Name, binding.PluginId, item.command, binding.Values));
                }
                options.AddRange(available.Select(item => new SceneDeviceOption("new/" + item.plugin.Manifest.Id + "/" + item.command.Id,
                    "新建 · " + item.plugin.Manifest.Name + " / " + item.command.Name, item.plugin.Manifest.Id, item.command)));
                var fields = new StackPanel { Spacing = 12 };
                fields.Children.Add(Text(Describe(device).Name, 14, true));
                if (options.Count == 0)
                {
                    var empty = Text("暂无可绑定的配置", 13); empty.Foreground = Muted; fields.Children.Add(empty);
                    devices.Children.Add(Card(fields, padding: new(16))); continue;
                }
                int selected = draft.Choices.TryGetValue(device.InstanceId, out var key) ? options.FindIndex(option => option.Key == key) + 1 : 0;
                var choice = new ComboBox { ItemsSource = new[] { "不绑定" }.Concat(options.Select(option => option.Label)).ToArray(),
                    SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choice, Describe(device).Name + "的配置");
                var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
                CommandForm? form = null;
                SceneDeviceOption? chosen = null;
                string? valueKey = null;
                void Capture()
                {
                    if (form is null || valueKey is null) return;
                    try { draft.Values[valueKey] = form.Read(); } catch (InvalidDataException) { }
                }
                void Load()
                {
                    Capture();
                    chosen = choice.SelectedIndex > 0 ? options[choice.SelectedIndex - 1] : null;
                    draft.Choices[device.InstanceId] = chosen?.Key ?? "";
                    form = null; content.Content = null;
                    if (chosen is null) return;
                    valueKey = device.InstanceId + "/" + chosen.Key;
                    var initial = draft.Values.TryGetValue(valueKey, out var values) ? values : chosen.Values;
                    var provider = available.First(item => item.plugin.Manifest.Id == chosen.PluginId).plugin;
                    form = CreateDeviceConfigurationForm(device, provider, chosen.Command, initial);
                    content.Content = form.View;
                }
                choice.SelectionChanged += (_, _) => Load(); Load();
                capture.Add(Capture);
                readers.Add(() =>
                {
                    if (chosen is null || form is null) return null;
                    if (!_devices.Any(item => item.ContainsInstance(device.InstanceId))) throw new InvalidDataException("设备已断开：" + Describe(device).Name);
                    var values = form.Read(); values["deviceInstanceId"] = JsonSerializer.SerializeToElement(device.InstanceId);
                    return new(device.InstanceId, "", chosen.PluginId, chosen.Command.Id, values);
                });
                fields.Children.Add(choice); fields.Children.Add(content); devices.Children.Add(Card(fields, padding: new(16)));
            }
            if (_devices.Count == 0) { var empty = Text("暂无设备", 13); empty.Foreground = Muted; devices.Children.Add(empty); }
        }
        page.Children.Add(devices);
        _captureSceneCreation = () => { foreach (var action in capture) action(); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(AsyncButton("创建", async () =>
        {
            string title = name.Text.Trim();
            if (title.Length == 0) { name.BorderBrush = ErrorBrush; name.Focus(FocusState.Programmatic); return; }
            if (!string.IsNullOrWhiteSpace(app.Text)) AddApplication(app.Text);
            if (_scenePreview)
            {
                _previewSceneNames.Add(title); _previewConfigurations.Add(draft.PreviewConfiguration);
                _previewScene = _previewSceneNames.Count - 1;
            }
            else
            {
                var plan = SceneCreationPlan.Create(title, draft.Applications,
                    readers.Select(read => read()).OfType<DeviceSceneBinding>());
                await _sceneGate.WaitAsync();
                try
                {
                    _profiles.Add(plan.Profile);
                    _rules.RemoveAll(rule => rule.Condition == "foreground" && rule.SceneId != null
                        && draft.Applications.Contains(SceneApplicationName(rule.Application)));
                    _rules.AddRange(plan.Rules);
                    _bindings.AddRange(plan.Bindings);
                    _storage.Save("scene-profiles.json", _profiles);
                    _storage.Save("scenes.json", _rules);
                    _storage.Save("device-scenes.json", _bindings);
                }
                finally { _sceneGate.Release(); }
            }
            _captureSceneCreation = null; _sceneCreation = null; Navigate("scenes");
        }, true));
        actions.Children.Add(Button("取消", Cancel)); page.Children.Add(actions);
        _content.Children.Add(Scroll(page));
    }
}
