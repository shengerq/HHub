using System.Text.Json;

namespace Hhub.Core;

public sealed record PluginNotice(string Id, string Title, string Message, Uri? HelpUrl, string? HelpText);

public static class PluginDiagnostics
{
    public static IReadOnlyList<PluginNotice> Read(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (!values.TryGetValue("diagnostics", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var result = new List<PluginNotice>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? Get(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            string? id = Get("id"), title = Get("title"), message = Get("message");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message)) continue;
            Uri? link = Uri.TryCreate(Get("helpUrl"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
            result.Add(new(id, title, message, link, Get("helpText")));
        }
        return result;
    }
}
