using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hhub.Desktop;

internal static class HostReleaseFeed
{
    private const string Repository = "shengerq/HHub";
    private const string AssetName = "HHub-Host-win-x64.zip";
    private const string VersionIndex = "https://raw.githubusercontent.com/shengerq/HHub/main/updates.json";
    public static string CurrentVersion { get; } = typeof(HostReleaseFeed).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static string ReadCurrentNotes()
    {
        using var stream = typeof(HostReleaseFeed).Assembly.GetManifestResourceStream("HHub.ReleaseNotes.txt");
        if (stream is null) return "该版本没有随附更新日志。";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    internal sealed class RateLimited(DateTimeOffset retryAt) : HttpRequestException(
        "GitHub Releases request was rate limited.", null, System.Net.HttpStatusCode.TooManyRequests)
    {
        public DateTimeOffset RetryAt { get; } = retryAt;
    }

    public sealed record Release(string Tag, string Notes, long Size, long AssetId, string Digest, bool Installer = false);

    public static async Task<Release?> CheckAsync(string channel, CancellationToken cancellationToken)
    {
        using var request = Request(new(VersionIndex), "application/json");
        using var response = await Client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            bool exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var quota) && quota.Contains("0");
            if (status == System.Net.HttpStatusCode.TooManyRequests || status == System.Net.HttpStatusCode.Forbidden
                && (exhausted || response.Headers.RetryAfter is not null))
            {
                var now = DateTimeOffset.UtcNow;
                var retryAt = response.Headers.RetryAfter?.Date ?? now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                if (exhausted && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
                    && long.TryParse(reset.FirstOrDefault(), out long seconds) && seconds > 0 && seconds <= 253402300799L)
                {
                    var quotaReset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    if (quotaReset > now && (response.Headers.RetryAfter is null || quotaReset > retryAt)) retryAt = quotaReset;
                }
                throw new RateLimited(retryAt > now ? retryAt : now.AddMinutes(1));
            }
            throw new HttpRequestException("HHub version index request failed.", null, status);
        }
        using var releases = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        return SelectRelease(releases.RootElement, CurrentVersion, channel == "preview");
    }

    public static async Task DownloadAsync(Release release, string destination, CancellationToken cancellationToken)
    {
        string name = release.Installer ? $"HHub-{release.Tag.TrimStart('v')}-Setup-x64.exe" : AssetName;
        using var request = Request(new($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(release.Tag)}/{name}"),
            "application/octet-stream");
        using var origin = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)origin.StatusCode == 302)
        {
            if (origin.Headers.Location is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } redirect
                || redirect.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Invalid release redirect.");
            using var download = Request(redirect, "application/octet-stream");
            using var response = await Client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await SaveVerifiedAsync(response, release, destination, cancellationToken);
        }
        else await SaveVerifiedAsync(origin, release, destination, cancellationToken);
    }

    internal static async Task SaveVerifiedAsync(HttpResponseMessage response, Release release, string destination, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long size && size != release.Size)
            throw new InvalidDataException("Unexpected package size.");
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long received = 0;
                int length;
                while ((length = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += length;
                    if (received > release.Size) throw new InvalidDataException("Unexpected package size.");
                    hash.AppendData(buffer.AsSpan(0, length));
                    await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
                }
                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Release package verification failed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        finally { File.Delete(temporary); }
    }

    private static HttpRequestMessage Request(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"HHub/{CurrentVersion}");
        request.Headers.Accept.ParseAdd(accept);
        return request;
    }

    internal static Release? SelectRelease(JsonElement releases, string current, bool includePreview)
    {
        if (!TryVersion(current, out var installed) || releases.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid release data.");
        Release? newest = null;
        var newestVersion = installed;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || Flag(release, "draft") || Text(release, "tag_name") is not { } tag
                || !TryVersion(tag, out var version) || !includePreview && (Flag(release, "prerelease") || !version.Stable)
                || version.CompareTo(newestVersion) <= 0 || !release.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array) continue;
            string installerName = $"HHub-{tag.TrimStart('v')}-Setup-x64.exe";
            foreach (var asset in assets.EnumerateArray().OrderByDescending(item =>
                item.ValueKind == JsonValueKind.Object && Text(item, "name") == installerName))
            {
                if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") is not { } name
                    || (name != AssetName && name != installerName) || Text(asset, "state") != "uploaded"
                    || !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                    || !size.TryGetInt64(out long bytes) || bytes is <= 0 or > 536870912
                    || !asset.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
                    || !id.TryGetInt64(out long assetId) || assetId <= 0) continue;
                string notes = Text(release, "body") ?? "";
                string? digest = Text(asset, "digest");
                if (digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                    || !digest.AsSpan(7).ToArray().All(Uri.IsHexDigit)) continue;
                notes = Regex.Replace(notes, @"(?:https?://|www\.|github\.com/)[^\s<>]+", "", RegexOptions.IgnoreCase)
                    .Replace(Repository, "HHub", StringComparison.OrdinalIgnoreCase).Trim();
                newest = new(tag, notes.Length > 8000 ? notes[..8000] + "…" : notes, bytes, assetId, digest[7..], name == installerName);
                newestVersion = version;
                break;
            }
        }
        return newest;
    }

    private static string? Text(JsonElement data, string property) => data.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement data, string property) => data.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static bool TryVersion(string text, out (Version Number, bool Stable, int Preview) result)
    {
        result = default;
        if (text.StartsWith('v')) text = text[1..];
        var parts = text.Split('-', 2);
        if (!Version.TryParse(parts[0], out var version) || version.Build < 0 || version.Revision >= 0) return false;
        if (parts.Length == 1) { result = (version, true, 0); return true; }
        const string prefix = "preview.";
        if (!parts[1].StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(parts[1].AsSpan(prefix.Length),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int preview)) return false;
        result = (version, false, preview);
        return true;
    }
}
