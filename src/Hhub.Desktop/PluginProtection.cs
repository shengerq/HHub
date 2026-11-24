using System.Diagnostics;
using System.Runtime.InteropServices;
using Hhub.Core;
using Hhub.Desktop.Pages;
using Hhub.UnifiedHost;

namespace Hhub.Desktop;

internal sealed record PluginSecurityReport(string Plugin, string Version, string Reason, string Verdict,
    DateTimeOffset CheckedAt, string PackageHash, string Entry, bool Blocked);

internal static class PluginProtection
{
    private static readonly string Root = Path.Combine(PluginPreferences.Root, "plugin-security");
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private const long HighMemory = 512L * 1024 * 1024;
    private const int MaxScanBytes = 32 * 1024 * 1024;

    internal static void OpenReports()
    {
        Directory.CreateDirectory(Root);
        using var process = Process.Start(new ProcessStartInfo(Root) { UseShellExecute = true });
    }

    private static string ReportFile(string id) => id + ".json";

    public static PluginSecurityReport? Latest(string id)
    {
        try { return new AppStorage(Root).Load<PluginSecurityReport?>(ReportFile(id), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Trace.WriteLine(error); return null; }
    }

    internal static bool IsBlocked(InstalledPackage plugin) =>
        Latest(plugin.Definition.Id) is { Blocked: true } report && report.PackageHash == plugin.PackageHash;

    private static string EntryPoint(InstalledPackage plugin) =>
        Path.Combine(plugin.Directory, plugin.Definition.EntryPoint.Replace('/', Path.DirectorySeparatorChar));

    internal static async Task WatchAsync(Process process, InstalledPackage plugin, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            var lastCpu = process.TotalProcessorTime;
            long lastTick = Stopwatch.GetTimestamp();
            int highCpu = 0, highMemory = 0;
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                process.Refresh();
                if (process.HasExited) return;
                var cpu = process.TotalProcessorTime;
                long tick = Stopwatch.GetTimestamp();
                double cores = (cpu - lastCpu).TotalSeconds / Stopwatch.GetElapsedTime(lastTick, tick).TotalSeconds;
                lastCpu = cpu;
                lastTick = tick;
                highCpu = cores >= 0.85 ? highCpu + 1 : 0;
                highMemory = process.PrivateMemorySize64 >= HighMemory ? highMemory + 1 : 0;
                if (highCpu < 3 && highMemory < 3) continue;
                string reason = highCpu >= 3
                    ? $"CPU 持续占用约 {cores:0.0} 个逻辑核心"
                    : $"专用内存持续占用 {process.PrivateMemorySize64 / 1048576d:0} MiB";
                await ReportAsync(plugin, reason).ConfigureAwait(false);
                if (!token.IsCancellationRequested && IsBlocked(plugin) && !process.HasExited)
                    process.Kill(entireProcessTree: true);
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Trace.WriteLine(error); }
    }

    internal static async Task ReportAsync(InstalledPackage plugin, string reason)
    {
        await ScanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string id = plugin.Definition.Id;
            if (Latest(id) is { } recent && recent.PackageHash == plugin.PackageHash
                && DateTimeOffset.UtcNow - recent.CheckedAt < TimeSpan.FromMinutes(5)) return;
            var (verdict, blocked) = await Task.Run(() => Scan(EntryPoint(plugin))).ConfigureAwait(false);
            new AppStorage(Root).Save(ReportFile(id),
                new PluginSecurityReport(id, plugin.Definition.Version, reason, verdict, DateTimeOffset.UtcNow,
                    plugin.PackageHash, plugin.Definition.EntryPoint, blocked));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Trace.WriteLine(error); }
        finally { ScanGate.Release(); }
    }

    private static (string Verdict, bool Blocked) Scan(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length is <= 0 or > MaxScanBytes) return ("文件大小超出自动扫描范围", false);
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            if (AmsiInitialize("HHub Plugin Protection", out nint context) != 0) return ("防病毒接口不可用", false);
            try
            {
                if (AmsiScanBuffer(context, bytes, (uint)bytes.Length, path, 0, out int verdict) != 0)
                    return ("防病毒扫描不可用", false);
                return verdict switch
                {
                    >= 32768 => ("防病毒提供程序检出威胁", true),
                    >= 16384 and <= 20479 => ("防病毒管理员策略已阻止", true),
                    0 => ("防病毒提供程序返回安全", false),
                    1 => ("防病毒接口未返回检测结果", false),
                    _ => ("防病毒提供程序未给出明确结论", false)
                };
            }
            finally { AmsiUninitialize(context); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException
            or EntryPointNotFoundException)
        { Trace.WriteLine(error); return ("无法扫描插件入口文件", false); }
    }

    [DllImport("amsi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AmsiInitialize(string appName, out nint context);

    [DllImport("amsi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AmsiScanBuffer(nint context, byte[] buffer, uint length, string name, nint session, out int result);

    [DllImport("amsi.dll", ExactSpelling = true)]
    private static extern void AmsiUninitialize(nint context);
}
