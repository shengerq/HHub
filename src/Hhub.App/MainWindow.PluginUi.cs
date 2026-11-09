using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Hhub.Core;
using Hhub.PluginUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace Hhub.App;
public sealed partial class MainWindow {
 private readonly List<Action> _pluginControlUpdates = [];
 private readonly List<IDisposable> _pluginPageLifetimes = [];
 private readonly Dictionary<string, Task<string>> _pluginUiPaths = [];
 private Task<string> PreparePluginUiAsync(InstalledPlugin plugin) {
  string id = plugin.Manifest.Id;
  if (_installingPlugins.Contains(id)) throw new InvalidOperationException("插件正在安装或卸载");
  if (_pluginUiPaths.TryGetValue(id, out var current)
      && (!current.IsCompleted || current.IsCompletedSuccessfully && File.Exists(current.Result)))
   return current;
  string cacheRoot = Path.Combine(_storage.Root, "ui-cache");
  return _pluginUiPaths[id] = Task.Run(() => PluginUiShadow.Stage(plugin, cacheRoot));
 }
 private async Task WaitForPluginUiStagingAsync(string id) {
  if (!_pluginUiPaths.TryGetValue(id, out var pending) || pending.IsCompleted) return;
  try { await pending; }
  catch (Exception error) { System.Diagnostics.Trace.WriteLine(error.GetBaseException().Message); }
 }
 private async Task MountPluginPageAsync(InstalledPlugin plugin, PluginPage page, Grid shell, TextBlock loading) {
  try {
   string path = await PreparePluginUiAsync(plugin);
   if (!CanCompletePage(shell)) return;
   if (_installingPlugins.Contains(plugin.Manifest.Id))
   {
    loading.Text = "插件正在安装或卸载，请稍后重新打开";
    return;
   }
   var view = LoadPluginPage(plugin, page, stagedPath: path);
   var header = page.HeaderPlacement == "right" ? _actions : _titleControls;
   var controls = view.HeaderControls;
   for (int index = 0; index < controls.Count; index++)
    header.Children.Insert(index, controls[index]);
   shell.Children.Clear();
   shell.Children.Add(view.Root);
  } catch (Exception error) {
   if (CanCompletePage(shell)) loading.Text = "无法加载插件界面：" + error.GetBaseException().Message;
  }
 }
 private void ReleasePluginPages() {
  _embeddedPluginIds.Clear();
  _pluginControlUpdates.Clear();
  foreach(var lifetime in _pluginPageLifetimes) {
   try { lifetime.Dispose(); }
   catch(Exception error) { ShowError("插件界面清理失败："+error.GetBaseException().Message); }
  }
  _pluginPageLifetimes.Clear();
 }
 private IPluginPageView LoadPluginPage(InstalledPlugin plugin, PluginPage page,
  Func<string,IReadOnlyDictionary<string,JsonElement>,Task>? send = null, bool embedded = false, string? stagedPath = null) {
  PluginPageLoadContext? loadContext = null;
  try {
   var device = _openDevice;
   string path = stagedPath ?? PreparePluginUiAsync(plugin).GetAwaiter().GetResult();
   loadContext=new PluginPageLoadContext();
   var assembly=loadContext.LoadFromAssemblyPath(path);
   var type=assembly.GetExportedTypes().Single(t=>typeof(IPluginUiModule).IsAssignableFrom(t)&&!t.IsAbstract);
   var module=(IPluginUiModule)Activator.CreateInstance(type)!;
   var context=new PluginUiContext {
    Plugin=plugin,Snapshot=()=>_runtime.Snapshot(plugin.Manifest.Id),IsFresh=()=>_runtime.IsFresh(plugin.Manifest),
    IsEnabled=()=>PluginMode(plugin.Manifest.Id)!="disabled", Send=send??((command,values)=>SendPluginCommandAsync(plugin,command,values)),
    RunAction=RunUiActionAsync,AvailableWidth=()=>_content.ActualWidth,Accent=Accent,Muted=Muted,Stroke=Stroke,Surface=Surface,
    DeviceInstanceId=device?.InstanceId,Theme=()=>_dark?"dark":"light",IsSidebarCollapsed=()=>_preferences.SidebarCollapsed,
    Device=device,DeviceStatus=()=>{var d=device is null?null:Describe(device);return(d?.Name??plugin.Manifest.Name,d?.Connection??"—",d?.Battery);},
    IntegrationManifest=id=>IntegrationTarget(plugin,id) is { } provider && PluginMode(provider.Manifest.Id)!="disabled"
        ? provider.Manifest : null,
    IntegrationSnapshot=id=>IntegrationTarget(plugin,id) is { } target?_runtime.Snapshot(target.Manifest.Id):new Dictionary<string,JsonElement>(),
    IntegrationSend=(id,command,values)=>SendIntegrationAsync(plugin,id,command,values,device),
    EmbedPage=embedded?null:(id,definition)=>CreateEmbeddedPluginPage(plugin,id,definition,device),
    Scenes=()=>_profiles,BindConfiguration=BindPluginConfigurationAsync,
    OpenSettings=()=>{_pluginSettingsOpen=true;Render();},
    CloseSettings=()=>{_pluginSettingsOpen=false;Render();},
    GoBack=()=>{_openPlugin=null;_openDevice=null;RefreshNavigation();Render();}
   };
   var view=module.CreatePage(context,page);
   _pluginControlUpdates.Add(view.Refresh);
   _pluginPageLifetimes.Add(new PluginPageLifetime(view,loadContext));
   return view;
  } catch(Exception error) {
   loadContext?.Unload();
   return new FailedPluginPage(new Border { Child = Empty("\uE783","无法加载插件界面："+error.GetBaseException().Message) });
  }
 }
 private sealed class PluginPageLoadContext():AssemblyLoadContext(isCollectible:true) {
  protected override Assembly? Load(AssemblyName name)=>name.Name==typeof(IPluginUiModule).Assembly.GetName().Name?typeof(IPluginUiModule).Assembly:
   name.Name==typeof(PluginManifest).Assembly.GetName().Name?typeof(PluginManifest).Assembly:null;
 }
 private sealed class PluginPageLifetime(IPluginPageView view,AssemblyLoadContext context):IDisposable {
  public void Dispose(){try {view.Dispose();} finally {context.Unload();}}
 }
 private sealed class FailedPluginPage(FrameworkElement root):IPluginPageView {
  public FrameworkElement Root=>root;
  public IReadOnlyList<FrameworkElement> HeaderControls=>[];
  public void Refresh(){}
  public void Dispose(){}
 }
}
