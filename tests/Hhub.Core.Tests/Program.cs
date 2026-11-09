using System.IO.Compression;
using System.Text.Json;
using Hhub.Core;
using Hhub.SystemMonitor;
using System.Globalization;
using System.Reflection.Metadata;


if (args.Length == 2 && args[0] == "--instance-check")
{
    using var lease = InstanceLease.TryAcquire(args[1]);
    Console.WriteLine(lease is null ? "busy" : "acquired");
    return;
}

if (args.Length > 1 && args[0] == "--validate-packages")
{
    string temporary = Path.Combine(Path.GetTempPath(), "hhub-package-audit-" + Guid.NewGuid().ToString("N"));
    var packageCatalog = new PluginCatalog(new AppStorage(temporary));
    try
    {
        foreach (string package in args.Skip(1))
        {
            var installed = packageCatalog.Install(Path.GetFullPath(package));
            Console.WriteLine($"PASS: {installed.Manifest.Id} {installed.Manifest.Version} installs with valid commands, navigation and package paths");
            packageCatalog.Uninstall(installed);
        }
    }
    finally { Directory.Delete(temporary, recursive: true); }
    return;
}

if (args.Contains("--fixture"))
{
    int logArgument = Array.IndexOf(args, "--record-start");
    if (logArgument >= 0) File.AppendAllText(args[logArgument + 1], Environment.ProcessId + "\n");
    Console.WriteLine("{\"type\":\"snapshot\",\"values\":{\"status\":\"ready\"}}");
    while (Console.ReadLine() is { } line)
    {
        using var message = JsonDocument.Parse(line);
        if (message.RootElement.GetProperty("type").GetString() == "shutdown")
        {
            if (args.Contains("--slow-stop")) await Task.Delay(250);
            return;
        }
        string command = message.RootElement.GetProperty("command").GetString()!;
        string requestId = message.RootElement.GetProperty("requestId").GetString()!;
        if (command == "exit") return;
        if (command == "malformed") { Console.WriteLine("invalid-json"); continue; }
        if (command == "bad-result") { Console.WriteLine(JsonSerializer.Serialize(new { type = "result", requestId })); continue; }
        if (command == "oversized") { Console.WriteLine(new string('x', 1024 * 1024 + 1)); continue; }
        if (command == "hold") { Console.WriteLine(JsonSerializer.Serialize(new { type = "snapshot", values = new { status = "held", requestId } })); continue; }
        if (command == "query-host")
        {
            Console.WriteLine("{\"type\":\"hostRequest\",\"requestId\":\"fixture-host\",\"action\":\"state.read\",\"values\":{\"monitor\":true}}");
            using var reply = JsonDocument.Parse(Console.ReadLine()!);
            var result = reply.RootElement;
            bool success = result.GetProperty("type").GetString() == "hostResult"
                && result.GetProperty("requestId").GetString() == "fixture-host"
                && result.GetProperty("success").GetBoolean()
                && result.GetProperty("values").GetProperty("ready").GetBoolean();
            Console.WriteLine(JsonSerializer.Serialize(new { type = "result", requestId, success }));
            continue;
        }
        if (command == "spoof") Console.WriteLine(JsonSerializer.Serialize(new { type = "result", requestId = message.RootElement.GetProperty("values").GetProperty("target").GetString(), success = true }));
        if (command == "burst")
        {
            for (int index = 1; index <= 128; index++)
                Console.WriteLine(JsonSerializer.Serialize(new { type = "snapshot", values = new { index, samples = new[] { index, index + 1 } } }));
        }
        else if (command == "stderr-burst")
        {
            for (int index = 1; index <= 128; index++) Console.Error.WriteLine("diagnostic " + index);
        }
        else Console.WriteLine("{\"type\":\"snapshot\",\"values\":{\"status\":\"received\"}}");
        bool accepted = message.RootElement.GetProperty("command").GetString() != "reject";
        Console.WriteLine(JsonSerializer.Serialize(new { type = "result", requestId = message.RootElement.GetProperty("requestId").GetString(), success = accepted, error = accepted ? null : "fixture rejected command" }));
    }
    return;
}

int passed = 0;
if (args.Contains("--disclosure-checks"))
{
    PluginDisclosureChecks.Run(Check);
    Console.WriteLine($"{passed} disclosure checks passed.");
    return;
}
HardwareDisplayNameChecks.Run(Check);
SceneCreationChecks.Run(Check);
ScenePresetChecks.Run(Check);
await PluginMarketChecks.RunAsync(Check);
await PluginMarketsChecks.RunAsync(Check);
PluginDisclosureChecks.Run(Check);
await ShellIntegrationChecks.RunAsync(Check);
PluginUiShadowChecks.Run(Check);
var journal = new DiagnosticJournal();
var reportTime = DateTimeOffset.UtcNow;
var reportNotice = new PluginNotice("driver", "Driver", "Access denied", null, null);
journal.Observe("monitor", [reportNotice], reportTime);
journal.Observe("monitor", [reportNotice], reportTime.AddSeconds(1));
Check(journal.ForPlugin("monitor").Single().ChangedAt == reportTime, "unchanged reports retain stable state across sample refreshes");
journal.Observe("monitor", [], reportTime.AddSeconds(2));
Check(journal.ForPlugin("monitor").Single() is { Active: false, Notice.Message: "Access denied" }, "recovery retains the original report instead of erasing it");
journal.Observe("monitor", [reportNotice], reportTime.AddSeconds(3));
Check(journal.ForPlugin("monitor").Single().Active && journal.ForPlugin("other").Count == 0, "recurring reports reactivate without duplication and stay isolated by plugin");
for (int index = 1; index <= 70; index++)
{
    var notice = reportNotice with { Id = "sample-" + index };
    journal.Observe("bounded", [notice, notice], reportTime.AddSeconds(index));
}
var boundedReports = journal.ForPlugin("bounded");
Check(boundedReports.Count == 64 && boundedReports[0] is { Active: true, Notice.Id: "sample-70" }
    && boundedReports.All(entry => entry.Notice.Id != "sample-1"), "diagnostic history keeps current reports and evicts the oldest recovered entries");
journal.Forget("bounded");
Check(journal.ForPlugin("bounded").Count == 0 && journal.ForPlugin("monitor").Count == 1, "removing a plugin releases only its diagnostic history");
DeviceInventoryChecks.Run(Check);
GamingPeripheralChecks.Run(Check);
Check(SamplingMath.SensorGigabytes(1536, null) == 1.5 && SamplingMath.SensorGigabytes(null, 2.25f) == 2.25,
    "GPU capacity converts LHM MiB to GiB and preserves native GiB readings");
Check(SamplingMath.SensorGigabytes(0, 8) == 0 && SamplingMath.SensorGigabytes(float.NaN, null) is null
    && SamplingMath.SensorGigabytes(-1, null) is null, "idle VRAM zero stays zero; invalid and unavailable capacity stay unknown");
Check(MonitorDiagnostics.Describe(true, null, true, "CPU 功耗", false).Any(notice => notice.Id == "pawnio.access")
    && MonitorDiagnostics.Describe(true, null, true, "CPU 功耗", true).All(notice => notice.Id != "pawnio.access"),
    "installed driver and open driver handle are distinguished without diagnosing unsupported hardware as a missing driver");
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}

async Task WaitUntil(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (!condition())
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("Lifecycle state did not settle");
        await Task.Delay(20);
    }
}

var activity = new ActivitySnapshot("game", new HashSet<string>(["game", "music"], StringComparer.OrdinalIgnoreCase));
SceneRule Rule(string id, string resource, int priority, string condition = "foreground", string app = "GAME.exe") =>
    new() { Id = id, Name = id, Application = app, Condition = condition, Priority = priority, PluginId = "test.tool", Command = "apply", Resource = resource };
