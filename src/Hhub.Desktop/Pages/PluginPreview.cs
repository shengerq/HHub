using Hhub.Core;

namespace Hhub.Desktop.Pages;

internal sealed class PluginPreview
{
#if DEBUG
    private static readonly string[] Names =
    [
        "鼠标配置", "键盘灯效", "耳机均衡器", "手柄映射", "摄像头设置", "系统监测",
        "触控板手势", "显示器配置", "麦克风设置", "音频路由", "快捷键管理", "网络监测",
        "打印机状态", "电量提醒", "窗口布局", "场景切换", "USB 设备监测", "外设配置备份"
    ];

    private readonly HashSet<string> _installed = Enumerable.Range(1, 12)
        .Select(index => $"preview.plugin-{index:00}").ToHashSet(StringComparer.Ordinal);
    private readonly HashSet<string> _downloaded = new(StringComparer.Ordinal);

    public IReadOnlyList<InstalledPlugin> Plugins { get; } = Names.Select((name, index) => new InstalledPlugin(
        new PluginManifest
        {
            Id = $"preview.plugin-{index + 1:00}",
            Name = $"虚拟{name}",
            Version = $"1.{index % 4}.0",
            Description = $"{name}的虚拟测试条目。",
            EntryPoint = "preview.exe",
            RequiredCapabilities = ["runtime.dotnet10.shared.v1"]
        }, "")).ToArray();

#else
    private readonly HashSet<string> _installed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _downloaded = new(StringComparer.Ordinal);
    public IReadOnlyList<InstalledPlugin> Plugins { get; } = [];
#endif

    public bool IsInstalled(InstalledPlugin plugin) => _installed.Contains(plugin.Manifest.Id);

    public void Uninstall(InstalledPlugin plugin) => _installed.Remove(plugin.Manifest.Id);

    public bool IsDownloaded(InstalledPlugin plugin) => _downloaded.Contains(plugin.Manifest.Id);

    public void Download(InstalledPlugin plugin) => _downloaded.Add(plugin.Manifest.Id);
}
