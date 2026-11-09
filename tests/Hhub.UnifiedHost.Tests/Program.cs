using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hhub.PluginContracts;
using Hhub.PluginSdk;
using Hhub.UnifiedHost;

if (args is ["--prepare-install-fixture", var fixtureProfile, var fixturePackage, var fixtureCertificate])
{
    using var certificate = X509CertificateLoader.LoadCertificateFromFile(fixtureCertificate);
    var store = new PluginStore(fixtureProfile, new CertificationPolicy([certificate.GetCertHashString(HashAlgorithmName.SHA256)]));
    var installed = await store.InstallAsync(fixturePackage);
    store.SavePreset(new(1, installed.Definition.Id, "安装验收预设", new()
    { ["mouse.dpi"] = JsonSerializer.SerializeToElement(800), ["mouse.stages"] = JsonSerializer.SerializeToElement(new[] { 800, 1600 }) }, TargetId: "simulation.default"));
    File.WriteAllText(Path.Combine(store.DataDirectory(installed.Definition.Id), "private-helper.exe"), "test only");
    Directory.CreateDirectory(Path.Combine(store.Root, "downloads")); File.WriteAllText(Path.Combine(store.Root, "downloads", "test.tmp"), "test only");
    return;
}

if (args is ["--interrupt-update", var interruptedPackage, var profile, var publicCertificate, var phase])
{
    using var certificate = X509CertificateLoader.LoadCertificateFromFile(publicCertificate);
    var store = new PluginStore(profile, new CertificationPolicy([certificate.GetCertHashString(HashAlgorithmName.SHA256)]));
    await store.InstallAsync(interruptedPackage, candidate =>
    {
        File.WriteAllText(Path.Combine(store.DataDirectory(candidate.Definition.Id), "preset.json"), "candidate format that must not survive rollback");
        if (phase == "after-pointer") File.WriteAllText(Path.Combine(profile, "plugins", candidate.Definition.Id, "current.json"),
            JsonSerializer.Serialize(new { version = candidate.Definition.Version, hash = candidate.PackageHash }, PluginApi.Json));
        Environment.Exit(19);
        return Task.CompletedTask;
    });
    return;
}

if (args.Contains("--hhub-plugin-v1"))
{
    if (args.Contains("--hang")) { await Task.Delay(Timeout.Infinite); return; }
    if (args.Contains("--bad-identity"))
    {
        var request = PluginApi.Read<RpcRequest>((await Console.In.ReadLineAsync())!);
        Console.WriteLine(JsonSerializer.Serialize(new RpcResponse("result", request.RequestId, PluginApi.Protocol, "wrong.plugin", true), PluginApi.Json));
        return;
    }
    var definition = PluginApi.Read<PluginDefinition>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(MemoryBackend).Assembly.Location)!, "plugin.json")));
    IPluginBackend backend = args.Contains("--multi") ? new MultiTargetBackend() : new MemoryBackend(definition);
    await PluginApplication.RunAsync(definition, backend, Console.In, Console.Out);
    return;
}

