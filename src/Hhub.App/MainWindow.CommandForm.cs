using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private sealed record CommandForm(FrameworkElement View, Func<Dictionary<string, JsonElement>> Read);

    private async Task ExecutePluginPageCommandAsync(InstalledPlugin plugin, string commandId)
    {
        var command = plugin.Manifest.Commands.FirstOrDefault(command => command.Id == commandId)
            ?? throw new InvalidOperationException("插件没有声明此命令");
        Dictionary<string, JsonElement> values = [];
        if (command.Parameters.Length > 0)
        {
            var form = CreateCommandForm(command);
            var dialog = Dialog(command.Name, form.View, "应用");
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { values = form.Read(); }
                catch (InvalidDataException) { args.Cancel = true; }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        if (_openDevice is { } device) values["deviceInstanceId"] = JsonSerializer.SerializeToElement(device.InstanceId);
        await SendPluginCommandAsync(plugin, commandId, values);
    }

    private CommandForm CreateCommandForm(PluginCommand command, IReadOnlyDictionary<string, JsonElement>? initial = null, bool showAdvanced = true)
    {
        initial ??= new Dictionary<string, JsonElement>();
        var fields = new StackPanel { Spacing = 14 };
        var readers = new Dictionary<string, Func<JsonElement>>();
        foreach (var parameter in command.Parameters)
        {
            JsonElement? value = initial.TryGetValue(parameter.Key, out var existing) ? existing : parameter.DefaultValue;
            switch (parameter.Type)
            {
                case "number":
                    var number = new NumberBox { Header = parameter.Name, Value = value?.ValueKind == JsonValueKind.Number ? value.Value.GetDouble() : double.NaN,
                        Minimum = parameter.Minimum ?? double.MinValue, Maximum = parameter.Maximum ?? double.MaxValue,
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
                    fields.Children.Add(number);
                    readers[parameter.Key] = () =>
                    {
                        if (!double.IsFinite(number.Value) || number.Value < number.Minimum || number.Value > number.Maximum)
                        { number.BorderBrush = ErrorBrush; throw new InvalidDataException("请填写有效的" + parameter.Name); }
                        return JsonSerializer.SerializeToElement(number.Value);
                    };
                    break;
                case "boolean":
                    var toggle = new ToggleSwitch { Header = parameter.Name, IsOn = value?.ValueKind == JsonValueKind.True };
                    fields.Children.Add(toggle); readers[parameter.Key] = () => JsonSerializer.SerializeToElement(toggle.IsOn);
                    break;
                case "string":
                    var text = new TextBox { Header = parameter.Name, Text = value?.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? "" : "" };
                    fields.Children.Add(text); readers[parameter.Key] = () => JsonSerializer.SerializeToElement(text.Text);
                    break;
                default: fields.Children.Add(Text("暂不支持此参数：" + parameter.Name)); break;
            }
        }
        var extras = initial.Where(pair => !readers.ContainsKey(pair.Key)).ToDictionary();
        var advanced = new TextBox { Text = JsonSerializer.Serialize(extras, JsonFormat.Options), AcceptsReturn = true, Height = 100 };
        var expander = new Expander { Header = "高级参数", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (showAdvanced) fields.Children.Add(expander);
        return new(fields, () =>
        {
            Dictionary<string, JsonElement>? result;
            try { result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(advanced.Text, JsonFormat.Options); }
            catch (JsonException) { result = null; }
            if (result is null)
            { expander.IsExpanded = true; advanced.BorderBrush = ErrorBrush; throw new InvalidDataException("高级参数格式不正确"); }
            foreach (var (key, read) in readers) result[key] = read();
            return result;
        });
    }
}
