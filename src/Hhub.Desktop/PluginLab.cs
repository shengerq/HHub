using System.Text.Json;
using System.Diagnostics;
using Hhub.Core;
using Hhub.PluginContracts;
using Hhub.UnifiedHost;

namespace Hhub.Desktop;

internal sealed class PluginLab(string profile, string[] packages, string fingerprint, string[]? previousSigners = null)
{
    private static string[] Signers(string fingerprint, string[]? previous) => [fingerprint, .. previous ?? []];

    private Lazy<Task<(PluginStore Store, Source[] Sources, SemaphoreSlim StartupGate)>> _catalog =
        MakeCatalog(profile, packages, Signers(fingerprint, previousSigners), new SemaphoreSlim(1, 1));

    private static Lazy<Task<(PluginStore Store, Source[] Sources, SemaphoreSlim StartupGate)>> MakeCatalog(string profile, string[] packages, string[] signers, SemaphoreSlim startupGate) => new(() => Task.Run(() =>
    {
        var policy = new CertificationPolicy(signers);
        var store = new PluginStore(Path.GetFullPath(profile), policy);
        var installed = store.DiscoverManifests().Plugins.ToDictionary(plugin => plugin.Definition.Id, StringComparer.Ordinal);
        var sources = packages.Select(package =>
        {
            string path = Path.GetFullPath(package);
            var reference = CertifiedPackages.ReadCertifiedReference(path, policy);
            var existing = installed.GetValueOrDefault(reference.Definition.Id);
            bool matches = existing?.PackageHash == reference.ContentHash;
            var candidate = new InstalledPackage(reference.Definition,
                Path.Combine(store.Root, "plugins", reference.Definition.Id, reference.ContentHash), reference.ContentHash);
            return new Source(candidate, new(() => Task.Run(async () =>
            {
                if (matches)
                {
                    try { return store.VerifyInstalled(candidate); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                        or System.Security.Cryptography.CryptographicException or JsonException) { Trace.WriteLine(error); }
                }
                await startupGate.WaitAsync().ConfigureAwait(false);
                try { return await store.InstallAsync(path).ConfigureAwait(false); }
                finally { startupGate.Release(); }
            })));
        }).ToDictionary(source => source.Candidate.Definition.Id, StringComparer.Ordinal);
        foreach (var package in installed.Values)
            sources.TryAdd(package.Definition.Id, new Source(package, new(() => Task.Run(() => store.VerifyInstalled(package)))));
        return (store, sources.Values.ToArray(), startupGate);
    }));

    private sealed record Source(InstalledPackage Candidate, Lazy<Task<InstalledPackage>> Registration);

    public string Profile => Path.GetFullPath(profile);
    public bool AcceptsImports => packages.Length == 0;
    public bool CanReinstall => packages.Length > 0;

    public Task<VerifiedPackage> InspectPackageAsync(string path) => Task.Run(() =>
        CertifiedPackages.ReadCertifiedReference(path, new CertificationPolicy(Signers(fingerprint, previousSigners))));

    public async Task InstallPackageAsync(string path, string? expectedId = null, string? expectedVersion = null, string? expectedHash = null)
    {
        var (store, _, startupGate) = await _catalog.Value;
        await startupGate.WaitAsync();
        try
        {
            if (expectedId is not null)
            {
                var verified = await InspectPackageAsync(path);
                if (verified.Definition.Id != expectedId || verified.Definition.Version != expectedVersion)
                    throw new InvalidDataException("下载的插件与库中的身份或版本不一致。");
            }
            var plugin = await Task.Run(() => store.InstallAsync(path, expectedArchiveHash: expectedHash));
            if (PluginActivation.Read(plugin.Definition.Id) == PluginActivation.Mode.Removed)
                PluginActivation.Save(plugin.Definition.Id, PluginActivation.Mode.Default);
            _catalog = MakeCatalog(profile, packages, Signers(fingerprint, previousSigners), startupGate);
        }
        finally { startupGate.Release(); }
    }

    public bool Running(string id)
    {
        lock (_efficiencyGate)
            return _workers.Any(worker => worker.Id == id && worker.Process.TryGetTarget(out var process) && IsLive(process));
    }

    public async Task SetActivationAsync(string id, PluginActivation.Mode mode)
    {
        var (store, _, startupGate) = await _catalog.Value;
        await startupGate.WaitAsync();
        try
        {
            var previous = PluginActivation.Read(id);
            PluginActivation.Save(id, mode);
            try
            {
                if ((mode is PluginActivation.Mode.Disabled or PluginActivation.Mode.Removed)
                    && _retained.Remove(id, out var retained))
                    await retained.DisposeAsync();
                if (mode == PluginActivation.Mode.Removed)
                {
                    await store.UninstallAsync(id, preserveData: true);
                    _catalog = MakeCatalog(profile, packages, Signers(fingerprint, previousSigners), startupGate);
                }
            }
            catch
            {
                PluginActivation.Save(id, previous);
                throw;
            }
        }
        finally { startupGate.Release(); }
    }

