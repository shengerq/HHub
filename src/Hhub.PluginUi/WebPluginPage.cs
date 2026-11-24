using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Hhub.PluginUi;

// Reusable content surface: plugins own their HTML/CSS; the host owns the shell,
// process lifecycle and the same declared-command transport as native modules.
public sealed class WebPluginPage : IPluginPageView
{
    private readonly PluginUiContext _context;
    private readonly WebView2 _web = new() { AllowDrop = false, DefaultBackgroundColor = Microsoft.UI.Colors.Transparent, Opacity = 0 };
    private readonly Grid _root = new() { AllowDrop = true };
    private readonly TextBlock _loading = new() { Text = "正在加载插件界面…", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
    private readonly string _folder, _entry;
    private bool _disposed, _initializing, _ready;
    private string _lastFrame = "";
    private const string Origin = "https://plugin.hhub.local";
    public FrameworkElement Root => _root;
    public IReadOnlyList<FrameworkElement> HeaderControls => [];
    public event Action<string>? SectionChanged;
    public WebPluginPage(PluginUiContext context, string folder, string entry = "index.html")
    {
        _context = context;
        _root.Background = context.Surface;
        _folder = PluginCatalog.ResolveInside(context.Plugin.Directory, folder);
        _entry = entry;
        if (!File.Exists(PluginCatalog.ResolveInside(_folder, entry))) throw new InvalidDataException("插件 WebUI 入口不存在");
        _root.Children.Add(_web); _root.Children.Add(_loading);
        _root.Loaded += Loaded;
    }
    private async void Loaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || _initializing) return;
        _initializing = true;
        try
        {
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hhub", "webui", _context.Plugin.Manifest.Id);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
            if (_disposed) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (_disposed) { _web.Close(); return; }
            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("plugin.hhub.local", _folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.NavigationStarting += NavigationStarting;
            core.NewWindowRequested += NewWindowRequested;
            core.WebMessageReceived += MessageReceived;
            core.NavigationCompleted += NavigationCompleted;
            _web.Source = new Uri(Origin + "/" + _entry + "?version=" + Uri.EscapeDataString(_context.Plugin.Manifest.Version));
        }
        catch (Exception ex) { if (!_disposed) _loading.Text = "无法加载插件 WebUI：" + ex.GetBaseException().Message; }
    }
    private static bool Local(string? uri) => Uri.TryCreate(uri, UriKind.Absolute, out var address)
        && address.Scheme == "https" && address.Host == "plugin.hhub.local" && address.IsDefaultPort;
    private void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    { if (!Local(args.Uri)) args.Cancel = true; }
    private void NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
    private void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_disposed) return;
        _ready = args.IsSuccess;
        _web.Opacity = _ready ? 1 : 0;
        _loading.Visibility = _ready ? Visibility.Collapsed : Visibility.Visible;
        if (!_ready) _loading.Text = "插件 WebUI 加载失败：" + args.WebErrorStatus;
        Refresh();
    }
    private async void MessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_disposed || !Local(args.Source)) return;
        string? id = null;
        try
        {
            string json = args.WebMessageAsJson;
            if (json.Length > 65536) throw new InvalidDataException("WebUI 请求过大");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() == "back") { _context.GoBack?.Invoke(); return; }
            if (root.GetProperty("type").GetString() == "ready") { _lastFrame = ""; Refresh(); return; }
            if (root.GetProperty("type").GetString() == "section" &&
                root.TryGetProperty("section", out var section) && section.GetString() is { Length: <= 64 } name)
            {
                SectionChanged?.Invoke(name);
                return;
            }
            if (root.GetProperty("type").GetString() != "command") return;
            id = root.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new InvalidDataException("缺少 WebUI 请求标识");
            string command = root.GetProperty("command").GetString() ?? "";
            if (!_context.IsEnabled()) throw new InvalidOperationException("插件已停用");
            if (!_context.Plugin.Manifest.Commands.Any(c => c.Id == command)) throw new InvalidOperationException("插件未声明此命令");
            var values = root.TryGetProperty("values", out var parameters)
                ? parameters.Deserialize<Dictionary<string, JsonElement>>(JsonFormat.Wire) ?? throw new InvalidDataException("参数必须为对象") : [];
            if (_context.DeviceInstanceId is { } target && !values.ContainsKey("deviceInstanceId")) values["deviceInstanceId"] = JsonSerializer.SerializeToElement(target);
            await _context.Send(command, values);
            Post(new { type = "result", id, success = true });
        }
        catch (Exception ex) { Post(new { type = "result", id, success = false, error = ex.GetBaseException().Message }); }
        finally { Refresh(); }
    }
    private void Post(object frame)
    {
        if (_disposed || !_ready) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(frame, JsonFormat.Wire));
    }
    public void Refresh()
    {
        if (_disposed || !_ready) return;
        string frame = JsonSerializer.Serialize(new { type = "snapshot", values = _context.Snapshot(), fresh = _context.IsFresh(), enabled = _context.IsEnabled(),
            theme = _context.Theme(), deviceInstanceId = _context.DeviceInstanceId }, JsonFormat.Wire);
        if (frame == _lastFrame) return;
        _lastFrame = frame; _web.CoreWebView2.PostWebMessageAsJson(frame);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _root.Loaded -= Loaded;
        _web.Opacity = 0; _web.Visibility = Visibility.Collapsed;
        if (_web.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= NavigationStarting; core.NewWindowRequested -= NewWindowRequested;
            core.WebMessageReceived -= MessageReceived; core.NavigationCompleted -= NavigationCompleted;
        }
        _web.Close();
    }
}
