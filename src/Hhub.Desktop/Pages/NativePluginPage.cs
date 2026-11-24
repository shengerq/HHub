using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Hhub.Core;
using Hhub.PluginContracts;
using Hhub.PluginUi;
using Hhub.UnifiedHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Hhub.Desktop.Pages;

internal sealed class NativePluginPage(
    PluginPage page, IPluginPageView view, AssemblyLoadContext loadContext, bool keepActive) : IDisposable
{
    private sealed record Layout(string? NativePage, bool KeepActive);
    private sealed record Presentation(int ApiVersion, string Assembly, PluginPage Page);

    public PluginPage Page => page;
    public IPluginPageView View => view;
    public bool KeepActive => keepActive;

    public static NativePluginPage? Open(PluginStore store, InstalledPackage plugin, FrameworkElement container,
        Func<IReadOnlyDictionary<string, JsonElement>> snapshot, Func<bool> fresh,
        Func<bool> enabled, Func<string, JsonElement, Task> write, Func<Func<Task>, Task> runAction)
    {
        var layoutFile = new FileInfo(Path.Combine(plugin.Directory, "device-page.json"));
        if (!layoutFile.Exists || layoutFile.Length > 64 * 1024) return null;
        var layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(layoutFile.FullName), JsonFormat.Wire);
        if (layout?.NativePage is null) return null;

        plugin = store.VerifyInstalled(plugin);
        if (!plugin.Definition.Permissions.Contains("ui.native", StringComparer.Ordinal))
            throw new InvalidDataException("插件未声明原生界面权限");
        layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(Path.Combine(plugin.Directory, "device-page.json")), JsonFormat.Wire);
        if (layout?.NativePage is not { } nativePath) throw new InvalidDataException("缺少插件界面声明");
        string declaration = Resolve(plugin.Directory, nativePath);
        if (new FileInfo(declaration).Length > 64 * 1024) throw new InvalidDataException("插件界面声明过大");
        var presentation = PluginApi.Read<Presentation>(File.ReadAllText(declaration));
        if (presentation.ApiVersion != 1 || presentation.Page is null)
            throw new InvalidDataException("插件界面版本不受支持");
        string assemblyPath = Resolve(Path.GetDirectoryName(declaration)!, presentation.Assembly);
        var loader = new PageLoadContext();
        try
        {
            using var assemblyBytes = File.OpenRead(assemblyPath);
            var type = loader.LoadFromStream(assemblyBytes).GetExportedTypes()
                .Single(type => !type.IsAbstract && typeof(IPluginUiModule).IsAssignableFrom(type));
            var module = (IPluginUiModule)Activator.CreateInstance(type)!;
            var manifest = new PluginManifest
            {
                Id = plugin.Definition.Id, Name = plugin.Definition.Name, Version = plugin.Definition.Version,
                EntryPoint = plugin.Definition.EntryPoint, Page = presentation.Page
            };
            var context = new PluginUiContext
            {
                Plugin = new InstalledPlugin(manifest, plugin.Directory),
                Snapshot = snapshot, IsFresh = fresh, IsEnabled = enabled,
                Send = (_, _) => throw new InvalidOperationException("插件界面未实现命令映射"),
                WriteCapability = write, RunAction = runAction, AvailableWidth = () => container.ActualWidth,
                Accent = Brush("AccentFillColorDefaultBrush"), Muted = Brush("TextFillColorSecondaryBrush"),
                Stroke = Brush("ControlStrokeColorDefaultBrush"), Surface = Brush("CardBackgroundFillColorDefaultBrush")
            };
            return new NativePluginPage(presentation.Page, module.CreatePage(context, presentation.Page), loader, layout.KeepActive);
        }
        catch
        {
            loader.Unload();
            throw;
        }
    }

    private static string Resolve(string directory, string relative)
    {
        PluginApi.ValidatePath(relative);
        return Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public void Dispose()
    {
        try { View.Dispose(); }
        finally { loadContext.Unload(); }
    }

    private sealed class PageLoadContext() : AssemblyLoadContext(isCollectible: true);
}