    public async Task<(PluginStore Store, InstalledPackage[] Plugins)> RegisterAsync(Func<PluginDefinition, bool>? include = null)
    {
        var (store, sources, _) = await _catalog.Value.ConfigureAwait(false);
        var plugins = await Task.WhenAll(sources.Where(source => PluginActivation.Enabled(source.Candidate.Definition.Id) && include?.Invoke(source.Candidate.Definition) != false)
            .Select(source => source.Registration.Value)).ConfigureAwait(false);
        return (store, plugins);
    }

    public async Task<(PluginStore Store, InstalledPackage[] Plugins)> DiscoverManifestsAsync()
    {
        var (store, sources, _) = await _catalog.Value.ConfigureAwait(false);
        return (store, sources.Select(source => source.Candidate).ToArray());
    }

    internal sealed record LicenseNotice(string Name, string Text, string? Directory);

    public async Task<LicenseNotice[]> ReadLicensesAsync()
    {
        var (store, _, _) = await _catalog.Value.ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var notices = new List<LicenseNotice>();
            foreach (var plugin in store.DiscoverManifests().Plugins.OrderBy(plugin => plugin.Definition.Name))
            {
                string file = Path.Combine(plugin.Directory, "THIRD-PARTY-NOTICES.md");
                string directory = Path.Combine(plugin.Directory, "licenses");
                if (!File.Exists(file) && !Directory.Exists(directory)) continue;
                try
                {
                    var verified = store.VerifyInstalled(plugin);
                    var info = new FileInfo(file);
                    string text = info.Exists && info.Length <= 256 * 1024 ? File.ReadAllText(file) : "";
                    string? files = Directory.Exists(directory) ? directory : null;
                    if (text.Length != 0 || files is not null) notices.Add(new(verified.Definition.Name, text, files));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or InvalidDataException or System.Security.Cryptography.CryptographicException or JsonException)
                { Trace.WriteLine(error); }
            }
            return notices.ToArray();
        }).ConfigureAwait(false);
    }

    // The store takes an exclusive profile lock while acquiring each plugin's session lease.
    private readonly Dictionary<string, PluginSession> _retained = new(StringComparer.Ordinal);

    public bool HasBackgroundActivity => _retained.Values.Any(session => session.IsRunning && session.BackgroundActivity);

    public async Task<PluginSession> OpenInteractiveSessionAsync(PluginStore store, InstalledPackage plugin, bool retain,
        CancellationToken token, TimeSpan? timeout)
    {
        string id = plugin.Definition.Id;
        if (_retained.TryGetValue(id, out var open))
        {
            if (open.IsRunning) return open;
            _retained.Remove(id);
            await open.DisposeAsync();
        }
        var session = await StartSessionAsync(store, plugin, token, timeout);
        if (retain) _retained.Add(id, session);
        return session;
    }

    public async ValueTask ReleaseInteractiveSessionAsync(InstalledPackage plugin, PluginSession session)
    {
        string id = plugin.Definition.Id;
        if (_retained.GetValueOrDefault(id) == session)
        {
            if (session.IsRunning && session.BackgroundActivity) return;
            _retained.Remove(id);
        }
        await session.DisposeAsync();
    }

    public async Task StopRetainedSessionsAsync()
    {
        var sessions = _retained.Values.ToArray();
        _retained.Clear();
        await Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));
    }

    private readonly object _efficiencyGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _sessionStarts = new(StringComparer.Ordinal);
    private readonly List<(string Id, WeakReference<Process> Process)> _workers = [];
    private bool _efficiencyMode;

    public void SetEfficiencyMode(bool enabled)
    {
        lock (_efficiencyGate)
        {
            _efficiencyMode = enabled;
            _workers.RemoveAll(worker => !worker.Process.TryGetTarget(out var process) || !ApplyEfficiency(process, enabled));
        }
    }

    private void ConfigureProcess(string id, Process process)
    {
        lock (_efficiencyGate)
        {
            _workers.RemoveAll(worker => !worker.Process.TryGetTarget(out var active) || !IsLive(active));
            if (ApplyEfficiency(process, _efficiencyMode)) _workers.Add((id, new(process)));
        }
    }

    private static bool IsLive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool ApplyEfficiency(Process process, bool enabled)
    {
        if (!OperatingSystem.IsWindows() || !IsLive(process)) return false;
        try { ProcessEfficiency.SetWindowsMode(process, enabled); }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception error) { Trace.WriteLine(error); }
        return true;
    }

    public async Task<PluginSession> StartSessionAsync(PluginStore store, InstalledPackage plugin, CancellationToken token,
        TimeSpan? operationTimeout = null, bool overviewOnly = false)
    {
        var startGate = _sessionStarts.GetOrAdd(plugin.Definition.Id, static _ => new SemaphoreSlim(1, 1));
        await startGate.WaitAsync(token);
        try
        {
            if (!PluginActivation.Enabled(plugin.Definition.Id)) throw new PluginRejectedException("plugin.disabled");
            if (PluginProtection.IsBlocked(plugin)) throw new PluginRejectedException("plugin.antivirus-blocked");
            return await Task.Run(() => PluginSession.StartAsync(store, plugin, "dotnet",
                operationTimeout: operationTimeout, overviewOnly: overviewOnly, configureProcess: process => ConfigureProcess(plugin.Definition.Id, process),
                observeProcess: (process, cancellation) => PluginProtection.WatchAsync(process, plugin, cancellation),
                cancellationToken: token), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is InvalidDataException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception
            || error is IOException { Message: "plugin.output-closed" or "plugin.start-failed" })
        {
            _ = PluginProtection.ReportAsync(plugin, "插件启动或协议握手异常");
            throw;
        }
        finally { startGate.Release(); }
    }

    public async Task ReadServiceAsync(ServiceRequirement requirement,
        Action<IReadOnlyDictionary<string, JsonElement>, TimeSpan> display, CancellationToken token, TimeSpan? interval = null,
        IReadOnlyCollection<string>? optionalReadouts = null)
    {
        var (store, sources, _) = await _catalog.Value.WaitAsync(token);
        var service = ServiceCatalog.Resolve(requirement, sources.Where(source => PluginActivation.Enabled(source.Candidate.Definition.Id)).Select(source => source.Candidate));
        if (service is null) throw new InvalidDataException("service.required-provider-missing");
        var plugin = await sources.Single(item => item.Candidate.Definition.Id == service.ProviderId).Registration.Value.WaitAsync(token);
        var exposed = service.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        if (optionalReadouts is not null)
            foreach (var capability in plugin.Definition.Capabilities)
                if (!capability.Writable && optionalReadouts.Contains(capability.Id)) exposed.Add(capability.Id);
        await RunPageAsync(plugin, async () =>
        {
            await using var session = await StartSessionAsync(store, plugin, token);
            using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(1));
            do
            {
                token.ThrowIfCancellationRequested();
                long started = Stopwatch.GetTimestamp();
                if (!PluginActivation.Enabled(plugin.Definition.Id)) break;
                var reply = await session.ReadAsync(token: token);
                var elapsed = Stopwatch.GetElapsedTime(started);
                token.ThrowIfCancellationRequested();
                display(reply.Values!.Where(pair => exposed.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), elapsed);
            }
            while (await timer.WaitForNextTickAsync(token));
        }, token);
    }

    private sealed record PageSession(Task Work, CancellationTokenSource? Background);
    private readonly Dictionary<string, PageSession> _pageSessions = new(StringComparer.Ordinal);

    public Task WaitForPageSessionsAsync()
    {
        foreach (var session in _pageSessions.Values)
            CancelBackground(session);
        return Task.WhenAll(_pageSessions.Values.Select(session => session.Work).Where(task => !task.IsCompleted));
    }

    public Task? TryRunIdlePageAsync(InstalledPackage plugin, Func<CancellationToken, Task> run, CancellationToken token)
    {
        string id = plugin.Definition.Id;
        if (_pageSessions.GetValueOrDefault(id)?.Work.IsCompleted == false || _retained.ContainsKey(id)) return null;
        var background = CancellationTokenSource.CreateLinkedTokenSource(token);
        var work = RunAfterCurrentAsync(null, run, background.Token);
        _pageSessions[id] = new(work, background);
        _ = DisposeBackgroundAsync(work, background);
        return work;
    }

    private static void CancelBackground(PageSession? session)
    {
        if (session?.Work.IsCompleted != false || session.Background is not { } background) return;
        try { background.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static async Task DisposeBackgroundAsync(Task work, CancellationTokenSource background)
    {
        try { await work; }
        catch (Exception error) { Trace.WriteLine(error); }
        finally { background.Dispose(); }
    }

    public Task RunPageAsync(InstalledPackage plugin, Func<Task> run, CancellationToken token)
    {
        string id = plugin.Definition.Id;
        var previous = _pageSessions.GetValueOrDefault(id);
        CancelBackground(previous);
        var work = RunAfterCurrentAsync(previous?.Work, _ => run(), token);
        _pageSessions[id] = new(work, null);
        return work;
    }

    private static async Task RunAfterCurrentAsync(Task? previous, Func<CancellationToken, Task> run, CancellationToken token)
    {
        if (previous is not null)
        {
            try { await previous; }
            catch (Exception error) { Trace.WriteLine(error); }
        }
        try
        {
            token.ThrowIfCancellationRequested();
            await run(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
