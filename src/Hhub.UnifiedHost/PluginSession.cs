using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hhub.PluginContracts;

namespace Hhub.UnifiedHost;

// Sequential per-session RPC; a timeout invalidates the session instead of replaying a write.
public sealed class PluginSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly InstalledPackage _plugin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Task _stderr;
    private readonly IDisposable? _lease;
    private bool _closed;
    private bool _disposed;
    public long Revision { get; private set; }
    public bool BackgroundActivity { get; private set; }
    public bool IsRunning => !_closed && !_process.HasExited;
    public string? LastDiagnostic { get; private set; }
    public PluginTarget[] Targets { get; private set; } = [];
    public string? SelectedTargetId { get; private set; }
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, JsonElement> Values { get; private set; } = new Dictionary<string, JsonElement>();

    private PluginSession(Process process, InstalledPackage plugin, IDisposable? lease)
    {
        _process = process; _plugin = plugin; _lease = lease;
        _stderr = DrainErrorAsync();
    }

    public static async Task<PluginSession> StartAsync(PluginStore store, InstalledPackage plugin, string dotnetPath,
        TimeSpan? timeout = null)
    {
        var lease = store.AcquireSessionLease(plugin);
        try { return await StartCoreAsync(store, plugin, dotnetPath, timeout, lease); }
        catch { lease.Dispose(); throw; }
    }

    internal static async Task ProbeCandidateAsync(PluginStore store, InstalledPackage plugin, string managedHostPath)
    {
        // InstallTransaction holds the exclusive mutation and plugin leases here.
        await using var session = await StartCoreAsync(store, plugin, managedHostPath, TimeSpan.FromSeconds(5), null);
        foreach (var target in session.Targets.Where(target => target.Online)) await session.ReadAsync(target.Id);
    }

    private static async Task<PluginSession> StartCoreAsync(PluginStore store, InstalledPackage plugin, string dotnetPath,
        TimeSpan? timeout, IDisposable? lease)
    {
        plugin = store.VerifyInstalled(plugin);
        string entry = Path.Combine(plugin.Directory, plugin.Definition.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
        bool managed = entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        string data = store.DataDirectory(plugin.Definition.Id); Directory.CreateDirectory(data);
        var info = new ProcessStartInfo(managed ? dotnetPath : entry)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = plugin.Directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (managed) info.ArgumentList.Add(entry);
        foreach (string argument in plugin.Definition.Arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--hhub-plugin-v1");
        info.Environment["HHUB_PLUGIN_DATA"] = data;
        var process = Process.Start(info) ?? throw new IOException("plugin.start-failed");
        var session = new PluginSession(process, plugin, lease);
        try { await session.CallAsync("hello", timeout: timeout); await session.DiscoverAsync(); return session; }
        catch { await session.DisposeAsync(); throw; }
    }

    public Task<RpcResponse> DiscoverAsync() => CallAsync("discover");
    private string SelectTarget(string? targetId)
    {
        string? id = targetId ?? SelectedTargetId;
        var online = Targets.Where(target => target.Online).ToArray();
        if (id is null) id = online.Length == 1 ? online[0].Id : throw new InvalidDataException("target.selection-required");
        if (!online.Any(target => target.Id == id)) throw new InvalidDataException("target.not-available");
        return id;
    }
    public Task<RpcResponse> ReadAsync(string? targetId = null) => CallAsync("read", targetId: SelectTarget(targetId));
    public Task<RpcResponse> SetAsync(string capabilityId, JsonElement value, string? targetId = null)
    {
        var capability = _plugin.Definition.Capabilities.SingleOrDefault(item => item.Id == capabilityId);
        if (capability is null || !capability.Writable || !capability.Value.Accepts(value))
            throw new InvalidDataException("capability.write-invalid");
        string target = SelectTarget(targetId);
        if (!_revisions.TryGetValue(target, out long revision)) throw new InvalidDataException("transaction.state-unknown");
        return CallAsync("set", capabilityId, value, revision, targetId: target);
    }

    public Task<RpcResponse> ApplyPresetAsync(PresetDocument preset, string? targetId = null)
    {
        string target = SelectTarget(targetId);
        if (preset.SchemaVersion != 1 || preset.PluginId != _plugin.Definition.Id || preset.TargetId != target)
            throw new InvalidDataException("preset.identity-mismatch");
        if (preset.Values.Count is 0 or > 128 || preset.Values.Any(pair =>
            _plugin.Definition.Capabilities.SingleOrDefault(item => item.Id == pair.Key) is not { } capability
            || !capability.Writable || capability.Persistence == "session" || !capability.Value.Accepts(pair.Value)))
            throw new InvalidDataException("preset.values-invalid");
        if (!_revisions.TryGetValue(target, out long revision)) throw new InvalidDataException("transaction.state-unknown");
        var changes = preset.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        return CallAsync("apply", expectedRevision: revision, targetId: target, changes: changes);
    }

    private async Task<RpcResponse> CallAsync(string type, string? capabilityId = null, JsonElement? value = null,
        long? expectedRevision = null, TimeSpan? timeout = null, string? targetId = null,
        Dictionary<string, JsonElement>? changes = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning) throw new IOException("plugin.session-ended");
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            string id = Guid.NewGuid().ToString("N");
            var request = new RpcRequest(type, id, PluginApi.Protocol, _plugin.Definition.Id, capabilityId, value, expectedRevision, targetId, changes);
            string message = JsonSerializer.Serialize(request, PluginApi.Json);
            if (message.Length > PluginApi.MaxMessageCharacters) throw new InvalidDataException("plugin.request-too-large");
            await _process.StandardInput.WriteLineAsync(message.AsMemory(), deadline.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            string line = await ReadBoundedLineAsync(_process.StandardOutput, deadline.Token).ConfigureAwait(false);
            var response = PluginApi.Read<RpcResponse>(line);
            if (response.Type != "result" || response.RequestId != id || response.Protocol != PluginApi.Protocol
                || response.PluginId != _plugin.Definition.Id || response.TargetId != targetId
                || type is "read" or "set" or "apply" && response.Revision < _revisions.GetValueOrDefault(targetId!))
                throw new InvalidDataException("plugin.response-invalid");
            if (!response.Success)
            {
                if (targetId is not null && response.ErrorCode == "transaction.state-unknown") _revisions.Remove(targetId);
                throw new PluginRejectedException(response.ErrorCode ?? "plugin.rejected");
            }
            if (type == "discover")
            {
                var targets = response.Targets ?? throw new InvalidDataException("target.discovery-missing");
                if (targets.Length > 128 || targets.Any(target => target is null || string.IsNullOrWhiteSpace(target.Id)
                    || target.Id.Length > 256 || target.Kind != _plugin.Definition.Kind || string.IsNullOrWhiteSpace(target.Name))
                    || targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targets.Length)
                    throw new InvalidDataException("target.discovery-invalid");
                Targets = targets;
                foreach (string target in _revisions.Keys.Where(target => !targets.Any(item => item.Id == target && item.Online)).ToArray()) _revisions.Remove(target);
                if (SelectedTargetId is { } selected && !_revisions.ContainsKey(selected))
                { SelectedTargetId = null; Values = new Dictionary<string, JsonElement>(); Revision = 0; }
            }
            if (type is "read" or "set" or "apply")
            {
                var values = response.Values ?? throw new InvalidDataException("plugin.state-missing");
                if (values.Count != _plugin.Definition.Capabilities.Length || _plugin.Definition.Capabilities.Any(item =>
                    !values.TryGetValue(item.Id, out var actual) || !item.Value.Accepts(actual)))
                    throw new InvalidDataException("plugin.state-invalid");
                if (type == "set" && (!JsonElement.DeepEquals(values[capabilityId!], value!.Value) || response.Revision <= _revisions.GetValueOrDefault(targetId!)))
                    throw new InvalidDataException("plugin.write-unconfirmed");
                if (type == "apply" && (changes!.Any(pair => !JsonElement.DeepEquals(values[pair.Key], pair.Value))
                    || response.Revision <= _revisions.GetValueOrDefault(targetId!)))
                    throw new InvalidDataException("plugin.write-unconfirmed");
                Values = values; Revision = response.Revision; SelectedTargetId = targetId; _revisions[targetId!] = response.Revision;
            }
            BackgroundActivity = response.BackgroundActivity;
            return response;
        }
        catch (PluginRejectedException) { throw; }
        catch
        {
            _closed = true; await KillAndWaitAsync().ConfigureAwait(false); throw;
        }
        finally { _gate.Release(); }
    }

    public static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder(); char[] character = new char[1];
        while (await reader.ReadAsync(character, cancellationToken).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= PluginApi.MaxMessageCharacters) throw new InvalidDataException("plugin.message-too-large");
            line.Append(character[0]);
        }
        throw new IOException("plugin.output-closed");
    }

    private async Task DrainErrorAsync()
    {
        try
        {
            if (_disposed) return;
            char[] buffer = new char[4096]; int count;
            while ((count = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                LastDiagnostic = new string(buffer, 0, count);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private async Task KillAndWaitAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (!_closed)
            {
                _closed = true;
                if (!_process.HasExited)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        var request = new RpcRequest("shutdown", Guid.NewGuid().ToString("N"), PluginApi.Protocol, _plugin.Definition.Id);
                        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, PluginApi.Json).AsMemory(), deadline.Token);
                        await _process.StandardInput.FlushAsync(deadline.Token);
                        await _process.WaitForExitAsync(deadline.Token);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException) { }
                }
                await KillAndWaitAsync();
            }
            await _stderr.ConfigureAwait(false);
            _process.Dispose(); _lease?.Dispose(); _disposed = true;
        }
        finally { _gate.Release(); }
    }
}

public sealed class PluginRejectedException(string code) : IOException(code);
