using System.Net;
using System.Net.Http;
using Hhub.Core;

namespace Hhub.Desktop;

internal static class OfficialPluginLibrary
{
    internal const string Source = "https://raw.githubusercontent.com/shengerq/HHub-Plugins/main/catalog.json";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(30) };

    internal static bool IsOfficial(string source) => string.Equals(source, Source, StringComparison.Ordinal);

    internal static Task<MarketPlugin[]> ReadAsync(string source, CancellationToken token) =>
        PluginMarket.ReadAsync(source, token);

    internal static async Task DownloadAsync(MarketPlugin plugin, string path, CancellationToken token)
    {
        if (!IsOfficial(plugin.SourceUrl))
        {
            await PluginMarket.DownloadVerifiedArchiveAsync(plugin, path, token);
            return;
        }
        var uri = new Uri(plugin.PackageUrl);
        if (plugin.Bytes is not (> 0 and <= 268435456)
            || uri.Host != "github.com" || uri.Scheme != "https" || !uri.IsDefaultPort
            || !uri.AbsolutePath.StartsWith("/shengerq/HHub-Plugins/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("此插件缺少有效的官方安装包。");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(TimeSpan.FromMinutes(3));
        using var origin = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        var release = new HostReleaseFeed.Release(plugin.Version, "", plugin.Bytes.Value, plugin.AssetId ?? 0, plugin.Sha256);
        if (origin.StatusCode != HttpStatusCode.Found)
        {
            await HostReleaseFeed.SaveVerifiedAsync(origin, release, path, linked.Token);
            return;
        }
        if (origin.Headers.Location is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } redirect
            || redirect.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("插件下载地址无效。");
        using var payload = await Client.GetAsync(redirect, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        await HostReleaseFeed.SaveVerifiedAsync(payload, release, path, linked.Token);
    }
}
