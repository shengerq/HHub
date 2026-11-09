using System.Text;
using System.Text.Json;
using Hhub.PluginContracts;

namespace Hhub.PluginSdk;

public interface IPluginBackend : IAsyncDisposable
{
    Task<PluginTarget[]> DiscoverAsync();
    Task<Dictionary<string, JsonElement>> ReadAsync(string targetId);
    Task WriteAsync(string targetId, string capabilityId, JsonElement value);
    bool BackgroundActivity { get; }
}

public static class PluginApplication
{
    private sealed class TargetState { public long Revision; public bool Unknown = true; }
    public static async Task RunAsync(PluginDefinition definition, IPluginBackend backend, TextReader input, TextWriter output)
    {
        PluginApi.Validate(definition);
        bool ready = false; PluginTarget[] discovered = [];
        var states = new Dictionary<string, TargetState>(StringComparer.Ordinal);
        await using (backend)
        {
            while (await ReadLineAsync(input) is { } line)
            {
                var request = PluginApi.Read<RpcRequest>(line);
                if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128
                    || request.Protocol != PluginApi.Protocol || request.PluginId != definition.Id)
                    throw new InvalidDataException("protocol.request-invalid");
                if (request.Type == "shutdown") return;
                RpcResponse response; TargetState? state = null;
                try
                {
                    Dictionary<string, JsonElement>? values = null; PluginTarget[]? targets = null;
                    if (ready && request.Type is "read" or "set" or "apply")
                    {
                        string id = request.TargetId ?? throw new InvalidDataException("target.required");
                        if (!discovered.Any(target => target.Id == id && target.Online)) throw new InvalidDataException("target.not-available");
                        if (!states.TryGetValue(id, out state)) states[id] = state = new();
                    }
                    if (request.Type == "hello") ready = true;
                    else if (!ready) throw new InvalidDataException("protocol.handshake-required");
                    else if (request.Type == "discover")
                    {
                        targets = await backend.DiscoverAsync();
                        if (targets.Length > 128 || targets.Any(target => target is null || string.IsNullOrWhiteSpace(target.Id)
                            || target.Id.Length > 256 || string.IsNullOrWhiteSpace(target.Name) || target.Name.Length > 120
                            || target.Kind != definition.Kind)
                            || targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targets.Length)
                            throw new InvalidDataException("target.discovery-invalid");
                        foreach (string id in states.Keys.Where(id => !targets.Any(target => target.Id == id && target.Online)).ToArray()) states.Remove(id);
                        discovered = targets;
                    }
                    else if (request.Type == "read")
                    {
                        try { values = await ReadCompleteAsync(request.TargetId!); state!.Unknown = false; }
                        catch { state!.Unknown = true; throw; }
                    }
                    else if (request.Type is "set" or "apply")
                    {
                        if (state!.Unknown) throw new InvalidDataException("transaction.state-unknown");
                        if (request.ExpectedRevision != state.Revision) throw new InvalidDataException("transaction.revision-conflict");
                        var changes = request.Type == "apply" ? request.Values
                            : request.CapabilityId is { } key && request.Value is { } value ? new() { [key] = value } : null;
                        if (changes is null || changes.Count is 0 or > 128 || changes.Any(pair =>
                            definition.Capabilities.SingleOrDefault(item => item.Id == pair.Key) is not { } capability
                            || !capability.Writable || !capability.Value.Accepts(pair.Value)
                            || request.Type == "apply" && capability.Persistence == "session"))
                            throw new InvalidDataException("capability.write-invalid");
                        Dictionary<string, JsonElement> baseline;
                        try { baseline = await ReadCompleteAsync(request.TargetId!); }
                        catch { state.Unknown = true; throw; }
                        var attempted = new List<string>();
                        try
                        {
                            foreach (var pair in changes)
                            {
                                attempted.Add(pair.Key); // A failed write may already have changed the device.
                                await backend.WriteAsync(request.TargetId!, pair.Key, pair.Value);
                            }
                            values = await ReadCompleteAsync(request.TargetId!);
                            if (changes.Any(pair => !JsonElement.DeepEquals(values[pair.Key], pair.Value))) throw new IOException("transaction.readback-mismatch");
                            state.Revision++;
                        }
                        catch
                        {
                            try
                            {
                                bool restoreFailed = false;
                                foreach (string restoreKey in attempted.AsEnumerable().Reverse())
                                {
                                    try { await backend.WriteAsync(request.TargetId!, restoreKey, baseline[restoreKey]); }
                                    catch { restoreFailed = true; }
                                }
                                var recovered = await ReadCompleteAsync(request.TargetId!);
                                if (restoreFailed || baseline.Any(pair => !JsonElement.DeepEquals(pair.Value, recovered[pair.Key])))
                                    throw new IOException("transaction.recovery-mismatch");
                            }
                            catch { state.Unknown = true; }
                            throw new InvalidDataException(state.Unknown ? "transaction.state-unknown" : "transaction.rolled-back");
                        }
                    }
                    else throw new InvalidDataException("protocol.operation-unsupported");
                    response = new("result", request.RequestId, PluginApi.Protocol, definition.Id, true, state?.Revision ?? 0, values,
                        BackgroundActivity: backend.BackgroundActivity, TargetId: request.TargetId, Targets: targets);
                }
                catch (Exception error)
                {
                    response = new("result", request.RequestId, PluginApi.Protocol, definition.Id, false, state?.Revision ?? 0,
                        ErrorCode: error is InvalidDataException ? error.Message : "plugin.operation-failed",
                        BackgroundActivity: backend.BackgroundActivity, TargetId: request.TargetId);
                }
                await output.WriteLineAsync(JsonSerializer.Serialize(response, PluginApi.Json)); await output.FlushAsync();
            }
        }

        async Task<Dictionary<string, JsonElement>> ReadCompleteAsync(string targetId)
        {
            var values = await backend.ReadAsync(targetId);
            if (values.Count != definition.Capabilities.Length || definition.Capabilities.Any(capability =>
                !values.TryGetValue(capability.Id, out var value) || !capability.Value.Accepts(value)))
                throw new InvalidDataException("plugin.state-invalid");
            return values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        }
    }

    private static async Task<string?> ReadLineAsync(TextReader reader)
    {
        var result = new StringBuilder(); char[] buffer = new char[1];
        while (await reader.ReadAsync(buffer) != 0)
        {
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= PluginApi.MaxMessageCharacters) throw new InvalidDataException("protocol.message-too-large");
            result.Append(buffer[0]);
        }
        return result.Length == 0 ? null : throw new InvalidDataException("protocol.truncated-line");
    }
}