var selected = SceneEngine.Evaluate([Rule("low", "mouse", 1), Rule("high", "mouse", 10), Rule("audio", "eq", 2, "running", "music")], activity);
Check(selected.Count == 2 && selected.Any(rule => rule.Id == "high") && selected.Any(rule => rule.Id == "audio"), "per-resource priority with simultaneous background rule");
Check(SceneEngine.Evaluate([Rule("disabled", "mouse", 1) with { Enabled = false }], activity).Count == 0, "disabled rules do not execute");
Check(SceneEngine.Evaluate([Rule("absent", "mouse", 1, "running", "missing")], activity).Count == 0, "absent process does not match");
Check(SceneEngine.Evaluate([Rule("b", "mouse", 1), Rule("a", "mouse", 1)], activity)[0].Id == "a", "equal priorities are deterministic");
Check(SceneEngine.Evaluate([Rule("dotted", "mouse", 1, "foreground", "game.client")], activity with { ForegroundApplication = "game.client" }).Count == 1, "dots inside a process name are preserved");
Check(SamplingMath.CpuLoad(0, 0, 0, 50, 60, 40) == 50, "CPU kernel time includes idle time");
Check(SamplingMath.CpuLoad(10, 10, 10, 10, 10, 10) is null, "no CPU delta is unknown rather than zero load");
Check(SamplingMath.MegabitsPerSecond(0, 1_000_000, 2) == 4, "network byte rate converts to Mbps");
Check(SamplingMath.MegabitsPerSecond(100, 50, 1) is null, "reset network counters are not negative throughput");
Check(SamplingMath.MemoryLoad(100, 25) == 75 && SamplingMath.MemoryLoad(100, 101) is null, "memory load uses valid available memory");
Check(MetricFormatter.Format(JsonSerializer.SerializeToElement(12.3456), " Mbps", CultureInfo.InvariantCulture) == "12.35 Mbps", "metric precision and units");
Check(MetricFormatter.Format(JsonSerializer.SerializeToElement<object?>(null), "°C") == "—", "missing temperature stays unavailable");

var firstMonitor = new PluginManifest { Id = "test.monitor-one", Name = "One", Version = "1.0.0", EntryPoint = "one.exe", Category = "monitor", ActivationPages = ["home", "system"] };
var secondMonitor = firstMonitor with { Id = "test.monitor-two" };
Check(PluginPolicies.NeedsPage(firstMonitor, "home", null, firstMonitor.Id), "selected monitor activates on home");
Check(!PluginPolicies.NeedsPage(secondMonitor, "home", null, firstMonitor.Id), "unselected monitor does not auto-start");
Check(PluginPolicies.NeedsPage(secondMonitor, "plugins", secondMonitor.Id, firstMonitor.Id), "opening another plugin explicitly creates a page demand");
Check(!PluginPolicies.NeedsPage(firstMonitor, "scenes", null, firstMonitor.Id), "leaving monitor pages releases the page demand");
var mouseA = new DeviceRecord("mouse-A", "Mouse", "鼠标", "USB", "24AE", "1234", "interface-A");
var mouseB = mouseA with { InstanceId = "mouse-B", InterfacePath = "interface-B" };
var adapterPlugin = firstMonitor with { Category = "device", DeviceMatches = [new("24ae", "1234")] };
Check(PluginPolicies.MatchesDevice(adapterPlugin, mouseA), "device identity matches VID/PID without case sensitivity");
Check(!PluginPolicies.MatchesDevice(adapterPlugin, mouseA with { Pid = "9999" }), "same vendor with different product is not an exact match");
Check(PluginPolicies.MatchesDevice(adapterPlugin with { DeviceMatches = [new(Category: "鼠标", Connection: "USB")] }, mouseA), "generic device capabilities can match category and transport");
Check(!PluginPolicies.MatchesDevice(adapterPlugin with { DeviceMatches = [new()] }, mouseA), "empty match rules do not match every device");
var sceneRules = new[] { Rule("scene-low", "", 1) with { SceneId = "music" }, Rule("scene-high", "", 10) with { SceneId = "gaming" } };
Check(SceneEngine.Evaluate(sceneRules, activity).Count == 1, "scene triggers share one priority domain");
Check(SceneEngine.EffectiveScene(sceneRules, activity, "daily", false) == "gaming", "highest priority matching scene wins");
Check(SceneEngine.EffectiveScene(sceneRules, activity, "daily", true) == "daily", "manual scene lock overrides automatic triggers");
Check(SceneEngine.EffectiveScene(sceneRules, activity with { ForegroundApplication = "editor" }, "daily", false) == "daily", "leaving the trigger restores the manual base scene");
var targetValues = new Dictionary<string, JsonElement> { ["deviceInstanceId"] = JsonSerializer.SerializeToElement("wrong") };
var bindingA = new DeviceSceneBinding(mouseA.InstanceId, "gaming", adapterPlugin.Id, "apply", targetValues);
var bindingB = bindingA with { DeviceId = mouseB.InstanceId };
var profile = new SceneProfile("gaming", "Game", "") { Actions = [new(adapterPlugin.Id, "global", "configuration", [])] };
var plan = ScenePlan.Build(profile, [bindingA, bindingB], new HashSet<string> { mouseA.InstanceId, mouseB.InstanceId });
Check(plan.Count == 3 && plan.Select(item => item.Key).Distinct().Count() == 3, "scene retains separate actions for identical device instances");
Check(plan.Single(item => item.DeviceId == mouseA.InstanceId).Values["deviceInstanceId"].GetString() == mouseA.InstanceId, "device command includes its actual instance identity");
Check(targetValues["deviceInstanceId"].GetString() == "wrong", "building a device command does not mutate saved configuration");
Check(ScenePlan.Build(profile, [bindingA, bindingB], new HashSet<string> { mouseA.InstanceId }).Count == 2, "disconnected devices are excluded from scene execution");
var legacy = JsonSerializer.Deserialize<SceneProfile>("{\"id\":\"daily\",\"name\":\"Daily\",\"glyph\":\"\"}", JsonFormat.Options)!;
Check(legacy.Actions.Length == 0, "existing scene files remain compatible");
Check(!PluginPolicies.ShouldAutoStart("always", true, PluginRunState.Faulted) && !PluginPolicies.ShouldAutoStart("onDemand", true, PluginRunState.Stopping), "faulted and stopping plugins cannot enter an automatic restart loop");
Check(PluginPolicies.ShouldAutoStart("onDemand", true, PluginRunState.Stopped) && !PluginPolicies.ShouldAutoStart("disabled", true, PluginRunState.Stopped), "ordinary page demand starts a stopped plugin but never overrides explicit disable");
Check(JsonSerializer.Deserialize<PluginManifest>("{\"id\":\"legacy\",\"name\":\"Legacy\",\"version\":\"1.0\",\"entryPoint\":\"tool.exe\"}", JsonFormat.Options)!.MinimumHostVersion == "0.5.0", "legacy plugin manifests retain compatible host defaults");
Reject(() => PluginCatalog.Validate(adapterPlugin with { MinimumHostVersion = "0.6.0" }), "future host requirements are rejected before installation");
Reject(() => PluginCatalog.Validate(adapterPlugin with { RequiredCapabilities = ["unsupported.future-capability"] }), "unknown required capabilities are reported explicitly");
Reject(() => PluginCatalog.Validate(adapterPlugin with { UiAssembly = "Ui.dll", UiApiVersion = 2 }), "future UI API modules are rejected before loading code");
Reject(() => PluginCatalog.Validate(adapterPlugin with { Page = new("Complex", []) { Layout = "declarative" } }), "a declarative page cannot pretend to work without its UI module");
Reject(() => PluginCatalog.Validate(adapterPlugin with { Page = new("Complex", [new("slider", "Value")]) }), "unsupported controls cannot silently appear as usable tile pages");

