using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private async Task DeleteSceneAsync(SceneProfile profile)
    {
        if (_profiles.Count <= 1) { ShowError("至少保留一个场景"); return; }
        var dialog = Dialog("删除场景 · " + profile.Name, Text("同时移除该场景的应用规则和设备配置绑定。", BodyTextSize), "删除");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _sceneGate.WaitAsync();
        try
        {
            _profiles.RemoveAll(scene => scene.Id == profile.Id);
            _rules.RemoveAll(rule => rule.SceneId == profile.Id);
            _bindings.RemoveAll(binding => binding.SceneId == profile.Id);
            var fallback = _profiles.FirstOrDefault(scene => scene.Id == "daily") ?? _profiles[0];
            if (_preferences.CurrentSceneId == profile.Id)
                _preferences = _preferences with { CurrentSceneId = fallback.Id };
            if (_automaticSceneId == profile.Id) _automaticSceneId = null;
            _sceneActionAttempts.Clear(); _sceneErrors.Clear(); _appliedScenes.Clear();
            _requestedDeviceScenes.Clear(); _lastAppliedSceneId = null;
            _storage.Save("scene-profiles.json", _profiles);
            _storage.Save("scenes.json", _rules);
            _storage.Save("device-scenes.json", _bindings);
            SavePreferences();
            _sceneDetailId = null; _sceneApplicationsExpanded = false;
            _page = "scenes";
        }
        finally { _sceneGate.Release(); }
        await EvaluateScenesAsync();
        RefreshNavigation(); Render(); QueueActivation();
    }

    private async Task EditSceneAsync()
    {
        var available = _installed.Plugins.Where(plugin => plugin.Manifest.Transport == "stdio-v1")
            .SelectMany(plugin => plugin.Manifest.Commands.Select(command => (plugin, command))).ToArray();
        if (available.Length == 0) { ShowError("先安装具有配置动作的插件"); return; }
        var sceneChoice = new ComboBox { Header = "场景", ItemsSource = _profiles.Select(profile => profile.Name).ToArray(), SelectedIndex = Math.Max(0, _profiles.FindIndex(profile => profile.Id == CurrentScene?.Id)), HorizontalAlignment = HorizontalAlignment.Stretch };
        var actionChoice = new ComboBox { Header = "动作", ItemsSource = available.Select(item => $"{item.plugin.Manifest.Name} / {item.command.Name}").ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var values = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        CommandForm? form = null;
        var fields = new StackPanel { Spacing = 16, MinWidth = 380 };
        fields.Children.Add(sceneChoice); fields.Children.Add(actionChoice); fields.Children.Add(values);
        void LoadValues()
        {
            if (sceneChoice.SelectedIndex < 0 || actionChoice.SelectedIndex < 0) return;
            var selected = available[actionChoice.SelectedIndex];
            string resource = selected.command.ConflictResource ?? selected.command.Resource;
            var action = _profiles[sceneChoice.SelectedIndex].Actions.FirstOrDefault(action => action.PluginId == selected.plugin.Manifest.Id && action.Resource == resource);
            form = CreateCommandForm(selected.command, action?.Values);
            values.Content = form.View;
        }
        sceneChoice.SelectionChanged += (_, _) => LoadValues();
        actionChoice.SelectionChanged += (_, _) => LoadValues();
        LoadValues();
        Dictionary<string, JsonElement>? parameters = null;
        var dialog = Dialog("配置场景", fields, "保存动作");
        dialog.SecondaryButtonText = "移除此动作";
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { parameters = form!.Read(); }
            catch (InvalidDataException) { args.Cancel = true; }
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        int index = sceneChoice.SelectedIndex;
        var chosen = available[actionChoice.SelectedIndex];
        string resource = chosen.command.ConflictResource ?? chosen.command.Resource;
        var actions = _profiles[index].Actions.Where(action => action.PluginId != chosen.plugin.Manifest.Id || action.Resource != resource).ToList();
        if (result == ContentDialogResult.Primary) actions.Add(new(chosen.plugin.Manifest.Id, chosen.command.Id, resource, parameters!));
        _profiles[index] = _profiles[index] with { Actions = actions.ToArray() };
        _storage.Save("scene-profiles.json", _profiles);
        _sceneActionAttempts.Clear(); _sceneErrors.Clear();
        await EvaluateScenesAsync();
        Render();
    }
}
