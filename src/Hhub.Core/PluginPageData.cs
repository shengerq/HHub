using System.Text.Json;

namespace Hhub.Core;

// Reusable declaration bindings. The host does not know any plugin's settings types.
public static class PluginPageData
{
    public static bool Equivalent(JsonElement left, JsonElement right) => left.ValueKind == right.ValueKind && (left.ValueKind switch
    {
        JsonValueKind.String => left.GetString() == right.GetString(),
        JsonValueKind.Number => left.GetDouble() == right.GetDouble(),
        _ => left.GetRawText() == right.GetRawText()
    });
    public static bool Boolean(IReadOnlyDictionary<string, JsonElement> values, string? key) => key is not null
        && values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;
    public static bool Enabled(PageElement element, IReadOnlyDictionary<string, JsonElement> values, bool fresh) => fresh
        && (element.EnabledKey is null || Boolean(values, element.EnabledKey))
        && (element.DisabledKey is null || !Boolean(values, element.DisabledKey));
    public static Dictionary<string, JsonElement> Arguments(PageElement element, object? value = null, JsonElement? index = null)
    {
        var arguments = new Dictionary<string, JsonElement>(element.Values);
        if (value is not null) arguments[element.Parameter] = JsonSerializer.SerializeToElement(value, JsonFormat.Wire);
        if (index is { } item) arguments[element.IndexParameter] = item.Clone();
        return arguments;
    }
    public static void Validate(PluginManifest manifest)
    {
        var pages = new[] { manifest.Page, manifest.SettingsPage }.OfType<PluginPage>();
        int count = 0;
        void Visit(PageElement element, int depth)
        {
            if (++count > 512 || depth > 12) throw new InvalidDataException("插件页面声明过大");
            if (element is null || element.Options.Any(option => option is null))
                throw new InvalidDataException("插件页面数组包含空元素");
            if (element.Type == "command" && element.Command is null)
                throw new InvalidDataException("插件页面引用了未声明的命令");
            foreach (string? command in new[] { element.Command, element.SecondaryCommand, element.OffCommand })
                if (command is not null && !manifest.Commands.Any(c => c.Id == command)) throw new InvalidDataException("插件页面引用了未声明的命令");
            if (element.Type is "slider" or "slider-bank")
                if (!double.IsFinite(element.Minimum) || !double.IsFinite(element.Maximum) || !double.IsFinite(element.Step)
                    || element.Maximum <= element.Minimum || element.Step <= 0) throw new InvalidDataException("插件滑块范围无效");
            if (!double.IsFinite(element.Width) || element.Width is < 0 or > 2000) throw new InvalidDataException("插件控件宽度无效");
            if (!double.IsFinite(element.Height) || element.Height is < 0 or > 2000) throw new InvalidDataException("插件控件高度无效");
            foreach (var child in element.Children) Visit(child, depth + 1);
        }
        foreach (var page in pages)
        {
            if (page.Layout is not ("tiles" or "declarative")) throw new InvalidDataException("不支持的插件页面布局");
            foreach (var element in page.HeaderElements.Concat(page.Elements)) Visit(element, 0);
        }
    }
}