var multiConfiguration = adapterPlugin with { Commands = [new("dpi", "DPI", "mouse.dpi"), new("rate", "Rate", "mouse.rate"), new("syncOn", "Sync on", "mouse.sync.on") { ConflictResource = "mouse.sync" }, new("syncOff", "Sync off", "mouse.sync.off") { ConflictResource = "mouse.sync" }] };
var dpiBinding = bindingA with { Command = "dpi" };
var rateBinding = dpiBinding with { Command = "rate" };
var savedBindings = PluginPolicies.SaveDeviceConfiguration([dpiBinding, bindingB], rateBinding, multiConfiguration);
Check(savedBindings.Contains(dpiBinding) && savedBindings.Contains(rateBinding) && savedBindings.Contains(bindingB), "adding a device setting preserves other settings and identical device instances");
savedBindings = PluginPolicies.SaveDeviceConfiguration(savedBindings, dpiBinding with { Values = new() { ["dpi"] = JsonSerializer.SerializeToElement(1600) } }, multiConfiguration);
Check(savedBindings.Count(binding => binding.DeviceId == dpiBinding.DeviceId && binding.Command == "dpi") == 1 && savedBindings.Contains(rateBinding), "editing one device setting replaces only that resource");
var syncOn = dpiBinding with { Command = "syncOn" }; var syncOff = dpiBinding with { Command = "syncOff" };
Check(!PluginPolicies.SaveDeviceConfiguration([syncOn, rateBinding], syncOff, multiConfiguration).Contains(syncOn), "mutually exclusive device commands replace the previous setting");
Check(PluginPolicies.HasNavigation(adapterPlugin with { Category = "utility" }) && !PluginPolicies.HasNavigation(firstMonitor), "functional plugins get sidebar entries while ordinary monitors do not");
var navigationPlugin = adapterPlugin with { Category = "utility", Transport = "stdio-v1", Navigation = new(Glyph: "\uE713", Command: "open"), Commands = [new("open", "Open", "panel.open")] };
PluginCatalog.Validate(navigationPlugin);
Check(PluginPolicies.HasNavigation(JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(navigationPlugin, JsonFormat.Options), JsonFormat.Options)!), "sidebar metadata survives manifest serialization without a host reference");
Check(!PluginPolicies.HasNavigation(navigationPlugin with { Category = "device" }), "device plugins never create sidebar links even with legacy navigation metadata");
bool invalidNavigation = false;
try { PluginCatalog.Validate(navigationPlugin with { Navigation = new(Command: "missing") }); } catch (InvalidDataException) { invalidNavigation = true; }
Check(invalidNavigation, "sidebar rejects an entry whose open command does not exist");
bool invalidPage = false;
try { PluginCatalog.Validate(navigationPlugin with { Page = new("Page", [new("command", "Missing", Command: "missing")]) }); } catch (InvalidDataException) { invalidPage = true; }
Check(invalidPage, "plugin page rejects a command button that cannot execute");
var pageSlider = new PageElement("slider", "Effect", "effect", "open")
{ Parameter = "strength", Minimum = 0, Maximum = 10, Step = 0.5, Values = new() { ["effect"] = JsonSerializer.SerializeToElement(2) } };
var declaredPage = navigationPlugin with { UiAssembly = "ui/Example.dll", Navigation = new(Glyph: "\uE8D6"), Page = new("Controls", [new("group", "Group") { Children = [pageSlider] }]) { Layout = "declarative" } };
PluginCatalog.Validate(declaredPage);
var declaredRoundTrip = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(declaredPage, JsonFormat.Options), JsonFormat.Options)!;
Check(declaredRoundTrip.Page!.Layout == "declarative" && declaredRoundTrip.Page.Elements[0].Children[0].Parameter == "strength" && declaredRoundTrip.Navigation!.Command is null, "embedded plugin controls and command-free navigation survive manifest serialization");
var pageArguments = PluginPageData.Arguments(pageSlider, 3.5, JsonSerializer.SerializeToElement(7));
Check(pageArguments["effect"].GetInt32() == 2 && pageArguments["strength"].GetDouble() == 3.5 && pageArguments["index"].GetInt32() == 7 && !pageSlider.Values.ContainsKey("strength"), "control bindings preserve fixed arguments and do not mutate declarations");
Check(PluginPageData.Equivalent(JsonSerializer.SerializeToElement("音乐"), JsonDocument.Parse("\"音乐\"").RootElement) && PluginPageData.Equivalent(JsonSerializer.SerializeToElement(10), JsonSerializer.SerializeToElement(10.0)), "dropdown selection compares values independently of JSON string escaping and number formatting");
Check(!PluginPageData.Enabled(pageSlider, new Dictionary<string, JsonElement>(), false) && !PluginPageData.Enabled(pageSlider with { DisabledKey = "running" }, new Dictionary<string, JsonElement> { ["running"] = JsonSerializer.SerializeToElement(true) }, true), "stale data and active-session constraints disable plugin controls");
Reject(() => PluginCatalog.Validate(declaredPage with { Page = new("Page", [new("group", "Nested") { Children = [pageSlider with { Command = "missing" }] }]) { Layout = "declarative" } }), "nested controls cannot reference missing commands");
Reject(() => PluginCatalog.Validate(declaredPage with { Page = new("Page", [pageSlider with { Step = 0 }]) { Layout = "declarative" } }), "invalid declared slider range is rejected before UI rendering");

var defaults = JsonSerializer.Deserialize<UserPreferences>("{}", JsonFormat.Options)!;
Check(defaults.AutomaticScenes && defaults.WatchDevices && defaults.TranslucentBackground && defaults.DefaultPluginMode == "onDemand", "old preferences gain compatible platform defaults");
var savedMode = JsonSerializer.Deserialize<PluginUserState>(JsonSerializer.Serialize(new PluginUserState([]) { Modes = new() { [firstMonitor.Id] = "onDemand" } }, JsonFormat.Options), JsonFormat.Options)!;
Check(savedMode.Modes[firstMonitor.Id] == "onDemand", "per-plugin startup modes survive persistence");
Check(JsonSerializer.Deserialize<PluginUserState>("{\"disabled\":[]}", JsonFormat.Options)!.Modes.Count == 0, "old plugin state does not fabricate explicit startup modes");
Check(!PluginPolicies.DeviceConfigurations(adapterPlugin, mouseA).Any(), "device commands do not become configurations by default");
var configurable = adapterPlugin with { Transport = "stdio-v1", Commands = [new("dpi", "DPI", "mouse.dpi") { SceneConfigurable = true }, new("battery", "Battery", "battery.read")] };
Check(PluginPolicies.DeviceConfigurations(configurable, mouseA).Select(command => command.Id).SequenceEqual(["dpi"]), "only advertised device configuration capabilities are exposed");
Check(!PluginPolicies.DeviceConfigurations(configurable, mouseA with { Pid = "9999" }).Any(), "configuration capability stays bound to the matching device");
foreach (string category in new[] { "utility", "device", "monitor", "theme" })
{
    var manifest = firstMonitor with { Category = category, ActivationPages = [] };
    bool demand = PluginPolicies.HasDemand(manifest, "plugins", manifest.Id, null, null, false);
    Check(PluginPolicies.ShouldRun("onDemand", demand)
        && !PluginPolicies.ShouldRun("onDemand", PluginPolicies.HasDemand(manifest, "settings", null, null, null, false)), "opening and leaving pages controls on-demand plugin: " + category);
}
Check(PluginPolicies.HasDemand(configurable, "home", null, null, mouseA, false), "selected device creates a matching plugin demand");
Check(!PluginPolicies.HasDemand(firstMonitor, "settings", null, null, null, false), "completed scene commands release their plugin off-page");
Check(PluginPolicies.HasDemand(firstMonitor, "settings", null, null, null, true), "pending command retains its plugin off-page");
Check(!PluginPolicies.ShouldRun("disabled", true) && PluginPolicies.ShouldRun("always", false), "explicit disable and keep-running policies override demand");
Check(PluginPolicies.HasDemand(firstMonitor with { SupportsBackgroundActivity = true }, "settings", null, null, null, false, true), "declared active background session retains an off-page plugin");
Check(!PluginPolicies.HasDemand(firstMonitor, "settings", null, null, null, false, true), "undeclared background activity does not create a demand");
Check(JsonSerializer.Deserialize<PluginManifest>("{\"id\":\"test.old\",\"name\":\"Old\",\"version\":\"1.0\",\"entryPoint\":\"old.exe\"}", JsonFormat.Options)!.ShutdownTimeoutSeconds == 2.5, "old plugin shutdown timeout remains compatible");
Reject(() => PluginCatalog.Validate(firstMonitor with { ShutdownTimeoutSeconds = 60 }), "unbounded plugin shutdown timeout rejected");
var diskValues = new Dictionary<string, JsonElement> { ["storage.disks"] = JsonSerializer.SerializeToElement(new object[] { new { id = "disk-0", name = "Disk A", total = 512, temperature = 38 }, new { id = "disk-1", name = "Disk B", total = 1024, temperature = (double?)null } }) };
var diskReadings = MonitoringData.Disks(diskValues);
Check(diskReadings.Count == 2 && diskReadings[0].Id != diskReadings[1].Id && diskReadings[0].Temperature == 38, "multiple physical disks retain independent identity and temperature");
Check(diskReadings[1].Temperature is null && MonitoringData.Disks(new Dictionary<string, JsonElement>()).Count == 0, "missing disk temperature and old monitor snapshots remain unavailable");

