using System.Text;
using System.Text.Json;
using Hhub.PluginContracts;
using Hhub.PluginSdk;

Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
if (!args.Contains("--hhub-plugin-v1")) { Console.Error.WriteLine("Start this explicit simulation through the Hhub preview host."); return; }
string directory = Path.GetDirectoryName(typeof(SimulatedBackend).Assembly.Location)!;
var definition = PluginApi.Read<PluginDefinition>(File.ReadAllText(Path.Combine(directory, "plugin.json")));
await PluginApplication.RunAsync(definition, new SimulatedBackend(definition.Kind), Console.In, Console.Out);

sealed class SimulatedBackend(string kind) : IPluginBackend
{
    private readonly Dictionary<string, JsonElement> _state = kind == "device"
        ? new() { ["mouse.dpi"] = JsonSerializer.SerializeToElement(800), ["mouse.stages"] = JsonSerializer.SerializeToElement(new[] { 800, 1600, 3200 }) }
        : new() { ["audio.enabled"] = JsonSerializer.SerializeToElement(false), ["audio.preamp"] = JsonSerializer.SerializeToElement(0), ["audio.preset"] = JsonSerializer.SerializeToElement("平直") };
    public bool BackgroundActivity => kind == "function" && _state["audio.enabled"].GetBoolean();
    public Task<PluginTarget[]> DiscoverAsync() => Task.FromResult<PluginTarget[]>([new("simulation.default", "模拟目标", kind, "虚拟", true, true)]);
    public Task<Dictionary<string, JsonElement>> ReadAsync(string targetId) => Task.FromResult(_state);
    public Task WriteAsync(string targetId, string capabilityId, JsonElement value) { _state[capabilityId] = value.Clone(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { if (kind == "function") _state["audio.enabled"] = JsonSerializer.SerializeToElement(false); return ValueTask.CompletedTask; }
}
