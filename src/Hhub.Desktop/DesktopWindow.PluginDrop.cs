using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Hhub.Desktop;

internal sealed partial class DesktopWindow
{
    private bool _installingDroppedPlugins;
    private ExplorerPluginDrop? _explorerPluginDrop;

    private void EnablePluginDrop()
    {
        if (_pluginLab is not { AcceptsImports: true }) return;
        _layout.AllowDrop = true;
        void AcceptFiles(object sender, DragEventArgs args)
        {
            if (_installingDroppedPlugins || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
            args.AcceptedOperation = DataPackageOperation.Copy;
            args.DragUIOverride.Caption = "识别 HHub 插件包";
            args.Handled = true;
        }
        _layout.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(AcceptFiles), handledEventsToo: true);
        _layout.AddHandler(UIElement.DragOverEvent, new DragEventHandler(AcceptFiles), handledEventsToo: true);
        _layout.AddHandler(UIElement.DropEvent, new DragEventHandler(PluginDropped), handledEventsToo: true);
        _explorerPluginDrop = ExplorerPluginDrop.TryRegister(WinRT.Interop.WindowNative.GetWindowHandle(this), paths =>
        {
            if (_closing || _installingDroppedPlugins) return false;
            return DispatcherQueue.TryEnqueue(async () => await InstallDroppedPluginsAsync(() => Task.FromResult(paths)));
        });
        Closed += (_, _) => _explorerPluginDrop?.Dispose();
    }

    private async void PluginDropped(object sender, DragEventArgs args)
    {
        if (_installingDroppedPlugins || !args.DataView.Contains(StandardDataFormats.StorageItems)
            || _closing) return;

        args.Handled = true;
        args.AcceptedOperation = DataPackageOperation.Copy;
        await InstallDroppedPluginsAsync(() => ReadDropPathsAsync(args));
    }

    private async Task InstallDroppedPluginsAsync(Func<Task<string[]>> readPaths)
    {
        if (_closing || _installingDroppedPlugins || _pluginLab is not { AcceptsImports: true } lab
            || _layout.XamlRoot is not { } root) return;
        _installingDroppedPlugins = true;
        try
        {
            var paths = (await readPaths()).Where(path => Path.GetExtension(path).Equals(".hhub", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
            if (_closing) return;

            var recognized = new List<(string Path, Hhub.UnifiedHost.VerifiedPackage Package)>();
            var errors = new List<string>();
            foreach (var path in paths)
            {
                try { recognized.Add((path, await lab.InspectPackageAsync(path))); }
                catch (Exception error) when (IsPackageError(error))
                { errors.Add($"{Path.GetFileName(path)}：{PackageError(error)}"); }
            }

            if (recognized.Count > 0 && !_closing)
            {
                string summary = string.Join("\n\n", recognized.Select(item =>
                {
                    var definition = item.Package.Definition;
                    return $"{definition.Name} {definition.Version}\n权限：{string.Join("、", definition.Permissions)}";
                }).Concat(errors));
                var confirm = new ContentDialog
                {
                    XamlRoot = root, Title = "安装或更新插件", PrimaryButtonText = "安装", CloseButtonText = "取消",
                    Content = new ScrollViewer { MaxHeight = 320, Content = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap } }
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary || _closing) return;
            }

            int installed = 0;
            foreach (var item in recognized)
            {
                try
                {
                    var definition = item.Package.Definition;
                    await lab.InstallPackageAsync(item.Path, definition.Id, definition.Version, item.Package.Sha256);
                    installed++;
                }
                catch (Exception error) when (IsPackageError(error))
                { errors.Add($"{item.Package.Definition.Name}：{PackageError(error)}"); }
            }
            if (_closing) return;
            if (_viewport.Content is Pages.PluginsPage plugins && installed > 0)
                await plugins.RefreshManagedPluginsAsync();
            string text = paths.Length == 0 ? "请拖入本地 .hhub 插件包。" : $"已安装 {installed} 个插件。";
            if (errors.Count > 0) text += "\n\n" + string.Join("\n", errors);
            await ShowDropResultAsync(root, text);
        }
        catch (Exception error) when (IsPackageError(error) || error is System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Trace.WriteLine(error);
            if (!_closing) await ShowDropResultAsync(root, "无法读取拖入的插件包。");
        }
        finally { _installingDroppedPlugins = false; }
    }

    private static async Task<string[]> ReadDropPathsAsync(DragEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            return items.OfType<StorageFile>().Select(file => file.Path).Where(path => path.Length > 0).ToArray();
        }
        finally { deferral.Complete(); }
    }

    private static async Task ShowDropResultAsync(XamlRoot root, string text)
    {
        try
        {
            await new ContentDialog
            {
                XamlRoot = root, Title = "插件安装结果", CloseButtonText = "关闭",
                Content = new ScrollViewer { MaxHeight = 320, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } }
            }.ShowAsync();
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { System.Diagnostics.Trace.WriteLine(error); }
    }

    private static bool IsPackageError(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException
        or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception;

    private static string PackageError(Exception error) => error.Message switch
    {
        "package.signer-untrusted" => "此插件签名的发布者不受信任。",
        "package.not-certified" => "此插件包缺少签名。",
        "plugin.version-not-newer" => "已安装相同或更新版本。",
        "plugin.archive-changed" => "确认后文件发生了变化，请重新拖入。",
        _ => "安装失败，请检查插件包和运行状态。"
    };
}