var oldDiskData = new Dictionary<string, JsonElement> { ["storage.volumes"] = JsonSerializer.SerializeToElement(new[] { new { id = "C:", name = "C:", total = 256 }, new { id = "D:", name = "D:", total = 512 } }) };
var oldDisks = MonitoringData.Disks(oldDiskData);
Check(oldDisks.Count == 2 && oldDisks.All(disk => !disk.IsPhysical && disk.Temperature is null) && oldDisks[1].Total == 512, "old monitor retains multiple volume capacities without inventing physical temperatures");
diskValues["storage.volumes"] = oldDiskData["storage.volumes"];
Check(MonitoringData.Disks(diskValues).All(disk => disk.IsPhysical), "physical disks take precedence over fallback volumes without duplication");
diskValues["storage.disks"] = JsonSerializer.SerializeToElement(new object[] { new { id = "disk-0", name = "Disk A", total = 512, used = 123.5, temperature = 38 }, new { id = "disk-1", name = "Disk B", total = 256, used = 0, temperature = (double?)null } });
Check(MonitoringData.Disks(diskValues)[0].Used == 123.5 && MonitoringData.Disks(diskValues)[1].Used == 0, "per-disk used capacity preserves numeric values including zero");
Check(oldDisks.All(disk => disk.Used is null), "legacy disk snapshots do not invent used capacity");

var diagnosticValues = new Dictionary<string, JsonElement> { ["diagnostics"] = JsonSerializer.SerializeToElement(MonitorDiagnostics.Describe(false, null, true), JsonFormat.Options) };
var missingDriver = PluginDiagnostics.Read(diagnosticValues);
Check(missingDriver.Count == 1 && missingDriver[0].Id == "pawnio.missing" && missingDriver[0].HelpUrl?.Host == "pawnio.eu", "missing collection driver provides its cause and an official help link");
Check(MonitorDiagnostics.Describe(true, null, true).Single().Id == "sensors.unavailable", "missing sensor values with an installed driver are not misdiagnosed as a missing driver");
Check(MonitorDiagnostics.Describe(true, "Access denied", false).Single().Id == "sensors.initialization", "sensor initialization failures retain a separate diagnosis");
Check(MonitorDiagnostics.Describe(true, null, false).Length == 0 && PluginDiagnostics.Read(new Dictionary<string, JsonElement>()).Count == 0, "healthy collection and older plugins do not show a false diagnostic");
diagnosticValues["diagnostics"] = JsonSerializer.SerializeToElement(new[] { new { id = "unsafe", title = "Help", message = "Read this", helpUrl = "file:///C:/Windows/System32/cmd.exe" } });
Check(PluginDiagnostics.Read(diagnosticValues).Single().HelpUrl is null, "plugin diagnostic links cannot launch local files");

var detailRules = new[] { Rule("detail-front", "", 5, "foreground", "game.exe") with { SceneId = "gaming" }, Rule("detail-back", "", 3, "running", "music.exe") with { SceneId = "gaming" } };
Check(detailRules.Count(rule => SceneEngine.Matches(rule, activity)) == 2 && SceneEngine.Evaluate(detailRules, activity).Count == 1, "scene detail can show every recognized app without confusing it with the winning trigger");
Check(!SceneEngine.Matches(detailRules[0], activity with { ForegroundApplication = "editor" }) && SceneEngine.Matches(detailRules[1], activity with { ForegroundApplication = "editor" }), "scene detail distinguishes foreground changes from running background apps");

