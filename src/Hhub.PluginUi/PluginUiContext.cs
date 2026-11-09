using System.Text.Json;
using Hhub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
namespace Hhub.PluginUi;
public sealed class PluginUiContext {
 public required InstalledPlugin Plugin { get; init; }
 public required Func<IReadOnlyDictionary<string, JsonElement>> Snapshot { get; init; }
 public required Func<bool> IsFresh { get; init; }
 public required Func<bool> IsEnabled { get; init; }
 public required Func<string, IReadOnlyDictionary<string, JsonElement>, Task> Send { get; init; }
 public required Func<Func<Task>, Task> RunAction { get; init; }
 public required Func<double> AvailableWidth { get; init; }
 public required Brush Accent { get; init; }
 public required Brush Muted { get; init; }
 public required Brush Stroke { get; init; }
 public required Brush Surface { get; init; }
 public string? DeviceInstanceId { get; init; }
 public DeviceRecord? Device { get; init; }
 public Func<(string Name, string Connection, double? Battery)>? DeviceStatus { get; init; }
 public Func<string, PluginManifest?>? IntegrationManifest { get; init; }
 public Func<string, PluginPage, IPluginPageView>? EmbedPage { get; init; }
 public Func<string, IReadOnlyDictionary<string, JsonElement>>? IntegrationSnapshot { get; init; }
 public Func<string, string, IReadOnlyDictionary<string, JsonElement>, Task>? IntegrationSend { get; init; }
 public Func<IReadOnlyList<SceneProfile>>? Scenes { get; init; }
 public Func<string, PluginConfiguration, Task>? BindConfiguration { get; init; }
 public Func<string> Theme { get; init; } = () => "light";
 public Func<bool> IsSidebarCollapsed { get; init; } = () => true;
 public Action? OpenSettings { get; init; }
 public Action? CloseSettings { get; init; }
 public Action? GoBack { get; init; }
}
public interface IPluginUiModule { IPluginPageView CreatePage(PluginUiContext context, PluginPage page); }
public interface IPluginPageView : IDisposable {
 FrameworkElement Root { get; }
 IReadOnlyList<FrameworkElement> HeaderControls { get; }
 void Refresh();
}
