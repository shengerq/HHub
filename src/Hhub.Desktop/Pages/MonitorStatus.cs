using System.Text.Json;
using Hhub.PluginContracts;

namespace Hhub.Desktop.Pages;

internal static class MonitorStatus
{
    internal static string? Authorization(IReadOnlyDictionary<string, JsonElement> values) =>
        values.GetValueOrDefault(HomeMonitor.AuthorizationStatus) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() : null;

    internal static string Error(string? status) => status switch
    {
        "declined" => "硬件传感器授权已取消。",
        "driver-missing" => "缺少 PawnIO 驱动，部分传感器数据不可用。",
        "unavailable" => "无法启动硬件传感器采集。",
        "required" => "读取硬件传感器需要管理员授权。",
        _ => ""
    };

    internal static string ReadError(Exception error) => error switch
    {
        InvalidDataException { Message: "service.required-provider-missing" } => "未连接系统监测插件。",
        Hhub.UnifiedHost.PluginRejectedException { Message: "plugin.antivirus-blocked" } => "防病毒软件阻止了系统监测插件。",
        TimeoutException => "系统监测插件响应超时。",
        UnauthorizedAccessException => "访问系统监测插件被拒绝。",
        _ => "无法读取系统监测数据。"
    };
}
