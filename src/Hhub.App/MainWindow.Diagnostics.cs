using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private readonly DiagnosticJournal _diagnosticJournal = new();
    private string? _selectedDiagnosticId;
    private bool _diagnosticsDetailed;
    private bool _diagnosticsExpanded;
    private ContentControl? _diagnosticsHost;
    private string? _diagnosticsPluginId;
    private string _diagnosticsSignature = "";

    private ContentControl DiagnosticsHost(string? pluginId, bool detailed = false)
    {
        _diagnosticsPluginId = pluginId;
        _diagnosticsDetailed = detailed;
        _diagnosticsHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new(0, 0, 0, 8), Visibility = Visibility.Collapsed
        };
        UpdatePluginDiagnostics();
        return _diagnosticsHost;
    }

    private void UpdatePluginDiagnostics()
    {
        string? targetId = _diagnosticsPluginId ?? MonitorPlugin?.Manifest.Id;
        if (targetId is not { } id) return;
        var plugin = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == id);
        var frame = _runtime.Latest(id);
        var notices = frame is not null && plugin is not null && UsableData(id)
            ? PluginDiagnostics.Read(frame.Values).ToList() : [];
        if (plugin?.Manifest.Category == "monitor" && frame is not null && UsableData(id))
        {
            string backend = frame.Values.TryGetValue("monitor.version", out var version) && version.ValueKind == System.Text.Json.JsonValueKind.String
                ? version.GetString()! : "未报告，请更新监测插件";
            bool elevated = frame.Values.TryGetValue("monitor.isElevated", out var admin) && admin.ValueKind == System.Text.Json.JsonValueKind.True;
            string status = $"安装版本：{plugin.Manifest.Version}；采集端版本：{backend}；采集权限：{(elevated ? "管理员" : "普通用户")}。";
            if (frame.Values.TryGetValue("cpu.clockSource", out var source) && source.ValueKind == System.Text.Json.JsonValueKind.String)
                status += "\nCPU 频率来源：" + source.GetString();
            if (frame.Values.TryGetValue("monitor.authorizationPending", out var pending) && pending.ValueKind == System.Text.Json.JsonValueKind.True)
                status += "\n正在等待用户授权或初始化采集子进程。";
            if (frame.Values.TryGetValue("monitor.authorizationError", out var failure) && failure.ValueKind == System.Text.Json.JsonValueKind.String)
                status += "\n" + failure.GetString();
            notices.Insert(0, new("monitor.status", "采集状态", status, null, null));
        }
        string? error = _runtime.Error(id);
        if (error is not null) notices.Insert(0, new("runtime", "采集进程未能正常运行", error, null, null));
        if (frame is not null && UsableData(id) && frame.Values.TryGetValue("monitor.issues", out var issues)
            && issues.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            string message = string.Join("\n", issues.EnumerateArray().Select(issue => issue.GetString()));
            if (!string.IsNullOrWhiteSpace(message)) notices.Add(new("collection", "其他采集提示", message, null, null));
        }
        // Preserve the last report during restarts and transient stale snapshots.
        if (frame is not null && UsableData(id))
            _diagnosticJournal.Observe(id, notices, DateTimeOffset.UtcNow);
        else if (error is not null)
            _diagnosticJournal.Observe(id, _diagnosticJournal.ForPlugin(id).Where(entry => entry.Active && entry.Notice.Id != "runtime").Select(entry => entry.Notice)
                .Append(new("runtime", "采集进程未能正常运行", error, null, null)), DateTimeOffset.UtcNow);
        if (_diagnosticsHost is null) return;
        var entries = _diagnosticJournal.ForPlugin(id);
        string signature = UsableData(id) + System.Text.Json.JsonSerializer.Serialize(entries);
        if (signature == _diagnosticsSignature) return;
        _diagnosticsSignature = signature;
        _diagnosticsHost.Visibility = Visibility.Visible;
        if (entries.Count == 0) { _diagnosticsHost.Content = Text("尚无采集报告", BodyTextSize); return; }
        var layout = new Grid { ColumnSpacing = 16 };
        layout.ColumnDefinitions.Add(new() { Width = new(240) });
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
        foreach (var entry in entries)
        {
            var label = Text(entry.Notice.Title + (entry.Active ? "" : " · 已恢复"), BodyTextSize);
            list.Items.Add(new ListViewItem { Content = label, Tag = entry.Notice.Id, MinHeight = 40 });
        }
        var details = new StackPanel { Spacing = 12, Padding = new(12) };
        void Select(string noticeId)
        {
            _selectedDiagnosticId = noticeId;
            var entry = entries.First(item => item.Notice.Id == noticeId);
            details.Children.Clear();
            details.Children.Add(Text(entry.Notice.Title, BodyTextSize, true));
            var message = Text(entry.Notice.Message, BodyTextSize); message.TextWrapping = TextWrapping.Wrap;
            details.Children.Add(message);
            details.Children.Add(Text($"{(entry.Active ? (UsableData(id) ? "当前状态" : "上次状态，等待采集更新") : "已恢复，保留记录")} · {entry.ChangedAt.ToLocalTime():HH:mm:ss}", BodyTextSize));
            if (entry.Notice.HelpUrl is not null)
                details.Children.Add(new HyperlinkButton { Content = entry.Notice.HelpText ?? "查看说明", NavigateUri = entry.Notice.HelpUrl, Padding = new(0) });
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListViewItem { Tag: string key }) Select(key); };
        list.SelectedIndex = Math.Max(0, entries.ToList().FindIndex(entry => entry.Notice.Id == _selectedDiagnosticId));
        layout.Children.Add(list);
        var detailScroll = Scroll(details); Grid.SetColumn(detailScroll, 1); layout.Children.Add(detailScroll);
        if (_diagnosticsDetailed) _diagnosticsHost.Content = layout;
        else
        {
            var panel = new Expander { Header = "运行报告", Content = layout, IsExpanded = _diagnosticsExpanded,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            panel.Expanding += (_, _) => _diagnosticsExpanded = true;
            panel.Collapsed += (_, _) => _diagnosticsExpanded = false;
            _diagnosticsHost.Content = panel;
        }
    }
}
