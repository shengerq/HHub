using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Hhub.App;

public sealed partial class MainWindow
{
    private bool _localPackageInstallBusy;

    private Grid PluginDropZone(UIElement content)
    {
        var zone = new Grid { AllowDrop = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        zone.Children.Add(content);
        var hint = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        hint.Children.Add(Icon("\uE896", 32));
        hint.Children.Add(Text("释放以安装插件包", SectionTextSize, true));
        var hintCard = new Border
        {
            Style = (Style)Application.Current.Resources["HhubCardStyle"], Child = hint, Padding = new(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        // The full-window input layer is transparent; only the small themed hint is filled.
        var overlay = new Border
        {
            Child = hintCard, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = Accent, BorderThickness = new(2), CornerRadius = new(4),
            Visibility = Visibility.Collapsed, AllowDrop = true
        };
        zone.Children.Add(overlay);
        void AcceptDrag(object sender, DragEventArgs args)
        {
            if (args.DataView.Contains(PluginComposition.DragFormat)) return;
            bool accepted = !_localPackageInstallBusy && args.DataView.Contains(StandardDataFormats.StorageItems);
            args.AcceptedOperation = accepted ? DataPackageOperation.Copy : DataPackageOperation.None;
            overlay.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
            args.DragUIOverride.Caption = accepted ? "安装插件包" : "";
            args.Handled = true;
        }
        zone.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(AcceptDrag), handledEventsToo: true);
        zone.AddHandler(UIElement.DragOverEvent, new DragEventHandler(AcceptDrag), handledEventsToo: true);
        zone.DragLeave += (_, _) =>
        {
            // Child-to-overlay transitions also bubble DragLeave. Only hide after
            // the cursor actually leaves the window, otherwise the drop target flickers.
            if (!GetDropCursorPosition(out var cursor)) { overlay.Visibility = Visibility.Collapsed; return; }
            var position = AppWindow.Position; var size = AppWindow.Size;
            if (cursor.X < position.X || cursor.Y < position.Y || cursor.X >= position.X + size.Width || cursor.Y >= position.Y + size.Height)
                overlay.Visibility = Visibility.Collapsed;
        };
        zone.PointerMoved += (_, args) =>
        {
            if (overlay.Visibility == Visibility.Visible && !args.GetCurrentPoint(zone).Properties.IsLeftButtonPressed)
                overlay.Visibility = Visibility.Collapsed;
        };
        zone.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Escape) overlay.Visibility = Visibility.Collapsed; };
        zone.AddHandler(UIElement.DropEvent, new DragEventHandler(async (_, args) =>
        {
            if (args.DataView.Contains(PluginComposition.DragFormat)) return;
            args.Handled = true;
            overlay.Visibility = Visibility.Collapsed;
            var deferral = args.GetDeferral();
            string[] packagePaths = [];
            try
            {
                await RunUiActionAsync(async () =>
                {
                    if (!args.DataView.Contains(StandardDataFormats.StorageItems)) return;
                    var items = await args.DataView.GetStorageItemsAsync();
                    packagePaths = items.Where(item => item.IsOfType(StorageItemTypes.File) && !string.IsNullOrWhiteSpace(item.Path)).Select(item => item.Path).ToArray();
                    if (packagePaths.Length == 0) throw new InvalidDataException("请先将插件包保存或解压到本地，再拖入 .hhub 或 .zip 文件");
                });
            }
            catch (System.Runtime.InteropServices.COMException ex) { ShowError("无法读取拖入的文件：" + ex.Message); }
            finally { deferral.Complete(); }
            if (packagePaths.Length > 0) await RunUiActionAsync(() => InstallLocalPackagesAsync(packagePaths));
        }), handledEventsToo: true);
        return zone;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DropCursorPosition { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDropCursorPosition(out DropCursorPosition point);

    private Task InstallLocalPackageAsync(string path) => InstallLocalPackagesAsync([path]);

    private Task InstallLocalPackagesAsync(IEnumerable<string> paths) =>
        InstallPluginBatchAsync(() => Task.Run(() => PluginBatch.Plan(paths)));

    private async Task<bool> ConfirmPluginPackagesAsync(LocalPluginPackage[] packages, string source)
    {
        var summary = string.Join("\n\n", packages.Select(package =>
        {
            var manifest = package.Manifest;
            var existing = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == manifest.Id);
            var disclosure = manifest.Disclosure;
            var dependencies = disclosure?.Dependencies.Select(item => $"{item.Name} {item.Version}")
                .Concat(disclosure.Runtimes.Select(item => $"{item.Name} {item.Version} ({item.Provision})"))
                .ToArray();
            string version = existing is null ? manifest.Version : $"{existing.Manifest.Version} → {manifest.Version}";
            string permissions = manifest.Permissions.Length > 0 ? string.Join("、", manifest.Permissions) : "无声明";
            string requirements = dependencies is { Length: > 0 } ? string.Join("、", dependencies) : "无结构化声明";
            return $"{manifest.Name} {version}\n声明权限　{permissions}\n运行环境和依赖　{requirements}";
        }));
        var details = new ScrollViewer
        {
            MaxHeight = 360,
            Content = new TextBlock { Text = "来源　" + source + "\n\n" + summary, TextWrapping = TextWrapping.Wrap }
        };
        var confirmation = Dialog("安装或更新插件", details, "继续");
        return await confirmation.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task InstallPluginBatchAsync(Func<Task<PluginBatchPlan>> buildPlan, string source = "本地插件包")
    {
        if (_localPackageInstallBusy) throw new InvalidOperationException("正在安装插件，请稍候");
        _localPackageInstallBusy = true;
        try
        {
            var plan = await buildPlan();
            var failures = plan.Issues.Select(issue => Path.GetFileName(issue.Path) + "：" + issue.Message).ToList();
            if (plan.Packages.Length == 0) throw new InvalidDataException(string.Join("\n", failures));
            var eligible = new List<LocalPluginPackage>();
            foreach (var package in plan.Packages)
            {
                var existing = _installed.Plugins.FirstOrDefault(plugin => plugin.Manifest.Id == package.Manifest.Id);
                try
                {
                    if (existing is not null) PluginCatalog.ValidateUpgrade(existing.Manifest, package.Manifest);
                    eligible.Add(package);
                }
                catch (InvalidDataException error) { failures.Add(error.Message); }
            }
            if (eligible.Count == 0) throw new InvalidDataException(string.Join("\n", failures));
            if (!await ConfirmPluginPackagesAsync(eligible.ToArray(), source)) return;
            // Only leave a plugin page when that plugin is being replaced.
            // UI assemblies load from immutable copies, so package replacement needs no blocking GC.
            if (_openPlugin is { } current && eligible.Any(package => package.Manifest.Id == current.Manifest.Id))
            {
                _openPlugin = null; _openDevice = null; _pluginSettingsOpen = false;
                _page = "plugins"; _pluginTab = "manage"; Render();
            }
            int installed = 0;
            foreach (var package in eligible)
            {
                try { await InstallPackageAsync(package); installed++; }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException
                    or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
                { failures.Add(package.Manifest.Name + "：" + error.Message); }
            }
            RefreshNavigation();
            if (_page == "plugins" && _openPlugin is null && _openDevice is null)
            {
                _pluginTab = "manage"; _pluginSearch = ""; _pluginTargetDevice = null; Render();
            }
            else UpdateDeviceViews();
            string result = $"完成 {installed} 个" + (plan.Skipped.Length > 0 ? $"，跳过 {plan.Skipped.Length} 个重复版本" : "");
            if (failures.Count > 0) result += "\n" + string.Join("\n", failures);
            var finished = new ContentDialog { Title = "插件安装结果", Content = result, CloseButtonText = "关闭", XamlRoot = _root.XamlRoot, RequestedTheme = _root.ActualTheme };
            await finished.ShowAsync();
        }
        finally { _localPackageInstallBusy = false; QueueActivation(); }
    }
}