string sandbox = Path.Combine(Path.GetTempPath(), "hhub-tests-" + Guid.NewGuid().ToString("N"));
var storage = new AppStorage(sandbox);
var catalog = new PluginCatalog(storage);
try
{
    string bundledFixture = Path.Combine(sandbox, "bundled-fixture");
    Directory.CreateDirectory(bundledFixture);
    void BundleFixture(string filename, PluginManifest manifest)
    {
        using var archive = ZipFile.Open(Path.Combine(bundledFixture, filename), ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("plugin.json").Open());
        writer.Write(JsonSerializer.Serialize(manifest, JsonFormat.Wire));
    }
    BundleFixture("old.hhub", declaredPage with { Version = "0.1.0", UiAssembly = null });
    BundleFixture("new.hhub", declaredPage with { Version = "0.2.0" });
    var bundled = PluginCatalog.ReadBundledPackages(bundledFixture);
    Check(bundled.Errors.Length == 0 && bundled.Packages is [{ Manifest.Version: "0.2.0" }],
        "bundled discovery validates the newest version without reporting obsolete UI packages");
    BundleFixture("broken-latest.hhub", declaredPage with { Version = "0.3.0", UiAssembly = null });
    bundled = PluginCatalog.ReadBundledPackages(bundledFixture);
    Check(bundled.Packages.Length == 0 && bundled.Errors.Length == 1 && bundled.Errors[0].StartsWith("broken-latest.hhub"),
        "a broken newest bundled version remains visible as an error");
    async Task<string> ProbeInstance(string path)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--instance-check"); info.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(info)!;
        string result = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return result.Trim();
    }
    string instanceRoot = Path.Combine(sandbox, "instance");
    using (var lease = InstanceLease.TryAcquire(instanceRoot))
    {
        Check(lease is not null && await ProbeInstance(instanceRoot) == "busy", "another host process cannot acquire the same data directory lease");
        Check(await ProbeInstance(Path.Combine(sandbox, "other-instance")) == "acquired", "independent data roots do not block each other");
    }
    Check(await ProbeInstance(instanceRoot) == "acquired", "instance lease is released after its owner exits");
    storage.Save("preferences.json", new UserPreferences("dark"));
    Check(storage.Load("preferences.json", new UserPreferences()).Theme == "dark", "preferences survive reload");
    Reject(() => PluginCatalog.ResolveInside(storage.Plugins, "../escape.exe"), "reject directory traversal");
    Reject(() => PluginCatalog.ResolveInside(storage.Plugins, "C:/escape.exe"), "reject absolute plugin paths");

    string payload = Path.Combine(sandbox, "payload");
    Directory.CreateDirectory(payload);
    foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
        File.Copy(file, Path.Combine(payload, Path.GetFileName(file)));
    var manifest = new PluginManifest
    {
        Id = "test.tool", Name = "Test fixture", Version = "1.0.0",
        EntryPoint = "Hhub.Core.Tests.exe", Transport = "stdio-v1", Arguments = ["--fixture"],
        SupportsCommandResults = true,
        SnapshotMaxAgeSeconds = 1,
        Commands = [new("apply", "Apply", "test.configuration"), new("reject", "Reject", "test.rejection")],
        Page = new("Test", [new("metric", "Status", "status")])
    };
    File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(manifest, JsonFormat.Options));
    string package = Path.Combine(sandbox, "fixture.hhub");
    ZipFile.CreateFromDirectory(payload, package);
    Check(PluginCatalog.ReadPackageManifest(package).Id == "test.tool", "package metadata is readable without execution");
    string nullManifest = Path.Combine(sandbox, "null-manifest.hhub");
    using (var archive = ZipFile.Open(nullManifest, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("plugin.json").Open()))
        writer.Write("{\"id\":\"test.null\",\"name\":\"Null\",\"version\":\"1.0\",\"entryPoint\":\"tool.exe\",\"requiredCapabilities\":null}");
    bool rejectedNull = false;
    try { PluginCatalog.ReadPackageManifest(nullManifest); } catch (JsonException) { rejectedNull = true; }
    Check(rejectedNull, "null required manifest fields fail at the package boundary instead of crashing host startup");
    var installed = catalog.Install(package);
    Check(catalog.Discover().Plugins.Single().Manifest.Id == "test.tool", "package installs and is discovered");
    Reject(() => catalog.Install(package), "existing installation is not overwritten");
    int commonAppArgument = Array.IndexOf(args, "--common-app");
    if (commonAppArgument >= 0)
    {
        await using var commonRuntime = new PluginRuntime { SharedRuntimeHostPath = Path.GetFullPath(args[commonAppArgument + 1]),
            SharedRuntimeHostArguments = ["--plugin"], PreferSharedManagedBackends = true };
        await commonRuntime.SendAsync(installed, "apply");
        Check(commonRuntime.Snapshot(installed.Manifest.Id)["status"].GetString() == "received", "legacy managed EXE package runs its DLL under the common application executable and completes command roundtrip");
        await commonRuntime.StopAsync(installed.Manifest.Id);
        Check(!commonRuntime.HasSession(installed.Manifest.Id), "common application worker honors shutdown and leaves no owned process");
    }

    Reject(() => PluginCatalog.Validate(manifest with { SchemaVersion = 2 }), "reject unsupported protocol version");
    Reject(() => PluginCatalog.Validate(manifest with { Commands = [new("apply", "A", "a"), new("apply", "B", "b")] }), "reject duplicate command IDs");

    string malicious = Path.Combine(sandbox, "malicious.zip");
    using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) writer.Write("escape");
    Reject(() => catalog.Install(malicious), "reject archive extraction outside staging");
    Check(!File.Exists(Path.Combine(sandbox, "staging", "escaped.txt")), "no escaped archive file created");
    Reject(() => PluginCatalog.ResolveInside(storage.Plugins, "payload/COM¹.txt"), "reject Windows device names inside archives");
    Reject(() => PluginCatalog.Validate(manifest with { Id = "test.tool." }), "reject plugin IDs that alias another Windows directory");
    Reject(() => PluginCatalog.Validate(manifest with { Commands = [null!] }), "reject null command elements before accessing their fields");
    string oversizedManifest = Path.Combine(sandbox, "oversized-manifest.hhub");
    using (var zip = ZipFile.Open(oversizedManifest, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
        writer.Write(new string(' ', 1024 * 1024 + 1));
    Reject(() => PluginCatalog.ReadPackageManifest(oversizedManifest), "reject a highly compressed oversized manifest during preflight");

    await using (var runtime = new PluginRuntime())
    {
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Changed += () =>
        {
            if (runtime.Snapshot("test.tool").TryGetValue("status", out var status))
            {
                if (status.GetString() == "ready") ready.TrySetResult();
                if (status.GetString() == "received") received.TrySetResult();
            }
        };
        runtime.Start(installed);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(runtime.IsRunning("test.tool"), "independent executable starts and reports snapshot");
        await runtime.SendAsync(installed, "apply");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(true, "generic JSON command reaches plugin");
        bool rejected = false;
        try { await runtime.SendAsync(installed, "reject"); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "plugin command rejection reaches host");
        Check(runtime.Latest("test.tool") is not null && runtime.IsFresh(installed.Manifest), "snapshot arrival time is tracked");
        await Task.Delay(1150);
        Check(!runtime.IsFresh(installed.Manifest), "stale snapshot is not treated as current data");
        await Task.WhenAll(runtime.StopAsync("test.tool"), runtime.StopAsync("test.tool"));
        Check(!runtime.IsRunning("test.tool"), "shutdown stops managed process");
        Check(!runtime.HasSession("test.tool"), "concurrent stop requests release the shared session once");
    }

    await using (var runtime = new PluginRuntime())
    {
        var commands = installed.Manifest.Commands.Concat(new[] { "hold", "spoof", "malformed", "bad-result", "oversized", "exit" }.Select(id => new PluginCommand(id, id, id))).ToArray();
        string starts = Path.Combine(sandbox, "fixture-starts.log");
        var controlled = installed with { Manifest = installed.Manifest with { Commands = commands, Arguments = ["--fixture", "--record-start", starts] } };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => runtime.EnsureStartedAsync(controlled)));
        await WaitUntil(() => runtime.IsFresh(controlled.Manifest));
        Check(File.ReadAllLines(starts).Length == 1 && runtime.State(controlled.Manifest.Id) == PluginRunState.Running, "concurrent startup creates one plugin process");
        Task held = runtime.SendAsync(controlled, "hold");
        await WaitUntil(() => runtime.Snapshot(controlled.Manifest.Id).GetValueOrDefault("status").ToString() == "held");
        string heldRequest = runtime.Snapshot(controlled.Manifest.Id)["requestId"].GetString()!;
        var other = controlled with { Manifest = controlled.Manifest with { Id = "test.other", Arguments = ["--fixture"] } };
        await runtime.SendAsync(other, "spoof", new Dictionary<string, JsonElement> { ["target"] = JsonSerializer.SerializeToElement(heldRequest) });
        Check(!held.IsCompleted, "another plugin cannot acknowledge a pending command it does not own");
        await runtime.StopAsync(controlled.Manifest.Id);
        bool cancelled = false;
        try { await held.WaitAsync(TimeSpan.FromSeconds(2)); } catch (IOException) { cancelled = true; }
        Check(cancelled && runtime.State(controlled.Manifest.Id) == PluginRunState.Stopped, "stopping immediately releases pending commands without a false success");
        await runtime.StopAsync(other.Manifest.Id);
        foreach (string faulty in new[] { "malformed", "bad-result", "oversized", "exit" })
        {
            bool failed = false;
            try { await runtime.SendAsync(controlled, faulty).WaitAsync(TimeSpan.FromSeconds(4)); } catch (IOException) { failed = true; }
            await WaitUntil(() => !runtime.HasSession(controlled.Manifest.Id));
            Check(failed && runtime.State(controlled.Manifest.Id) == PluginRunState.Faulted, faulty + " terminates the broken session and fails its pending command");
            await runtime.SendAsync(controlled, "apply");
            Check(runtime.State(controlled.Manifest.Id) == PluginRunState.Running && runtime.Error(controlled.Manifest.Id) is null, "explicit retry recovers after " + faulty);
        }
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => runtime.SendAsync(controlled, "apply")));
        Check(runtime.State(controlled.Manifest.Id) == PluginRunState.Running, "concurrent commands retain their independent acknowledgements");
        await runtime.StopAsync(controlled.Manifest.Id);
        var slow = controlled with { Manifest = controlled.Manifest with { Arguments = ["--fixture", "--slow-stop"] } };
        runtime.Start(slow); await WaitUntil(() => runtime.IsFresh(slow.Manifest));
        Task stop = runtime.StopAsync(slow.Manifest.Id);
        await WaitUntil(() => runtime.State(slow.Manifest.Id) == PluginRunState.Stopping);
        Task restart = runtime.SendAsync(slow, "apply");
        await Task.WhenAll(stop, restart);
        Check(runtime.State(slow.Manifest.Id) == PluginRunState.Running, "a command arriving during stop waits for the old session before starting a new one");
        await runtime.StopAsync(slow.Manifest.Id);
    }

    var nextManifest = manifest with { Version = "1.1.0" };
    var closedRuntime = new PluginRuntime();
    await closedRuntime.DisposeAsync(); await closedRuntime.DisposeAsync();
    bool refusedAfterExit = false;
    try { closedRuntime.Start(installed); } catch (ObjectDisposedException) { refusedAfterExit = true; }
    Check(refusedAfterExit && !closedRuntime.HasSession(installed.Manifest.Id), "closing the runtime is idempotent and cannot spawn orphan plugin processes afterward");
    await using (var dispatched = new PluginRuntime())
    {
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        dispatched.Dispatch = action => notifications.Enqueue(action);
        int delivered = 0; dispatched.Changed += () => delivered++;
        var broken = installed with { Manifest = installed.Manifest with { Commands = [new("malformed", "Malformed", "protocol.test")] } };
        bool failed = false;
        try { await dispatched.SendAsync(broken, "malformed").WaitAsync(TimeSpan.FromSeconds(4)); } catch (IOException) { failed = true; }
        await WaitUntil(() => !dispatched.HasSession(broken.Manifest.Id));
        Check(failed && delivered == 0 && dispatched.Error(broken.Manifest.Id) is not null, "background fault cleanup preserves its cause without calling UI listeners directly");
        while (notifications.TryDequeue(out var notify)) notify();
        Check(delivered > 0, "plugin notifications run through the supplied UI dispatcher");
    }
    await using (var coalesced = new PluginRuntime())
    {
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        coalesced.Dispatch = action => notifications.Enqueue(action);
        var fixture = installed with { Manifest = installed.Manifest with { Commands = [new("burst", "Burst", "protocol.test"), new("stderr-burst", "Diagnostics", "protocol.test")] } };
        await coalesced.SendAsync(fixture, "burst");
        var latest = coalesced.Snapshot(fixture.Manifest.Id);
        Check(latest["index"].GetInt32() == 128 && latest["samples"][1].GetInt32() == 129 && notifications.Count <= 2,
            "a stalled dispatcher retains only the newest telemetry and one snapshot notification during a burst");
        await coalesced.SendAsync(fixture, "stderr-burst");
        await WaitUntil(() => coalesced.Error(fixture.Manifest.Id) == "diagnostic 128");
        Check(notifications.Count <= 2, "stderr bursts retain only the newest diagnostic and share the pending UI notification");
    }
    await using (var runtime = new PluginRuntime())
    {
        var dispatcher = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var firstSnapshots = new List<string>();
        runtime.Dispatch = dispatcher.Enqueue;
        runtime.FirstSnapshotReceived += firstSnapshots.Add;
        await runtime.EnsureStartedAsync(installed);
        await WaitUntil(() => runtime.Latest(installed.Manifest.Id) is not null);
        await runtime.StopAsync(installed.Manifest.Id);
        await runtime.EnsureStartedAsync(installed);
        await WaitUntil(() => runtime.Latest(installed.Manifest.Id) is not null);
        Check(firstSnapshots.Count == 0, "first snapshot notification waits for the host dispatcher");
        await runtime.SendAsync(installed, "apply");
        while (dispatcher.TryDequeue(out var notify)) notify();
        Check(firstSnapshots.SequenceEqual([installed.Manifest.Id]),
            "only the active process sends a first snapshot notification and later frames use normal batching");
    }
    await using (var broker = new PluginRuntime())
    {
        var fixture = installed with { Manifest = installed.Manifest with
        {
            RequiredCapabilities = ["host.remote.v1"],
            Commands = [new("query-host", "Read host", "protocol.test")]
        } };
        broker.HostRequest = (plugin, action, values) => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            ready = plugin == fixture && action == "state.read" && values.GetProperty("monitor").GetBoolean()
        }));
        await broker.SendAsync(fixture, "query-host").WaitAsync(TimeSpan.FromSeconds(5));
        Check(broker.IsRunning(fixture.Manifest.Id), "a declared plugin can receive a correlated host reply while a command is pending");
    }
    File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(nextManifest, JsonFormat.Options));
    string updatePackage = Path.Combine(sandbox, "update.hhub");
    ZipFile.CreateFromDirectory(payload, updatePackage);
    var batch = PluginBatch.Plan([package, Path.Combine(sandbox,"missing.hhub"), updatePackage, package, Path.Combine(sandbox,"readme.txt")]);
    Check(batch.Packages is [{ Manifest.Version: "1.1.0" }] && batch.Issues.Length == 2 && batch.Skipped.Length == 1,
        "batch preflight keeps the highest version, ignores duplicate paths and isolates bad files");
    string swapped = Path.Combine(sandbox, "swapped.hhub");
    File.Copy(updatePackage, swapped);
    var confirmed = PluginBatch.Plan([swapped]).Packages.Single();
    using (var zip = ZipFile.Open(swapped, ZipArchiveMode.Update))
    using (var writer = new StreamWriter(zip.CreateEntry("changed.txt").Open())) writer.Write("changed after confirmation");
    Reject(() => catalog.Install(confirmed, replace: true), "reject package substitution even when the manifest identity and version match");
    Check(catalog.Discover().Plugins.Single().Manifest.Version == "1.0.0", "package substitution leaves the working installation intact");
    installed = catalog.Install(batch.Packages.Single(), replace: true);
    Check(installed.Manifest.Version == "1.1.0" && catalog.Discover().Plugins.Single().Manifest.Version == "1.1.0", "plugin update replaces a stopped installation");
    string pendingUpdates = Path.Combine(storage.Root, "plugin-updates");
    string pluginTrash = Path.Combine(storage.Root, "plugin-trash");
    Check(Directory.GetDirectories(pendingUpdates).Length == 0 && Directory.GetDirectories(pluginTrash).Length == 0,
        "a successful update clears the pending old version and retired files");
    Reject(() => catalog.Install(package, replace: true), "ordinary package installation cannot downgrade a plugin");
    Reject(() => catalog.Install(updatePackage, replace: true), "ordinary package installation cannot overwrite the same version");
    File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(nextManifest with { Version = "1.2.0" }, JsonFormat.Options));
    string candidatePackage = Path.Combine(sandbox, "startup-check.hhub");
    ZipFile.CreateFromDirectory(payload, candidatePackage);
    var candidate = PluginBatch.Plan([candidatePackage]).Packages.Single();
    bool verificationFailed = false;
    try
    {
        await catalog.InstallAsync(candidate, replace: true, verifyUpdate: installedCandidate =>
        {
            Check(installedCandidate.Manifest.Version == "1.2.0" && catalog.Discover().Plugins.Count == 0,
                "candidate is available to the verifier before it is published");
            return Task.FromException(new IOException("simulated startup failure"));
        });
    }
    catch (IOException error) when (error.Message == "simulated startup failure") { verificationFailed = true; }
    Check(verificationFailed && catalog.Discover().Plugins.Single().Manifest.Version == "1.1.0",
        "failed startup validation restores the old version");
    string pending = Path.Combine(pendingUpdates, installed.Manifest.Id);
    Directory.Move(installed.Directory, pending);
    ZipFile.ExtractToDirectory(candidatePackage, installed.Directory);
    var reopenedCatalog = new PluginCatalog(new AppStorage(storage.Root));
    Check(reopenedCatalog.RecoverInterruptedUpdates().Length == 0
        && reopenedCatalog.Discover().Plugins.Single().Manifest.Version == "1.1.0"
        && !Directory.Exists(pending), "startup restores the previous version after an interrupted swap");
    Directory.Move(installed.Directory, pending);
    Check(reopenedCatalog.RecoverInterruptedUpdates().Length == 0
        && reopenedCatalog.Discover().Plugins.Single().Manifest.Version == "1.1.0",
        "startup restores the previous version after an interruption before the candidate was moved");
    var invalidUpdate = nextManifest with { SchemaVersion = 2 };
    File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(invalidUpdate, JsonFormat.Options));
    string invalidPackage = Path.Combine(sandbox, "invalid-update.hhub");
    ZipFile.CreateFromDirectory(payload, invalidPackage);
    Reject(() => catalog.Install(invalidPackage, replace: true), "invalid update is rejected before replacing the working version");
    Check(catalog.Discover().Plugins.Single().Manifest.Version == "1.1.0", "working version survives a rejected update");
    File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(nextManifest with { MinimumHostVersion = "0.6.0" }, JsonFormat.Options));
    string incompatiblePackage = Path.Combine(sandbox, "incompatible.hhub"); ZipFile.CreateFromDirectory(payload, incompatiblePackage);
    Reject(() => catalog.Install(incompatiblePackage, replace: true), "incompatible upgrades never replace the working plugin");
    Check(catalog.Discover().Plugins.Single().Manifest.Version == "1.1.0", "compatibility rejection leaves the installed version intact");
    File.WriteAllText(Path.Combine(payload, "plugin.json"), JsonSerializer.Serialize(nextManifest with { UiAssembly = "missing-ui.dll" }, JsonFormat.Options));
    string incompletePackage = Path.Combine(sandbox, "incomplete.hhub"); ZipFile.CreateFromDirectory(payload, incompletePackage);
    Reject(() => catalog.Install(incompletePackage, replace: true), "packages missing their declared UI assembly cannot install");
    string incompatibleFolder = Path.Combine(storage.Plugins, "future-plugin"); Directory.CreateDirectory(incompatibleFolder);
    File.WriteAllText(Path.Combine(incompatibleFolder, "plugin.json"), JsonSerializer.Serialize(nextManifest with { MinimumHostVersion = "0.6.0" }, JsonFormat.Options));
    Check(catalog.Discover().Plugins.Count == 1 && catalog.Discover().Errors.Count == 1, "an incompatible installed plugin does not prevent other plugins from loading");
    File.Delete(Path.Combine(incompatibleFolder, "plugin.json")); Directory.Delete(incompatibleFolder);
    string legacyBackup = Path.Combine(storage.Root, "backups", installed.Manifest.Id + "-" + Guid.NewGuid().ToString("N"));
    string sceneBackup = Path.Combine(storage.Root, "backups", "scene-presets-" + Guid.NewGuid().ToString("N"));
    string uiCache = Path.Combine(storage.Root, "ui-cache", installed.Manifest.Id);
    Directory.CreateDirectory(legacyBackup);
    Directory.CreateDirectory(sceneBackup);
    Directory.CreateDirectory(uiCache);
    Directory.Delete(pluginTrash);
    File.WriteAllText(pluginTrash, "block the move to the trash directory");
    bool uninstallFailed = false;
    try { catalog.Uninstall(installed); }
    catch (IOException) { uninstallFailed = true; }
    Check(uninstallFailed && catalog.Discover().Plugins.Single().Manifest.Version == "1.1.0",
        "failed uninstall before the move leaves the active plugin intact");
    File.Delete(pluginTrash);
    Check(catalog.Uninstall(installed).Length == 0 && catalog.Discover().Plugins.Count == 0
        && !Directory.Exists(legacyBackup) && !Directory.Exists(uiCache) && Directory.Exists(sceneBackup),
        "uninstall removes package, plugin backups and UI cache while preserving scene backups");
    Check(catalog.Uninstall(installed).Length == 0, "uninstall can be retried for an already removed package");

    int monitorArgument = Array.IndexOf(args, "--monitor-package");
    if (monitorArgument >= 0)
    {
        var monitor = catalog.Install(args[monitorArgument + 1]);
        string settings = Path.Combine(sandbox, "monitor-settings.json");
        File.WriteAllText(settings, """{"IntervalSeconds":1,"AutomaticSensorAccess":false}""");
        var isolated = monitor.Manifest with { Arguments = ["--settings", settings] };
        File.WriteAllText(Path.Combine(monitor.Directory, "plugin.json"), JsonSerializer.Serialize(isolated, JsonFormat.Options));
        monitor = PluginCatalog.Read(monitor.Directory);
        await using (var runtime = new PluginRuntime())
        {
            TaskCompletionSource sampled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Changed += () =>
            {
                var data = runtime.Snapshot(isolated.Id);
                if (data.TryGetValue("memory.total", out var total) && total.TryGetDouble(out double size) && size > 0
                    && data.TryGetValue("cpu.load", out var load) && load.ValueKind == JsonValueKind.Number) sampled.TrySetResult();
            };
            runtime.Start(monitor);
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(true, "real monitor sends CPU and memory through independent plugin protocol");
            var memoryData = runtime.Snapshot(isolated.Id);
            bool driverInstalled = memoryData["monitor.pawnIoInstalled"].GetBoolean();
            Check(new[] { "cpu.power", "gpu.power", "gpu.memoryUsed", "gpu.memoryTotal", "gpu.memoryTemperature" }.All(key =>
                memoryData.TryGetValue(key, out var measurement) && measurement.ValueKind is JsonValueKind.Number or JsonValueKind.Null),
                "monitor publishes power in watts and VRAM capacity as numeric or explicitly unavailable fields");
            Check(memoryData.TryGetValue("cpu.cores", out var cores) && cores.GetInt32() > 0
                && memoryData.TryGetValue("cpu.threads", out var threads) && threads.GetInt32() >= cores.GetInt32(),
                "monitor publishes physical cores and logical threads as distinct values");
            Check(memoryData["hardware.sensors"].EnumerateArray().All(sensor => sensor.GetProperty("id").ValueKind == JsonValueKind.String
                && sensor.GetProperty("name").ValueKind == JsonValueKind.String && sensor.TryGetProperty("value", out _))
                && memoryData["network.adapters"].EnumerateArray().All(adapter => adapter.GetProperty("id").ValueKind == JsonValueKind.String
                    && adapter.TryGetProperty("download", out _) && adapter.TryGetProperty("upload", out _)),
                "monitor sensor and network row payloads retain the existing wire format");
            Check(memoryData.TryGetValue("monitor.version", out var backendVersion) && backendVersion.GetString() == monitor.Manifest.Version,
                "running monitoring backend reports the same version as its installed manifest");
            Check(memoryData.TryGetValue("monitor.pawnIoAccessible", out var driverAccess) && driverAccess.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "monitor checks driver access independently from the installation record");
            Check(memoryData.ContainsKey("diagnostics") && (driverInstalled || PluginDiagnostics.Read(memoryData).Any(notice => notice.Id == "pawnio.missing")), "real monitor reports dependency status with actionable diagnostics when the driver is absent");
            Check(memoryData.TryGetValue("memory.temperature", out var ramTemperature) && ramTemperature.ValueKind is JsonValueKind.Number or JsonValueKind.Null, "monitor provides actual or explicitly unavailable memory temperature");
            Check(memoryData.TryGetValue("memory.clock", out var ramClock) && ramClock.ValueKind is JsonValueKind.Number or JsonValueKind.Null, "monitor provides configured memory frequency or an explicit missing value");
            var physicalDisks = MonitoringData.Disks(runtime.Snapshot(isolated.Id));
            Check(physicalDisks.Count > 0 && physicalDisks.Select(disk => disk.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == physicalDisks.Count, "real monitor enumerates physical disks without duplicating identities");
            await runtime.SendAsync(monitor, "pause");
            Check(runtime.Snapshot(isolated.Id)["monitor.paused"].GetBoolean(), "monitor confirms pause");
            var pausedFrame = runtime.Latest(isolated.Id)!;
            await WaitUntil(() => runtime.Latest(isolated.Id)?.ReceivedAtUtc > pausedFrame.ReceivedAtUtc);
            Check(pausedFrame.CapturedAtUtc is not null && runtime.Latest(isolated.Id)!.CapturedAtUtc == pausedFrame.CapturedAtUtc,
                "paused monitor keeps publishing cached frames without collecting a new sample");
            await runtime.SendAsync(monitor, "setInterval", new Dictionary<string, JsonElement> { ["seconds"] = JsonSerializer.SerializeToElement(2) });
            Check(runtime.Snapshot(isolated.Id)["monitor.intervalSeconds"].GetDouble() == 2, "monitor applies interval and confirms result");
            bool invalidIntervalRejected = false;
            try { await runtime.SendAsync(monitor, "setInterval", new Dictionary<string, JsonElement> { ["seconds"] = JsonSerializer.SerializeToElement(0) }); }
            catch (InvalidOperationException) { invalidIntervalRejected = true; }
            Check(invalidIntervalRejected, "monitor rejects unsupported sample interval");
            await runtime.SendAsync(monitor, "resume");
            Check(!runtime.Snapshot(isolated.Id)["monitor.paused"].GetBoolean(), "monitor resumes collection");
            var daily = new SceneProfile("daily", "Daily", "") { Actions = [new(isolated.Id, "setInterval", "monitor.interval", new() { ["seconds"] = JsonSerializer.SerializeToElement(1) })] };
            var gaming = new SceneProfile("gaming", "Gaming", "") { Actions = [new(isolated.Id, "setInterval", "monitor.interval", new() { ["seconds"] = JsonSerializer.SerializeToElement(5) })] };
            var trigger = new SceneRule { Name = "Game", Application = "game", SceneId = gaming.Id };
            foreach (var context in new[] { activity, activity with { ForegroundApplication = "editor" } })
            {
                string effective = SceneEngine.EffectiveScene([trigger], context, daily.Id, false);
                var active = effective == gaming.Id ? gaming : daily;
                foreach (var action in ScenePlan.Build(active, [], new HashSet<string>())) await runtime.SendAsync(monitor, action.Command, action.Values);
                Check(runtime.Snapshot(isolated.Id)["monitor.intervalSeconds"].GetDouble() == (effective == gaming.Id ? 5 : 1), "scene transition applies real plugin configuration: " + effective);
            }
            await runtime.StopAsync(isolated.Id);
            Check(!runtime.IsRunning(isolated.Id), "real monitor shuts down cleanly");
        }
        catalog.Uninstall(monitor);
        Check(catalog.Discover().Plugins.Count == 0, "real monitor can be removed after stop");
    }
    int audioArgument = Array.IndexOf(args, "--audio-package");
    if (audioArgument >= 0)
    {
        var audio = catalog.Install(args[audioArgument + 1]);
        audio = audio with { Manifest = audio.Manifest with { Arguments = ["--data-dir", Path.Combine(sandbox, "audio-data")] } };
        Check(audio.Manifest.SupportsBackgroundActivity && audio.Manifest.ShutdownTimeoutSeconds == 8, "audio package declares background sessions and bounded graceful shutdown");
        Check(audio.Manifest.Page?.Layout == "declarative" && audio.Manifest.Navigation?.Command is null && !audio.Manifest.Commands.Any(command => command.Id == "openUi"), "audio opens directly inside Hhub without an external-window command");
        Check(audio.Manifest.Name == "FXsound" && audio.Manifest.Page!.HeaderElements.Any(element => element.Type == "toggle" && element.Command == "start" && element.OffCommand == "stop"), "FXsound has a real master switch next to its page title");
        Check(audio.Manifest.Page!.HeaderElements is [{ Type: "toggle", ShowStateText: false }]
            && audio.Manifest.Page.HeaderPlacement == "right" && !audio.Manifest.Page.ShowSettingsShortcut && !audio.Manifest.Page.ShowManagementActions,
            "FXsound header contains only a text-free master switch on the right");
        Check(audio.Manifest.SettingsPage is null && !audio.Manifest.RequiredCapabilities.Any(capability => capability is "settings.page.v1" or "settings.embedded.v1"),
            "audio settings navigation is owned by the plugin without host settings capabilities");
        using (var uiStream=File.OpenRead(Path.Combine(audio.Directory,"ui","Hhub.Audio.Ui.dll")))
        using (var uiPe=new System.Reflection.PortableExecutable.PEReader(uiStream))
        {
            var metadata=uiPe.GetMetadataReader();
            Check(!metadata.MemberReferences.Select(handle=>metadata.GetString(metadata.GetMemberReference(handle).Name))
                .Any(name=>name is "get_OpenSettings" or "get_CloseSettings" or "get_GoBack"),
                "audio UI binary does not call the new host navigation APIs");
        }
        var audioControls = audio.Manifest.Page.Elements.Single(element=>element.Type=="columns" && element.Fill).Children[0];
        Check(new[]{"setMasterGain","setVolumeLeveling","setBalance","setQ","setEffect"}.All(command=>audioControls.Children.Any(element=>element.Command==command))
            && !File.Exists(Path.Combine(audio.Directory,"ui","AudioSettingsPage.json")), "advanced audio parameters live on the main page without a settings subpage");
        Check(audio.Manifest.Page.Elements[0].Children[0] is { Type: "toolbar", Children: [{ Command: "applyPreset" }, { Command: "savePreset" }] },
            "save preset belongs next to the main-page preset selector");
        var audioBandRow=audioControls.Children[^2];
        var audioActions=audioControls.Children[^1];
        Check(audio.Manifest.Page.Elements.Length==4 && audioBandRow.Type=="toolbar"
            && audioBandRow.Children.Any(element=>element.Type=="text" && element.Label=="EQ 频段")
            && audioBandRow.Children.Single(element=>element.Command=="setBands").Width==100
            && !audioBandRow.Children.Any(element=>element.Type=="command")
            && audioActions.Children.Any(element=>element.Command=="installDriver" && element.Key=="audio.driverStatus"),
            "EQ band count has its own labeled row and small selector, separate from reset and driver actions");
        Check(audio.Manifest.Page!.Elements[1] is { Type: "spectrum", Key: "audio.spectrum", Height: 128 }
            && audio.Manifest.Page.Elements[0].Children[1].DisabledKey == "audio.outputLocked",
            "main page restores the audio spectrum above EQ controls and permits live output switching");
        Check(audio.Manifest.Page!.Elements.Any(element => element.Type == "columns" && element.Fill)
            && !JsonSerializer.Serialize(audio.Manifest.Page, JsonFormat.Wire).Contains("\"type\":\"settings\""), "primary FXsound controls remain on a viewport-filling functional page");
        int sharedHostArgument = Array.IndexOf(args, "--shared-host");
        await using (var runtime = new PluginRuntime { SharedRuntimeHostPath = sharedHostArgument >= 0 ? Path.GetFullPath(args[sharedHostArgument + 1]) : Path.Combine(AppContext.BaseDirectory, "Hhub.PluginHost.exe") })
        {
            runtime.Start(audio);
            Check(audio.Manifest.EntryPoint == "Hhub.Audio.dll" && audio.Manifest.RequiredCapabilities.Contains("runtime.dotnet10.shared.v1")
                && !File.Exists(Path.Combine(audio.Directory, "Hhub.Audio.exe")) && !File.Exists(Path.Combine(audio.Directory,"coreclr.dll")),
                "audio backend starts in the host runtime without shipping its own executable or CLR");
            await runtime.SendAsync(audio, "applyPreset", new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("Music") });
            Check(runtime.Snapshot(audio.Manifest.Id)["audio.preset"].GetString() == "Music", "real audio package applies official FxSound preset through the host protocol");
            string musicBands = runtime.Snapshot(audio.Manifest.Id)["audio.bands"].GetRawText();
            double musicEffect = runtime.Snapshot(audio.Manifest.Id)["audio.effect.clarity"].GetDouble();
            await runtime.SendAsync(audio, "applyPreset", new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("Voice") });
            Check(runtime.Snapshot(audio.Manifest.Id)["audio.preset"].GetString() == "Voice"
                && runtime.Snapshot(audio.Manifest.Id)["audio.bands"].GetRawText() != musicBands
                && runtime.Snapshot(audio.Manifest.Id)["audio.effect.clarity"].GetDouble() != musicEffect,
                "switching official presets republishes both EQ and effect values used by the UI");
            Check(audio.Manifest.UiAssembly == "ui/Hhub.Audio.Ui.dll"
                && File.Exists(Path.Combine(audio.Directory,"ui","Hhub.Audio.Ui.dll"))
                && Directory.GetFiles(Path.Combine(audio.Directory,"presets","factory"),"*.fac",SearchOption.AllDirectories).Length == 25,
                "audio package owns its UI module and 25 original FxSound preset files");
            var pageState = runtime.Snapshot(audio.Manifest.Id);
            Check(pageState["audio.bands"].GetArrayLength() == 10 && pageState["audio.presetOptions"].GetArrayLength() >= 5, "audio publishes complete EQ and preset choices for host controls");
            foreach(var edit in new[]{("setMasterGain","gain",2.0),("setVolumeLeveling","leveling",1.0),("setBalance","balance",2.0),("setQ","q",1.5)})
            {
                await runtime.SendAsync(audio,"applyPreset",new Dictionary<string,JsonElement>{["name"]=JsonSerializer.SerializeToElement("Voice")});
                Check(runtime.Snapshot(audio.Manifest.Id)["audio.masterGain"].GetDouble()==0
                    && runtime.Snapshot(audio.Manifest.Id)["audio.volumeLeveling"].GetDouble()==0
                    && runtime.Snapshot(audio.Manifest.Id)["audio.balance"].GetDouble()==0
                    && runtime.Snapshot(audio.Manifest.Id)["audio.q"].GetDouble()==1,"selecting an official preset restores all advanced values");
                await runtime.SendAsync(audio,edit.Item1,new Dictionary<string,JsonElement>{[edit.Item2]=JsonSerializer.SerializeToElement(edit.Item3)});
                Check(runtime.Snapshot(audio.Manifest.Id)["audio.preset"].GetString()=="自定义","editing "+edit.Item1+" keeps the preset label consistent with modified parameters");
            }
            for(int band=0;band<10;band++)
            {
                var before=runtime.Snapshot(audio.Manifest.Id)["audio.bands"][band];
                double frequency=Math.Sqrt(before.GetProperty("minimumFrequency").GetDouble()*before.GetProperty("maximumFrequency").GetDouble());
                await runtime.SendAsync(audio,"setBandFrequency",new Dictionary<string,JsonElement>{["band"]=JsonSerializer.SerializeToElement(band),["frequency"]=JsonSerializer.SerializeToElement(frequency)});
                var after=runtime.Snapshot(audio.Manifest.Id)["audio.bands"][band];
                Check(Math.Abs(after.GetProperty("frequency").GetDouble()-frequency)<0.1
                    && after.GetProperty("gain").GetDouble()==before.GetProperty("gain").GetDouble(),
                    $"frequency knob {band+1} command reaches native DSP and republishes its confirmed frequency without changing gain");
            }
            await runtime.SendAsync(audio, "setEffect", new Dictionary<string, JsonElement> { ["effect"] = JsonSerializer.SerializeToElement(0), ["value"] = JsonSerializer.SerializeToElement(2.5) });
            Check(runtime.Snapshot(audio.Manifest.Id)["audio.effect.clarity"].GetDouble() == 2.5, "embedded slider command is confirmed by real audio state");
            await runtime.SendAsync(audio, "setVolumeLeveling", new Dictionary<string, JsonElement> { ["leveling"] = JsonSerializer.SerializeToElement(2) });
            Check(runtime.Snapshot(audio.Manifest.Id)["audio.volumeLeveling"].GetDouble() == 2, "settings volume leveling reaches native DSP and is confirmed");
            await runtime.SendAsync(audio, "resetAdvanced");
            Check(runtime.Snapshot(audio.Manifest.Id)["audio.volumeLeveling"].GetDouble() == 0
                && runtime.Snapshot(audio.Manifest.Id)["audio.effect.clarity"].GetDouble() == 2.5, "resetting advanced parameters preserves main-page effects");
            await runtime.SendAsync(audio, "setBands", new Dictionary<string, JsonElement> { ["bands"] = JsonSerializer.SerializeToElement(31) });
            Check(runtime.Snapshot(audio.Manifest.Id)["audio.bands"].GetArrayLength() == 31, "host EQ controls can switch dynamically to 31 real bands");
            Check(!runtime.Snapshot(audio.Manifest.Id)["audio.running"].GetBoolean() && !runtime.HasBackgroundActivity(audio.Manifest), "configuring DSP does not claim a live audio session");
            bool rejected = false;
            try { await runtime.SendAsync(audio, "setBand", new Dictionary<string, JsonElement> { ["band"] = JsonSerializer.SerializeToElement(99), ["gain"] = JsonSerializer.SerializeToElement(2) }); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "real audio validation failure reaches host");
            await runtime.StopAsync(audio.Manifest.Id);
            Check(!runtime.HasSession(audio.Manifest.Id), "real audio package shuts down within its declared timeout");
        }
        catalog.Uninstall(audio);
    }
}
finally { Directory.Delete(sandbox, true); }
Console.WriteLine($"{passed} checks passed.");
