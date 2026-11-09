using System.Reflection;
using Hhub.Core;
using Hhub.PluginUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Hhub.PluginPreview;

// Developer-only renderer: install using the real catalog, then render the exact
// installed UI DLL without activating a desktop window or injecting any input.
public partial class App : Application
{
    private Window? _window;
    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] options = Environment.GetCommandLineArgs();
        string package = options[1];
        string output = Path.GetFullPath(options[2]);
        string log = output + ".json";
        PluginRuntime? runtime = null;
        IPluginPageView? view = null;
        try
        {
            string previewRoot = Path.Combine(Path.GetTempPath(), "Hhub.PluginPreview", Guid.NewGuid().ToString("N"));
            var catalog = new PluginCatalog(new AppStorage(previewRoot));
            var plugin = catalog.Install(package);
            _window = new Window();
            _window.AppWindow.IsShownInSwitchers = false;
            _window.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1100, 800));
            runtime = new PluginRuntime
            {
                SharedRuntimeHostPath = options.Length > 3 ? Path.GetFullPath(options[3]) : Path.Combine(AppContext.BaseDirectory, "Hhub.PluginHost.exe")
            };
            var dispatcher = _window.DispatcherQueue;
            runtime.Dispatch = action => dispatcher.TryEnqueue(() => action());
            await runtime.SendAsync(plugin, "refresh");
            var modulePath = Path.Combine(plugin.Directory, plugin.Manifest.UiAssembly!);
            var assembly = Assembly.LoadFrom(modulePath);
            var moduleType = assembly.GetExportedTypes().Single(type => typeof(IPluginUiModule).IsAssignableFrom(type) && !type.IsAbstract);
            var module = (IPluginUiModule)Activator.CreateInstance(moduleType)!;
            var context = new PluginUiContext
            {
                Plugin = plugin, Snapshot = () => runtime.Snapshot(plugin.Manifest.Id),
                IsFresh = () => runtime.IsFresh(plugin.Manifest), IsEnabled = () => true,
                Send = (_, _) => throw new InvalidOperationException("预览只读，不能修改设置"),
                RunAction = action => action(), AvailableWidth = () => 900,
                Accent = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                Muted = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 96, 96, 96)),
                Stroke = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 229, 229, 229)),
                Surface = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 251, 251, 251))
            };
            view = module.CreatePage(context, plugin.Manifest.Page!);
            var root = new Grid
            {
                Width = 900, Height = 600, RequestedTheme = ElementTheme.Light,
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 245, 245, 245)),
                Padding = new Thickness(32), RowSpacing = 20
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(new TextBlock { Text = plugin.Manifest.Page!.Title, FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            Grid.SetRow(view.Root, 1); root.Children.Add(view.Root);
            _window.Content = root;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            root.Loaded += (_, _) => loaded.TrySetResult();
            // Show(false) avoids activation; the window remains outside every monitor.
            _window.AppWindow.Show(false);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            root.UpdateLayout();
            view.Refresh();
            await Task.Delay(400);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root, 1350, 900);
            var pixels = await bitmap.GetPixelsAsync();
            var file = await StorageFile.GetFileFromPathAsync(output).AsTask().ContinueWith(async task =>
                task.IsCompletedSuccessfully ? task.Result : await (await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!)).CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.ReplaceExisting)).Unwrap();
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 144, 144, pixels.ToArray());
            await encoder.FlushAsync();
            File.WriteAllText(log, System.Text.Json.JsonSerializer.Serialize(new
            {
                installed = plugin.Directory, version = plugin.Manifest.Version, screenshot = output,
                activated = false, values = runtime.Snapshot(plugin.Manifest.Id)
            }));
        }
        catch (Exception ex) { File.WriteAllText(log, ex.ToString()); Environment.ExitCode = 1; }
        finally
        {
            view?.Dispose();
            if (runtime is not null) await runtime.DisposeAsync();
            _window?.Close();
            Exit();
        }
    }
}
