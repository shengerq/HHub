using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hhub.Core;

public sealed record MarketPlugin(string Id, string Name, string Version, string PackageUrl, string Sha256, string Description = "")
{
    public string SourceUrl { get; init; } = "";
}

public static class PluginMarket
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    public static Uri Address(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("请填写有效的 HTTPS 市场索引地址（本机调试允许 HTTP）。");
        return uri;
    }

    public static MarketPlugin[] Parse(string json, Uri source)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema != 1
            || !root.TryGetProperty("plugins", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("市场地址未返回支持的插件索引（schemaVersion: 1、plugins 数组）。");
        var entries = items.Deserialize<MarketPlugin[]>(JsonFormat.Wire) ?? [];
        if (entries.Length > 2000) throw new InvalidDataException("市场插件数量过多。");
        var result = new List<MarketPlugin>();
        foreach (var item in entries)
        {
            if (item is null || !Regex.IsMatch(item.Id ?? "", "^[a-z0-9][a-z0-9.-]*$")
                || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 120 || !Version.TryParse(item.Version, out _)
                || !Regex.IsMatch(item.Sha256 ?? "", "^[a-fA-F0-9]{64}$") || string.IsNullOrWhiteSpace(item.PackageUrl))
                throw new InvalidDataException("市场插件的身份、版本、下载地址或 SHA-256 无效。");
            if (!Uri.TryCreate(source, item.PackageUrl, out var resolved)) throw new InvalidDataException("市场插件下载地址无效。");
            var url = Address(resolved.AbsoluteUri);
            result.Add(item with { PackageUrl = url.AbsoluteUri, Description = item.Description ?? "", SourceUrl = source.AbsoluteUri });
        }
        if (result.GroupBy(item => (item.Id, item.Version)).Any(group => group.Count() > 1))
            throw new InvalidDataException("市场中存在重复的插件版本。");
        return result.GroupBy(item => item.Id).Select(group => group.MaxBy(item => Version.Parse(item.Version))!).ToArray();
    }

    private static async Task Download(Uri uri, Stream destination, int limit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("下载内容超过大小限制。");
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        byte[] buffer = new byte[81920];
        int count, total = 0;
        while ((count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("下载内容超过大小限制。");
            await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
        }
    }

    public static async Task<MarketPlugin[]> ReadAsync(string address)
    {
        var uri = Address(address);
        using var data = new MemoryStream();
        await Download(uri, data, 2 * 1024 * 1024).ConfigureAwait(false);
        return Parse(System.Text.Encoding.UTF8.GetString(data.GetBuffer(), 0, (int)data.Length), uri);
    }

    public static async Task<LocalPluginPackage> DownloadPackageAsync(MarketPlugin entry, string path)
    {
        bool created = false;
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                created = true;
                await Download(Address(entry.PackageUrl), output, 256 * 1024 * 1024).ConfigureAwait(false);
            }
            var package = PluginCatalog.ReadPackage(path, entry.Sha256);
            var manifest = package.Manifest;
            if (manifest.Id != entry.Id || manifest.Version != entry.Version) throw new InvalidDataException("下载插件的身份或版本与市场索引不一致。");
            return package;
        }
        catch { if (created) File.Delete(path); throw; }
    }
}
