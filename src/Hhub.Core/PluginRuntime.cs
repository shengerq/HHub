using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace Hhub.Core;

public enum PluginRunState { Stopped, Running, Stopping, Faulted }

public sealed class PluginRuntime : IAsyncDisposable
{
    private sealed record PendingCommand(string PluginId, TaskCompletionSource Completion);
    private readonly ConcurrentDictionary<string, Process> _processes = [];
    private readonly ConcurrentDictionary<string, double> _shutdownTimeouts = [];
    private readonly ConcurrentDictionary<string, Task> _readers = [];
    private readonly ConcurrentDictionary<string, SnapshotFrame> _snapshots = [];
    private readonly ConcurrentDictionary<string, string> _errors = [];
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = [];
    private readonly object _startGate = new();
    private readonly ConcurrentDictionary<string, byte> _stopping = [];
    private readonly ConcurrentDictionary<string, byte> _faulted = [];
    private bool _disposed;
    private int _snapshotNotificationQueued;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _stopGates = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeGates = [];
    public event Action? Changed;
    public event Action<string>? FirstSnapshotReceived;
    public Action<Action> Dispatch { get; set; } = action => action();
    public Func<InstalledPlugin, string, JsonElement, Task<JsonElement>>? HostRequest { get; set; }
    public string SharedRuntimeHostPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "Hhub.PluginHost.exe");

    public string[] SharedRuntimeHostArguments { get; init; } = [];
    public bool PreferSharedManagedBackends { get; init; }

    public bool IsRunning(string id)
    {
        lock (_startGate) return _processes.TryGetValue(id, out var process) && !process.HasExited;
    }
    public PluginRunState State(string id) => _stopping.ContainsKey(id) ? PluginRunState.Stopping
        : _faulted.ContainsKey(id) ? PluginRunState.Faulted : IsRunning(id) ? PluginRunState.Running : PluginRunState.Stopped;
    public bool HasSession(string id) => _processes.ContainsKey(id);
    public string? Error(string id) => _errors.GetValueOrDefault(id);
    public SnapshotFrame? Latest(string id) => _snapshots.GetValueOrDefault(id);
    public IReadOnlyDictionary<string, JsonElement> Snapshot(string id) => Latest(id)?.Values ?? FrozenDictionary<string, JsonElement>.Empty;
    public bool IsFresh(PluginManifest manifest) => IsRunning(manifest.Id) && Latest(manifest.Id) is { } frame
        && DateTimeOffset.UtcNow - frame.ReceivedAtUtc <= TimeSpan.FromSeconds(manifest.SnapshotMaxAgeSeconds);
    public bool HasBackgroundActivity(PluginManifest manifest) => manifest.SupportsBackgroundActivity && IsFresh(manifest)
        && Snapshot(manifest.Id).TryGetValue("backgroundActivity", out var value) && value.ValueKind == JsonValueKind.True;

    public void Start(InstalledPlugin plugin)
    {
        lock (_startGate) StartCore(plugin);
    }

    public async Task EnsureStartedAsync(InstalledPlugin plugin, bool retry = false)
    {
        string id = plugin.Manifest.Id;
        var gate = _stopGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning(id) || !retry && HasSession(id)) return;
            await StartForDemandAsync(plugin).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // The per-plugin lifecycle gate is held by the caller.
    private async Task StartForDemandAsync(InstalledPlugin plugin)
    {
        string id = plugin.Manifest.Id;
        if (IsRunning(id)) return;
        if (HasSession(id)) await StopCoreAsync(id).ConfigureAwait(false);
        await Task.Run(() => Start(plugin)).ConfigureAwait(false);
    }

    private void StartCore(InstalledPlugin plugin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string id = plugin.Manifest.Id;
        if (_stopping.ContainsKey(id)) throw new IOException("插件正在停止，请稍候重试");
        if (IsRunning(id)) return;
        if (HasSession(id)) throw new InvalidOperationException("插件已退出，请先重新启动");
        _shutdownTimeouts[id] = plugin.Manifest.ShutdownTimeoutSeconds;
        _errors.TryRemove(id, out _);
        _faulted.TryRemove(id, out _);
        _snapshots.TryRemove(id, out _);
        bool integrated = plugin.Manifest.Transport == "stdio-v1";
        string entry = PluginCatalog.ResolveInside(plugin.Directory, plugin.Manifest.EntryPoint);
        if (PreferSharedManagedBackends && plugin.Manifest.Transport == "stdio-v1" && entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            string assembly = Path.ChangeExtension(entry, ".dll");
            if (IsManagedEntry(assembly)) entry = assembly;
        }
        bool sharedRuntime = entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (sharedRuntime && !File.Exists(SharedRuntimeHostPath))
        {
            const string message = "主体缺少 .NET 10 共享插件入口，请更新完整主体软件包";
            _faulted[id] = 0;
            _errors[id] = message;
            throw new InvalidOperationException(message);
        }
        var info = new ProcessStartInfo(sharedRuntime ? SharedRuntimeHostPath : entry)
        {
            WorkingDirectory = plugin.Directory, UseShellExecute = false, CreateNoWindow = integrated,
            RedirectStandardInput = integrated, RedirectStandardOutput = integrated, RedirectStandardError = integrated
        };
        // Existing audio-rendering plugins are latency-sensitive even before they
        // adopt the explicit manifest flag. Other plugins can declare the same need.
        info.Environment["HHUB_LATENCY_SENSITIVE"] = plugin.Manifest.LatencySensitive || plugin.Manifest.Permissions.Contains("audio.render") ? "1" : "0";
        if (sharedRuntime)
        {
            foreach (string argument in SharedRuntimeHostArguments) info.ArgumentList.Add(argument);
            info.ArgumentList.Add(entry);
        }
        foreach (string argument in plugin.Manifest.Arguments) info.ArgumentList.Add(argument);
        if (integrated)
        {
            info.ArgumentList.Add("--hhub-stdio");
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardInputEncoding = new UTF8Encoding(false);
            info.StandardErrorEncoding = Encoding.UTF8;
        }
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (!Owns(id, process)) return;
            FailPending(id, "插件已退出");
            Dispatch(() =>
            {
                lock (_startGate)
                {
                    if (!Owns(id, process)) return;
                    if (!_stopping.ContainsKey(id) && (integrated || process.ExitCode != 0))
                    {
                        _faulted[id] = 0;
                        _errors.TryAdd(id, $"插件意外退出，代码 {process.ExitCode}");
                    }
                    Changed?.Invoke();
                }
            });
        };
        _processes[id] = process;
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _processes.TryRemove(id, out _); process.Dispose();
            _faulted[id] = 0; _errors[id] = ex.Message; throw;
        }
        if (integrated) _readers[id] = Task.WhenAll(ReadOutputAsync(plugin, process), ReadErrorsAsync(id, process));
        if (process.HasExited && !_stopping.ContainsKey(id))
        {
            FailPending(id, "插件已退出");
            if (integrated || process.ExitCode != 0) { _faulted[id] = 0; _errors.TryAdd(id, $"插件意外退出，代码 {process.ExitCode}"); }
        }
        Dispatch(() => Changed?.Invoke());
    }

    private static bool IsManagedEntry(string path)
    {
        if (!File.Exists(path)) return false;
        string configuration = Path.ChangeExtension(path, ".runtimeconfig.json");
        if (!File.Exists(configuration)) return false;
        using var json = JsonDocument.Parse(File.ReadAllText(configuration));
        if (!json.RootElement.TryGetProperty("runtimeOptions", out var runtime)
            || !runtime.TryGetProperty("tfm", out var tfm) || tfm.GetString() != "net10.0") return false;
        using var stream = File.OpenRead(path);
        using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
        return reader.PEHeaders.CorHeader is { EntryPointTokenOrRelativeVirtualAddress: not 0 };
    }

    private bool Owns(string id, Process process) => _processes.TryGetValue(id, out var current) && ReferenceEquals(current, process);

    private async Task ReadOutputAsync(InstalledPlugin plugin, Process process)
    {
        string id = plugin.Manifest.Id;
        Task? hostRequest = null;
        try
        {
            var input = new PluginPipeReader(process.StandardOutput, 1024 * 1024);
            while (await input.ReadLineAsync() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "snapshot":
                        var values = root.GetProperty("values").Clone().EnumerateObject().ToDictionary(value => value.Name, value => value.Value);
                        DateTimeOffset? captured = root.TryGetProperty("capturedAtUtc", out var timestamp) && timestamp.TryGetDateTimeOffset(out var time) ? time : null;
                        var frame = new SnapshotFrame(DateTimeOffset.UtcNow, captured, values);
                        bool first;
                        lock (_startGate)
                        {
                            if (!Owns(id, process)) break;
                            first = _snapshots.TryAdd(id, frame);
                            if (!first) _snapshots[id] = frame;
                        }
                        if (first && FirstSnapshotReceived is { } firstReceived)
                        {
                            Dispatch(() =>
                            {
                                if (!Owns(id, process) || _stopping.ContainsKey(id)) return;
                                Changed?.Invoke();
                                firstReceived(id);
                            });
                        }
                        else NotifySnapshotChanged();
                        break;
                    case "result":
                        string request = root.GetProperty("requestId").GetString() ?? "";
                        bool success = root.GetProperty("success").GetBoolean();
                        string? resultError = root.TryGetProperty("error", out var error) ? error.GetString() : "插件拒绝执行命令";
                        if (Owns(id, process) && _pending.TryGetValue(request, out var pending) && pending.PluginId == id && _pending.TryRemove(request, out _))
                        {
                            if (success) pending.Completion.TrySetResult();
                            else pending.Completion.TrySetException(new InvalidOperationException(resultError));
                        }
                        break;
                    case "hostRequest":
                        if (!plugin.Manifest.RequiredCapabilities.Contains("host.remote.v1"))
                            throw new InvalidDataException("不允许该插件请求主体操作");
                        if (hostRequest is not null) await hostRequest.ConfigureAwait(false);
                        hostRequest = ReplyToHostRequestAsync(plugin, process, root.GetProperty("requestId").GetString()!,
                            root.GetProperty("action").GetString()!, root.GetProperty("values").Clone());
                        break;
                    default: throw new InvalidDataException("插件发送了不支持的消息类型");
                }
            }
            if (Owns(id, process) && !_stopping.ContainsKey(id)) FaultProtocol(id, process, "插件关闭了通信通道");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            if (Owns(id, process) && !_stopping.ContainsKey(id)) FaultProtocol(id, process, ex.Message);
        }
    }

    private async Task ReplyToHostRequestAsync(InstalledPlugin plugin, Process process, string requestId, string action, JsonElement values)
    {
        string id = plugin.Manifest.Id;
        object response;
        try
        {
            if (_stopping.ContainsKey(id) || !Owns(id, process) || HostRequest is null)
                throw new InvalidOperationException("主体服务不可用");
            var result = await HostRequest(plugin, action, values).ConfigureAwait(false);
            response = new { type = "hostResult", requestId, success = true, values = result };
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException)
        { response = new { type = "hostResult", requestId, success = false, error = error.Message }; }
        if (_stopping.ContainsKey(id) || !Owns(id, process)) return;
        try { await WriteAsync(id, JsonSerializer.Serialize(response, JsonFormat.Wire), TimeSpan.FromSeconds(3), process).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void NotifySnapshotChanged()
    {
        // A busy dispatcher needs one wakeup, not a closure retaining each frame.
        if (Interlocked.Exchange(ref _snapshotNotificationQueued, 1) != 0) return;
        Dispatch(() =>
        {
            Interlocked.Exchange(ref _snapshotNotificationQueued, 0);
            Changed?.Invoke();
        });
    }

    private void FaultProtocol(string id, Process process, string message)
    {
        lock (_startGate)
        {
            if (!Owns(id, process)) return;
            _faulted[id] = 0; _errors[id] = message;
            FailPending(id, message);
        }
        Dispatch(() => Changed?.Invoke());
        _ = Task.Run(async () =>
        {
            try { await StopOwnedAsync(id, process); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            { Dispatch(() => { _errors[id] = ex.Message; Changed?.Invoke(); }); }
        });
    }

    private async Task ReadErrorsAsync(string id, Process process)
    {
        try
        {
            var input = new PluginPipeReader(process.StandardError, 16 * 1024);
            while (await input.ReadLineAsync() is { } line)
            {
                lock (_startGate)
                {
                    if (!Owns(id, process)) break;
                    if (_faulted.ContainsKey(id)) continue;
                    _errors[id] = line;
                }
                NotifySnapshotChanged();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            if (Owns(id, process) && !_stopping.ContainsKey(id)) FaultProtocol(id, process, ex.Message);
        }
    }

    public async Task SendAsync(InstalledPlugin plugin, string command, IReadOnlyDictionary<string, JsonElement>? values = null)
    {
        if (plugin.Manifest.Transport != "stdio-v1") throw new InvalidOperationException("此插件使用独立窗口，不支持集成命令");
        if (!plugin.Manifest.Commands.Any(item => item.Id == command)) throw new InvalidOperationException("插件未声明此命令");
        string id = plugin.Manifest.Id;
        var lifecycle = _stopGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        string requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                await StartForDemandAsync(plugin).ConfigureAwait(false);
                if (plugin.Manifest.SupportsCommandResults) _pending[requestId] = new(id, completion);
                await WriteAsync(id, JsonSerializer.Serialize(new
                {
                    type = "command", requestId, command, values = values ?? new Dictionary<string, JsonElement>()
                }, JsonFormat.Wire), TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            finally { lifecycle.Release(); }
            if (plugin.Manifest.SupportsCommandResults)
            {
                try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                catch (TimeoutException) { throw new IOException("插件未在 15 秒内确认命令"); }
            }
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private async Task WriteAsync(string id, string message, TimeSpan timeout, Process? expected = null)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            var gate = _writeGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (!_processes.TryGetValue(id, out var process) || expected is not null && !ReferenceEquals(expected, process))
                    throw new IOException("插件会话已结束");
                await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellation.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellation.Token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { throw new IOException("插件未及时接收消息"); }
    }

    private void FailPending(string id, string message)
    {
        foreach (var (request, pending) in _pending.Where(pair => pair.Value.PluginId == id))
            if (_pending.TryRemove(request, out _)) pending.Completion.TrySetException(new IOException(message));
    }

    public Task StopAsync(string id) => StopOwnedAsync(id, null);

    private async Task StopOwnedAsync(string id, Process? expected)
    {
        var gate = _stopGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { if (expected is null || Owns(id, expected)) await StopCoreAsync(id); }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync(string id)
    {
        if (!_processes.TryGetValue(id, out var process)) return;
        _stopping[id] = 0;
        FailPending(id, "插件正在停止");
        try
        {
            if (!process.HasExited)
            {
                if (process.StartInfo.RedirectStandardInput)
                {
                    try { await WriteAsync(id, "{\"type\":\"shutdown\"}", TimeSpan.FromSeconds(_shutdownTimeouts.GetValueOrDefault(id, 2.5))); }
                    catch (IOException) { /* The pipe may already be closed or unresponsive. */ }
                }
                else process.CloseMainWindow();
                await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(_shutdownTimeouts.GetValueOrDefault(id, 2.5))));
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (_readers.TryRemove(id, out var reader)) await reader;
            lock (_startGate)
            {
                _processes.TryRemove(id, out _);
                _shutdownTimeouts.TryRemove(id, out _);
                process.Dispose();
            }
            _snapshots.TryRemove(id, out _);
        }
        finally { _stopping.TryRemove(id, out _); Dispatch(() => Changed?.Invoke()); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_startGate) { if (_disposed) return; _disposed = true; }
        try { foreach (string id in _processes.Keys.ToArray()) await StopAsync(id); }
        catch { lock (_startGate) _disposed = false; throw; }
        // Exit callbacks may still be queued; let their managed gates live until the runtime is collected.
    }
}
