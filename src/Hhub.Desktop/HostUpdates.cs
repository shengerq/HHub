using System.Net;
using System.Net.Http;

namespace Hhub.Desktop;

internal sealed class HostUpdates : IDisposable
{
    private CancellationTokenSource? _request;
    private CancellationTokenSource? _download;
    private DateTimeOffset _retryAt;
    public bool Checking { get; private set; }
    public string Status { get; private set; } = "尚未检查更新";
    public string DownloadStatus { get; private set; } = "";
    public bool Downloading => _download is not null;
    public HostReleaseFeed.Release? Offer { get; private set; }
    public event Action? Changed;

    public async Task CheckAsync(string channel)
    {
        if (Downloading || Checking) return;
        if (_retryAt > DateTimeOffset.UtcNow)
        {
            Status = RateLimitStatus(_retryAt);
            Changed?.Invoke();
            return;
        }
        _request?.Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        Checking = true;
        Status = "正在检查更新…";
        DownloadStatus = "";
        Offer = null;
        Changed?.Invoke();
        try
        {
            var release = await HostReleaseFeed.CheckAsync(channel, request.Token);
            if (request.IsCancellationRequested) return;
            Offer = release;
            Status = release is null ? "当前渠道没有更新的 Windows x64 版本" : $"可更新到 {release.Tag.TrimStart('v')}";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (HostReleaseFeed.RateLimited error)
        {
            if (request.IsCancellationRequested) return;
            _retryAt = error.RetryAt;
            Status = RateLimitStatus(_retryAt);
        }
        catch (Exception error)
        {
            if (request.IsCancellationRequested) return;
            Status = error is HttpRequestException http ? http.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "暂时无法读取更新，请稍后重试。",
                HttpStatusCode.TooManyRequests => "更新查询已达到限额，请稍后重试。",
                _ => "无法检查更新，请检查网络后重试。"
            } : error is OperationCanceledException ? "检查更新超时，请重试。" : "暂时无法读取更新信息。";
        }
        finally
        {
            if (ReferenceEquals(_request, request))
            {
                _request = null;
                Checking = false;
                Changed?.Invoke();
            }
        }
    }

    private static string RateLimitStatus(DateTimeOffset retryAt) =>
        $"更新服务请求额度已用完，可在 {retryAt.LocalDateTime:HH:mm} 后重试。";

    public void Reset()
    {
        _request?.Cancel();
        _request = null;
        Checking = false;
        Offer = null;
        Status = "尚未检查更新";
        DownloadStatus = "";
        Changed?.Invoke();
    }

    public async Task DownloadAsync(HostReleaseFeed.Release release, string directory)
    {
        if (Downloading || release != Offer) return;
        using var download = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _download = download;
        DownloadStatus = "正在下载并校验…";
        Changed?.Invoke();
        try
        {
            string version = release.Tag.TrimStart('v');
            string name = release.Installer ? $"HHub-{version}-Setup-x64" : $"HHub-Host-{version}-win-x64";
            string extension = release.Installer ? ".exe" : ".zip";
            string target = Path.Combine(directory, name + extension);
            for (int number = 2; File.Exists(target); number++)
                target = Path.Combine(directory, $"{name} ({number}){extension}");
            await HostReleaseFeed.DownloadAsync(release, target, download.Token);
            DownloadStatus = $"下载完成，已校验并保存为 {Path.GetFileName(target)}";
        }
        catch (InvalidDataException) { DownloadStatus = "下载包校验失败，请重试。"; }
        catch (OperationCanceledException) { DownloadStatus = "下载已取消或超时。"; }
        catch (Exception) { DownloadStatus = "无法下载更新包，请重试。"; }
        finally
        {
            _download = null;
            Changed?.Invoke();
        }
    }

    public void Dispose() { _request?.Cancel(); _request = null; _download?.Cancel(); Changed = null; }
}