string root = Path.Combine(Path.GetTempPath(), "hhub-unified-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    using var rsa = RSA.Create(3072);
    var request = new CertificateRequest("CN=Hhub TEST ONLY", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    string signer = certificate.GetCertHashString(HashAlgorithmName.SHA256);
    string publicCertificatePath = Path.Combine(root, "test.cer");
    File.WriteAllBytes(publicCertificatePath, certificate.Export(X509ContentType.Cert));
    var policy = new CertificationPolicy([signer]);
    var store = new PluginStore(Path.Combine(root, "profile"), policy);
    Check(store.Discover().Plugins.Length == 0 && !Directory.Exists(store.DataDirectory("test.mouse")), "empty host does not install or start optional functions");

    foreach (string kind in new[] { "device", "function" })
    {
        string id = kind == "device" ? "test.mouse" : "test.eq";
        var definition = Definition(id, kind);
        string payload = Path.Combine(root, id); Directory.CreateDirectory(payload);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(payload, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(definition, PluginApi.Json));
        string unsigned = Path.Combine(root, id + ".unsigned.hhub"); ZipFile.CreateFromDirectory(payload, unsigned);
        Reject(() => CertifiedPackages.Verify(unsigned, policy), "unsigned " + kind + " package is rejected");
        string package = Path.Combine(root, id + ".hhub"); CertifiedPackages.CertifyDirectory(payload, package, certificate);
        var verified = CertifiedPackages.Verify(package, policy);
        Check(verified.Definition.Id == id, "certified " + kind + " package validates");
        Reject(() => CertifiedPackages.Verify(package, new CertificationPolicy([])), "untrusted certification is rejected");
        Reject(() => CertifiedPackages.Verify(package, new CertificationPolicy([signer], [verified.Sha256])), "revoked package is rejected");
        string changed = Path.Combine(root, id + ".changed.hhub"); File.Copy(package, changed);
        using (var zip = ZipFile.Open(changed, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.GetEntry("plugin.json")!.Open())) writer.Write("{}");
        Reject(() => CertifiedPackages.Verify(changed, policy), "manifest modification invalidates certification");
        string added = Path.Combine(root, id + ".added.hhub"); File.Copy(package, added);
        using (var zip = ZipFile.Open(added, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry("extra.dll").Open())) writer.Write("payload");
        Reject(() => CertifiedPackages.Verify(added, policy), "extra executable invalidates certification");
        string removed = Path.Combine(root, id + ".removed.hhub"); File.Copy(package, removed);
        using (var zip = ZipFile.Open(removed, ZipArchiveMode.Update)) zip.GetEntry("Hhub.PluginContracts.dll")!.Delete();
        Reject(() => CertifiedPackages.Verify(removed, policy), "missing dependency invalidates certification");
        var installed = await store.InstallAsync(package);
        Check(store.Discover().Plugins.Any(plugin => plugin.Definition.Id == id), "installed package is reverified during discovery");
        var revokedStore = new PluginStore(store.Root, new CertificationPolicy([signer], [verified.ContentHash]));
        Check(revokedStore.Discover().Errors.Length > 0, "installed content revocation survives archive removal");
        var preset = new PresetDocument(1, id, "My preset", new() { ["sample.value"] = JsonSerializer.SerializeToElement(800) });
        store.SavePreset(preset);
        store.SavePreset(preset with { TargetId = "fixture.a", Name = "A profile" });
        store.SavePreset(preset with { TargetId = "fixture.b", Name = "B profile", Values = new() { ["sample.value"] = JsonSerializer.SerializeToElement(1600) } });
        Check(store.LoadPreset(id, targetId: "fixture.a")!.Values["sample.value"].GetInt32() == 800
            && store.LoadPreset(id, targetId: "fixture.b")!.Values["sample.value"].GetInt32() == 1600,
            "same-model targets retain independent presets");
        string dotnet = Environment.GetEnvironmentVariable("HHUB_TEST_SHARED_HOST") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe");
        await using (var session = await PluginSession.StartAsync(store, installed, dotnet))
        {
            await session.ReadAsync();
            await RejectBusyAsync(() => store.UninstallAsync(id, false), "running plugin cannot be uninstalled");
            await RejectBusyAsync(() => store.InstallAsync(package), "running plugin cannot be replaced");
            Check(session.Values["sample.value"].GetInt32() == 800, kind + " plugin completes handshake and full read through same SDK");
            await session.SetAsync("sample.value", JsonSerializer.SerializeToElement(1200));
            Check(session.Revision == 1 && session.Values["sample.value"].GetInt32() == 1200, "write is read back with a new revision");
            Reject(() => session.SetAsync("sample.value", JsonSerializer.SerializeToElement(-1)), "invalid write rejected before sending");
            var saved = store.LoadPreset(id)! with { TargetId = "fixture.1" };
            await session.ApplyPresetAsync(saved);
            Check(session.Revision == 2 && session.Values["sample.value"].GetInt32() == 800, "saved preset is applied and read back through the public protocol");
            Reject(() => session.ApplyPresetAsync(saved with { PluginId = "another.plugin" }), "preset cannot be applied to another plugin");
            Reject(() => session.ApplyPresetAsync(saved with { TargetId = "another.target" }), "preset cannot be applied to another target");
            Reject(() => session.ApplyPresetAsync(saved with { Values = new() { ["sample.running"] = JsonSerializer.SerializeToElement(true) } }),
                "preset cannot start a transient task or write a read-only capability");
            if (kind == "function")
            {
                await session.SetAsync("sample.running", JsonSerializer.SerializeToElement(true));
                Check(session.BackgroundActivity, "function plugin reports a real simulated continuous task");
                await session.SetAsync("sample.running", JsonSerializer.SerializeToElement(false));
                Check(!session.BackgroundActivity, "function stops retaining background demand after its task ends");
            }
        }
        Check(!System.Diagnostics.Process.GetProcessesByName("Hhub.UnifiedHost.Tests").Any(process => process.Id != Environment.ProcessId && process.MainModule?.FileName?.StartsWith(installed.Directory) == true), "graceful shutdown leaves no fixture process");
        File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(definition with { Version = "1.1.0" }, PluginApi.Json));
        string update = Path.Combine(root, id + ".update.hhub"); CertifiedPackages.CertifyDirectory(payload, update, certificate);
        string presetPath = Path.Combine(store.DataDirectory(id), "preset.json");
        string originalPreset = File.ReadAllText(presetPath);
        bool startupFailed = false;
        try
        {
            await store.InstallAsync(update, _ =>
            {
                File.WriteAllText(presetPath, "partially migrated settings");
                File.WriteAllText(Path.Combine(store.DataDirectory(id), "candidate-only.txt"), "must be removed");
                throw new IOException("simulated candidate startup failure");
            });
        }
        catch (IOException error) when (error.Message == "simulated candidate startup failure") { startupFailed = true; }
        Check(startupFailed && store.Discover().Plugins.Single(plugin => plugin.Definition.Id == id).Definition.Version == "1.0.0", "failed candidate startup restores the old program");
        Check(File.ReadAllText(presetPath) == originalPreset && !File.Exists(Path.Combine(store.DataDirectory(id), "candidate-only.txt")), "failed migration restores exact preset bytes and removes partial data");
        foreach (string phaseName in new[] { "before-pointer", "after-pointer" })
        {
            var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--interrupt-update", update, store.Root, publicCertificatePath, phaseName }) info.ArgumentList.Add(argument);
            using var child = System.Diagnostics.Process.Start(info)!;
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(child.ExitCode == 19 && File.Exists(Path.Combine(store.Root, "plugins", id, "pending.json")), "interrupted " + phaseName + " update retains a recovery journal");
            if (kind == "device" && phaseName == "before-pointer")
            {
                string backupPreset = Directory.GetFiles(Path.Combine(store.Root, "transactions", id), "preset.json", SearchOption.AllDirectories).Single();
                byte[] backupBytes = File.ReadAllBytes(backupPreset);
                File.AppendAllText(backupPreset, "corrupt backup");
                var blocked = new PluginStore(store.Root, policy);
                Check(blocked.RecoveryIssues.Length > 0 && !blocked.Discover().Plugins.Any(plugin => plugin.Definition.Id == id),
                    "corrupt recovery backup blocks activation instead of silently accepting candidate data");
                Check(File.Exists(Path.Combine(store.Root, "plugins", id, "pending.json")), "failed recovery retains journal and backup for repair");
                File.WriteAllBytes(backupPreset, backupBytes);
            }
            var recovered = new PluginStore(store.Root, policy);
            Check(recovered.RecoveryIssues.Length == 0 && recovered.Discover().Plugins.Single(plugin => plugin.Definition.Id == id).Definition.Version == "1.0.0"
                && File.ReadAllText(presetPath) == originalPreset, "restart restores program and presets after " + phaseName);
            Check(!File.Exists(Path.Combine(store.Root, "plugins", id, "pending.json")) && Directory.GetDirectories(Path.Combine(store.Root, "staging")).Length == 0,
                "restart cleans committed recovery journals and interrupted staging");
        }
        var newer = await store.InstallAsync(update);
        Check(newer.Definition.Version == "1.1.0" && store.LoadPreset(id)!.Values["sample.value"].GetInt32() == 800, "program update preserves versioned user presets");
        Check(store.ListPresets(id, "fixture.a").Length == 1 && store.LoadPreset(id, targetId: "fixture.b")!.Values["sample.value"].GetInt32() == 1600,
            "update validates and preserves all target-specific presets");
        File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(definition with
        { Version = "1.2.0", Capabilities = definition.Capabilities.Where(capability => capability.Id != "sample.value").ToArray() }, PluginApi.Json));
        string incompatible = Path.Combine(root, id + ".incompatible.hhub"); CertifiedPackages.CertifyDirectory(payload, incompatible, certificate);
        await RejectAsync(() => store.InstallAsync(incompatible), "update cannot remove a capability used by an existing preset");
        Check(store.Discover().Plugins.Single(plugin => plugin.Definition.Id == id).Definition.Version == "1.1.0"
            && File.ReadAllText(presetPath) == originalPreset, "incompatible update preserves active program and original preset bytes");
        await RejectAsync(() => store.InstallAsync(package), "downgrade is rejected without replacing current version");
        string executable = Path.Combine(newer.Directory, definition.EntryPoint);
        File.AppendAllText(executable, "tampered");
        Reject(() => store.VerifyInstalled(newer), "installed binary tampering is detected before startup");
        Check(store.Discover().Errors.Length > 0, "one tampered plugin produces a discoverable error");
        await store.UninstallAsync(id, preserveData: true);
        Check(!Directory.Exists(Path.Combine(store.Root, "plugins", id)) && store.LoadPreset(id) is not null, "uninstall removes binaries and retains only explicitly preserved presets");
        await store.UninstallAsync(id, preserveData: false);
        Check(!Directory.Exists(store.DataDirectory(id)), "complete removal cleans plugin data too");
        Check(!File.Exists(Path.Combine(store.Root, "locks", id + ".lock")), "uninstall removes its plugin-specific lock record");
    }

    {
        var definition = Definition("test.multi", "device") with { Arguments = ["--multi"] };
        string payload = Path.Combine(root, "multi"); Directory.CreateDirectory(payload);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(payload, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(definition, PluginApi.Json));
        string package = Path.Combine(root, "multi.hhub"); CertifiedPackages.CertifyDirectory(payload, package, certificate);
        var installed = await store.InstallAsync(package);
        string dotnet = Environment.GetEnvironmentVariable("HHUB_TEST_SHARED_HOST") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe");
        await using (var session = await PluginSession.StartAsync(store, installed, dotnet))
        {
            Check(session.Targets.Length == 2 && session.Targets.Select(target => target.Id).Distinct().Count() == 2,
                "two same-name devices remain separate discovered targets");
            Reject(() => session.ReadAsync(), "multiple targets require an explicit selection");
            await session.ReadAsync("fixture.a"); await session.SetAsync("sample.value", JsonSerializer.SerializeToElement(900));
            await session.ReadAsync("fixture.b");
            Check(session.Revision == 0 && session.Values["sample.value"].GetInt32() == 800, "target B does not inherit target A state or revision");
            await session.SetAsync("sample.value", JsonSerializer.SerializeToElement(1600)); await session.ReadAsync("fixture.a");
            Check(session.Revision == 1 && session.Values["sample.value"].GetInt32() == 900, "target writes are isolated by identity");
            await session.DiscoverAsync(); Reject(() => session.ReadAsync("fixture.b"), "offline target cannot be silently replaced by another device");
            await session.DiscoverAsync();
            Reject(() => session.SetAsync("sample.value", JsonSerializer.SerializeToElement(1700), "fixture.b"), "reconnected target must be read before writing");
            await session.ReadAsync("fixture.b");
            Check(session.Revision == 0 && session.Values["sample.value"].GetInt32() == 1600, "reconnection establishes a fresh target session with real backend state");
        }
        await store.UninstallAsync("test.multi", false);
    }
    var audioRequirement = new ServiceRequirement("audio.processing", 1);
    Check(ServiceCatalog.Resolve(audioRequirement, []) is null, "optional EQ service absence does not block a device plugin");
    Reject(() => ServiceCatalog.Resolve(audioRequirement with { Optional = false }, []), "missing required service is reported");
    var audioDefinition = Definition("test.provider", "function") with { Provides = [new("audio.processing", "1.0", ["sample.value"])] };
    PluginApi.Validate(audioDefinition);
    var provider = new InstalledPackage(audioDefinition, "not-started", new string('A', 64));
    Check(ServiceCatalog.Resolve(audioRequirement, [provider])?.ProviderId == provider.Definition.Id, "service resolves by contract and compatible major version");
    Check(ServiceCatalog.Resolve(audioRequirement with { MajorVersion = 2 }, [provider]) is null, "incompatible service major version is not substituted");
    var second = provider with { Definition = audioDefinition with { Id = "test.provider-two" } };
    Reject(() => ServiceCatalog.Resolve(audioRequirement, [provider, second]), "multiple providers require a user choice");
    Check(ServiceCatalog.Resolve(audioRequirement, [provider, second], second.Definition.Id)!.ProviderId == second.Definition.Id,
        "explicit service provider selection is stable");
    Reject(() => PluginApi.Validate(audioDefinition with { Provides = [new("audio.processing", "1.0", ["missing.capability"])] }),
        "service cannot expose an undeclared capability");

    foreach (string fault in new[] { "--hang", "--bad-identity" })
    {
        var definition = Definition("test.fault", "device") with { Arguments = [fault] };
        string payload = Path.Combine(root, fault); Directory.CreateDirectory(payload);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(payload, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(definition, PluginApi.Json));
        string package = Path.Combine(root, fault + ".hhub"); CertifiedPackages.CertifyDirectory(payload, package, certificate);
        var installed = await store.InstallAsync(package);
        string dotnet = Environment.GetEnvironmentVariable("HHUB_TEST_SHARED_HOST") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe");
        bool rejected = false;
        try { await using var session = await PluginSession.StartAsync(store, installed, dotnet, TimeSpan.FromMilliseconds(250)); }
        catch (Exception error) when (error is OperationCanceledException or InvalidDataException) { rejected = true; }
        Check(rejected, fault + " cannot create a usable session");
        await store.UninstallAsync("test.fault", false);
    }
    var sample = Definition("test.sdk", "device");
    var input = new StringReader(string.Join('\n', new[]
    {
        new RpcRequest("read", "1", PluginApi.Protocol, sample.Id),
        new RpcRequest("hello", "2", PluginApi.Protocol, sample.Id),
        new RpcRequest("discover", "d", PluginApi.Protocol, sample.Id),
        new RpcRequest("read", "3", PluginApi.Protocol, sample.Id, TargetId: "fixture.1"),
        new RpcRequest("set", "4", PluginApi.Protocol, sample.Id, "sample.value", JsonSerializer.SerializeToElement(900), 99, "fixture.1"),
        new RpcRequest("shutdown", "5", PluginApi.Protocol, sample.Id)
    }.Select(item => JsonSerializer.Serialize(item, PluginApi.Json))) + "\n");
    var replies = new StringWriter(); await PluginApplication.RunAsync(sample, new MemoryBackend(sample), input, replies);
    var responses = replies.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(PluginApi.Read<RpcResponse>).ToArray();
    Check(responses[0].ErrorCode == "protocol.handshake-required", "SDK rejects operations before handshake");
    Check(responses[4].ErrorCode == "transaction.revision-conflict", "stale revision cannot change plugin state");
    foreach (bool failRecovery in new[] { false, true })
    {
        var commands = new[] { new RpcRequest("hello", "h", PluginApi.Protocol, sample.Id),
            new RpcRequest("discover", "d", PluginApi.Protocol, sample.Id),
            new RpcRequest("read", "r", PluginApi.Protocol, sample.Id, TargetId: "fixture.1"),
            new RpcRequest("set", "s", PluginApi.Protocol, sample.Id, "sample.value", JsonSerializer.SerializeToElement(900), 0, "fixture.1"),
            new RpcRequest("set", "s2", PluginApi.Protocol, sample.Id, "sample.value", JsonSerializer.SerializeToElement(950), 0, "fixture.1"),
            new RpcRequest("shutdown", "q", PluginApi.Protocol, sample.Id) };
        var wire = new StringReader(string.Join('\n', commands.Select(item => JsonSerializer.Serialize(item, PluginApi.Json))) + "\n");
        var results = new StringWriter();
        await PluginApplication.RunAsync(sample, new FailingBackend(failRecovery), wire, results);
        var messages = results.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(PluginApi.Read<RpcResponse>).ToArray();
        Check(messages[3].ErrorCode == (failRecovery ? "transaction.state-unknown" : "transaction.rolled-back"),
            failRecovery ? "failed recovery reports unknown state" : "failed readback restores baseline before reporting rollback");
        if (failRecovery) Check(messages[4].ErrorCode == "transaction.state-unknown", "unknown state blocks another write");
    }
    foreach (int failureMode in new[] { 0, 1, 2 })
    {
        var batchDefinition = sample with { Capabilities = [sample.Capabilities[0],
            new("sample.other", "Second configuration", new() { Type = "integer", Minimum = 50, Maximum = 45000 }, true, "configuration")] };
        var changes = new Dictionary<string, JsonElement> { ["sample.value"] = JsonSerializer.SerializeToElement(900), ["sample.other"] = JsonSerializer.SerializeToElement(1800) };
        var commands = new[] { new RpcRequest("hello", "h", PluginApi.Protocol, sample.Id),
            new RpcRequest("discover", "d", PluginApi.Protocol, sample.Id),
            new RpcRequest("read", "r", PluginApi.Protocol, sample.Id, TargetId: "fixture.1"),
            new RpcRequest("apply", "a", PluginApi.Protocol, sample.Id, ExpectedRevision: 0, TargetId: "fixture.1", Values: changes),
            new RpcRequest("shutdown", "q", PluginApi.Protocol, sample.Id) };
        var backend = new BatchBackend(failureMode);
        var results = new StringWriter();
        await PluginApplication.RunAsync(batchDefinition, backend,
            new StringReader(string.Join('\n', commands.Select(command => JsonSerializer.Serialize(command, PluginApi.Json))) + "\n"), results);
        var messages = results.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(PluginApi.Read<RpcResponse>).ToArray();
        if (failureMode == 0)
            Check(messages[3].Success && messages[3].Revision == 1 && backend.Values["sample.value"].GetInt32() == 900
                && backend.Values["sample.other"].GetInt32() == 1800, "batch preset confirms every value with one revision");
        else
        {
            Check(messages[3].ErrorCode == (failureMode == 1 ? "transaction.rolled-back" : "transaction.state-unknown"),
                "partial preset failure reports rollback or unknown state accurately");
            Check(backend.Writes == 4 && backend.Values["sample.value"].GetInt32() == 800,
                "batch recovery attempts all affected values even when one restore fails");
            if (failureMode == 1) Check(backend.Values["sample.other"].GetInt32() == 1600, "failed batch restores both original values");
        }
    }
    var unknownData = new PresetDocument(1, "test.future", "Future", new());
    store.SavePreset(unknownData);
    string futurePath = Path.Combine(store.DataDirectory("test.future"), "preset.json");
    string future = File.ReadAllText(futurePath).Replace("\"schemaVersion\":1", "\"schemaVersion\":2"); File.WriteAllText(futurePath, future);
    Reject(() => store.LoadPreset("test.future"), "unknown preset format requires migration rather than defaults");
    Reject(() => store.SavePreset(unknownData), "older host cannot overwrite newer preset data");
    Check(File.ReadAllText(futurePath) == future, "rejected preset migration preserves original bytes");
    Reject(() => PluginApi.Read<PluginDefinition>("{\"id\":\"a\",\"id\":\"b\"}"), "duplicate JSON keys are rejected");
    string missingVersion = JsonSerializer.Serialize(Definition("test.version", "device"), PluginApi.Json).Replace("\"schemaVersion\":2,", "");
    Reject(() => PluginApi.Read<PluginDefinition>(missingVersion), "new manifest cannot omit its contract version");
    Reject(() => PluginApi.Validate(Definition("test.old", "device") with { SchemaVersion = 1 }), "historical manifest is not silently adapted");
    Reject(() => PluginApi.ValidatePath("../outside.exe"), "archive traversal is rejected");
    Reject(() => PluginApi.ValidatePath("CON.dll"), "Windows device path aliases are rejected");
    var complex = new ValueDefinition { Type = "object", Properties = new() { ["stages"] = new() { Type = "array", MaxItems = 6, Items = new() { Type = "integer", Minimum = 50, Maximum = 45000 } } } };
    complex.ValidateDefinition();
    Check(complex.Accepts(JsonSerializer.SerializeToElement(new { stages = new[] { 800, 1600 } })), "structured DPI value uses the public recursive schema");
    Check(!complex.Accepts(JsonSerializer.SerializeToElement(new { stages = new[] { 800 }, ignored = true })), "unknown structured fields are rejected");
    passed += await ProfileCleanupChecks.RunAsync(root, policy);
    string orphanSnapshot = Path.Combine(store.Root, "transactions", "test.orphan", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(orphanSnapshot); File.WriteAllText(Path.Combine(orphanSnapshot, "snapshot.json"), "uncommitted snapshot");
    var recoveredStore = new PluginStore(store.Root, policy);
    Check(!Directory.Exists(orphanSnapshot), "restart removes abandoned snapshots without a pending journal");
    Check(recoveredStore.Discover().Plugins.Length == 0, "reopening storage does not resurrect uninstalled plugins");
    Console.WriteLine($"{passed} unified host checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
void Reject(Action action, string name)
{
    bool rejected = false;
    try { action(); } catch (Exception error) when (error is InvalidDataException or CryptographicException or JsonException) { rejected = true; }
    Check(rejected, name);
}
async Task RejectAsync(Func<Task<InstalledPackage>> action, string name)
{
    bool rejected = false; try { await action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, name);
}
async Task RejectBusyAsync(Func<Task> action, string name)
{
    bool rejected = false; try { await action(); } catch (IOException) { rejected = true; } Check(rejected, name);
}
static PluginDefinition Definition(string id, string kind) => new()
{
    Id = id, Name = "Explicit simulation", Version = "1.0.0", Kind = kind, EntryPoint = "Hhub.UnifiedHost.Tests.dll",
    Capabilities = [new("sample.value", "Simulated DPI", new() { Type = "integer", Minimum = 50, Maximum = 45000 }, true, "configuration"),
        new("sample.running", "Simulated processing", new() { Type = "boolean" }, kind == "function")]
};
sealed class MemoryBackend(PluginDefinition definition) : IPluginBackend
{
    private readonly Dictionary<string, JsonElement> _state = new() { ["sample.value"] = JsonSerializer.SerializeToElement(800), ["sample.running"] = JsonSerializer.SerializeToElement(false) };
    public bool BackgroundActivity => definition.Kind == "function" && _state["sample.running"].GetBoolean();
    public Task<PluginTarget[]> DiscoverAsync() => Task.FromResult<PluginTarget[]>([new("fixture.1", "Fixture", definition.Kind, "virtual", true, true)]);
    public Task<Dictionary<string, JsonElement>> ReadAsync(string targetId) => Task.FromResult(_state);
    public Task WriteAsync(string targetId, string capabilityId, JsonElement value) { _state[capabilityId] = value.Clone(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _state["sample.running"] = JsonSerializer.SerializeToElement(false); return ValueTask.CompletedTask; }
}
sealed class FailingBackend(bool failRecovery) : IPluginBackend
{
    private int _value = 800;
    private bool _attempted;
    public bool BackgroundActivity => false;
    public Task<PluginTarget[]> DiscoverAsync() => Task.FromResult<PluginTarget[]>([new("fixture.1", "Fixture", "device", "virtual", true, true)]);
    public Task<Dictionary<string, JsonElement>> ReadAsync(string targetId) => Task.FromResult(new Dictionary<string, JsonElement>
    { ["sample.value"] = JsonSerializer.SerializeToElement(_value), ["sample.running"] = JsonSerializer.SerializeToElement(false) });
    public Task WriteAsync(string targetId, string capabilityId, JsonElement value)
    {
        if (!_attempted) { _attempted = true; _value = 901; }
        else if (failRecovery) throw new IOException("simulated rollback failure");
        else _value = value.GetInt32();
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
sealed class BatchBackend(int failureMode) : IPluginBackend
{
    public Dictionary<string, JsonElement> Values { get; } = new()
    { ["sample.value"] = JsonSerializer.SerializeToElement(800), ["sample.other"] = JsonSerializer.SerializeToElement(1600) };
    public int Writes { get; private set; }
    public bool BackgroundActivity => false;
    public Task<PluginTarget[]> DiscoverAsync() => Task.FromResult<PluginTarget[]>([new("fixture.1", "Batch fixture", "device", "virtual", true, true)]);
    public Task<Dictionary<string, JsonElement>> ReadAsync(string targetId) => Task.FromResult(Values);
    public Task WriteAsync(string targetId, string capabilityId, JsonElement value)
    {
        Writes++;
        if (failureMode == 2 && Writes == 3) throw new IOException("restore failed");
        Values[capabilityId] = value.Clone();
        if (failureMode != 0 && Writes == 2) throw new IOException("partially applied write");
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
sealed class MultiTargetBackend : IPluginBackend
{
    private readonly Dictionary<string, int> _values = new() { ["fixture.a"] = 800, ["fixture.b"] = 800 };
    private int _discoveryCount;
    public bool BackgroundActivity => false;
    public Task<PluginTarget[]> DiscoverAsync()
    {
        _discoveryCount++;
        return Task.FromResult<PluginTarget[]>([new("fixture.a", "Same model", "device", "virtual", true, true),
            new("fixture.b", "Same model", "device", "virtual", _discoveryCount != 2, true)]);
    }
    public Task<Dictionary<string, JsonElement>> ReadAsync(string targetId) => Task.FromResult(new Dictionary<string, JsonElement>
    { ["sample.value"] = JsonSerializer.SerializeToElement(_values[targetId]), ["sample.running"] = JsonSerializer.SerializeToElement(false) });
    public Task WriteAsync(string targetId, string capabilityId, JsonElement value) { _values[targetId] = value.GetInt32(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
