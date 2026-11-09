using System.Net.Http;
using System.Text.Json;

namespace Hhub.Core;

public sealed record MarketLoadResult(MarketPlugin[] Plugins, string[] Errors);
public sealed record MarketAddressEdit(bool Success, string[] Addresses, string? Error);

public static class PluginMarkets
{
    public static MarketAddressEdit EditAddress(IEnumerable<string> addresses, int? index, string? input)
    {
        string[] original = addresses.ToArray();
        try
        {
            if (index is { } slot && (slot < 0 || slot >= original.Length))
                throw new InvalidDataException("市场列表已更改，请重新操作。");
            string candidate = PluginMarket.Address(input?.Trim() ?? "").AbsoluteUri;
            if (original.Where((_, position) => position != index).Any(address => PluginMarket.Address(address).AbsoluteUri == candidate))
                throw new InvalidDataException("此市场网址已添加。");
            var changed = original.ToList();
            if (index is { } editIndex) changed[editIndex] = candidate; else changed.Add(candidate);
            return new(true, Normalize(changed), null);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        { return new(false, original, error.Message); }
    }

    public static string[] Configured(UserPreferences preferences) => preferences.PluginMarketUrls is { } addresses
        ? addresses.Where(address => !string.IsNullOrWhiteSpace(address)).Select(address => address.Trim()).Distinct(StringComparer.Ordinal).ToArray()
        : string.IsNullOrWhiteSpace(preferences.PluginMarketUrl) ? [] : [preferences.PluginMarketUrl.Trim()];

    public static string[] Normalize(IEnumerable<string> addresses)
    {
        var result = addresses.Select(address => PluginMarket.Address(address.Trim()).AbsoluteUri).Distinct(StringComparer.Ordinal).ToArray();
        if (result.Length > 32) throw new InvalidDataException("最多可添加 32 个插件市场。");
        return result;
    }

    public static async Task<MarketLoadResult> ReadAsync(IEnumerable<string> addresses)
    {
        string[] sources = Normalize(addresses);
        var results = await Task.WhenAll(sources.Select(async address =>
        {
            try { return new MarketLoadResult(await PluginMarket.ReadAsync(address).ConfigureAwait(false), []); }
            catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or JsonException or ArgumentException)
            { return new MarketLoadResult([], [address + "：" + error.Message]); }
        })).ConfigureAwait(false);
        // Source order is the tie breaker when two markets advertise the same version.
        var plugins = results.SelectMany(result => result.Plugins).GroupBy(plugin => plugin.Id)
            .Select(group => group.MaxBy(plugin => Version.Parse(plugin.Version))!).OrderBy(plugin => plugin.Name).ToArray();
        return new(plugins, results.SelectMany(result => result.Errors).ToArray());
    }
}
