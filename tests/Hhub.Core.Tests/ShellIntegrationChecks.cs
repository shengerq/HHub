using Hhub.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class ShellIntegrationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var plugin = new InstalledPlugin(new PluginManifest { Id = "test.battery", Name = "Battery provider", Version = "1.0.0",
            EntryPoint = "worker.exe", Transport = "stdio-v1", DeviceMatches = [new("1234", "5678")],
            Commands = [new("battery", "Read battery", "device.battery.read")], BatteryTelemetry = new("battery", 10) }, Path.GetTempPath());
        var a = new DeviceRecord("a", "Mouse", "鼠标", "USB", "1234", "5678", "path-a") { MemberInstanceIds = ["endpoint-a"] };
        var b = a with { InstanceId = "b", MemberInstanceIds = [], InterfacePath = "path-b" };
        var cache = new TrayBatteryCache(); var now = DateTimeOffset.UtcNow;
        var values = new Dictionary<string, JsonElement> { ["devices"] = JsonSerializer.SerializeToElement(new[] {
            new { instanceId = "endpoint-a", battery = 42 }, new { instanceId = "b", battery = 99 } }) };
        cache.Observe(plugin, [a, b], new(now, now, values));
        var enabled = new HashSet<string> { plugin.Manifest.Id };
        var read = cache.Read([a, b], enabled, device => device.Name, now.AddSeconds(5));
        check(read.Count == 2 && read[0].Battery == 42 && read[1].Battery == 99,
            "tray batteries bind exact device instances including member endpoints; identical device names remain distinct");
        check(cache.Read([a, b], enabled, device => device.Name, now.AddSeconds(11)).All(item => item.Battery is null),
            "expired tray battery values become unknown rather than being displayed as live values");
        check(cache.Read([a, b], new HashSet<string>(), device => device.Name, now).All(item => item.Battery is null),
            "disabled or uninstalled providers cannot keep supplying cached tray battery values");
        cache.Read([a], enabled, device => device.Name, now);
        check(cache.Read([a, b], enabled, device => device.Name, now)[1].Battery is null, "disconnected devices lose their cached battery readings");
        values["devices"] = JsonSerializer.SerializeToElement(new[] { new { instanceId = "a", battery = (double?)null } });
        cache.Observe(plugin, [a, b], new(now.AddSeconds(1), now, values));
        check(cache.Read([a], enabled, device => device.Name, now.AddSeconds(2))[0].Battery is null,
            "a provider explicitly reporting unavailable battery clears the previous reading");
        var preferences = JsonSerializer.Deserialize<UserPreferences>("{}", JsonFormat.Wire)!;
        check(preferences.CloseToTray && preferences.MinimizeToTray && preferences.EfficiencyInTray && preferences.TrayBattery,
            "older preference files migrate to tray closing, minimizing, battery and efficiency defaults");
        PluginCatalog.Validate(plugin.Manifest);
        bool rejected = false;
        try { PluginCatalog.Validate(plugin.Manifest with { BatteryTelemetry = new("undeclared") }); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "battery refresh declarations cannot invoke an undeclared plugin command");
        if (OperatingSystem.IsWindows()) await WindowsChecksAsync(check);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WindowsChecksAsync(Action<bool, string> check)
    {
        string key = @"Software\Hhub.Tests\Startup-" + Guid.NewGuid().ToString("N");
        var registration = new StartupRegistration(key);
        try
        {
            string path = @"C:\Program Files\Hhub\Hhub.App.exe";
            registration.SetEnabled(path, true);
            check(registration.IsEnabled(path) && StartupRegistration.Command(path) == "\"" + path + "\" --startup",
                "startup registration quotes paths with spaces and uses the startup tray argument (isolated test key only)");
            registration.SetEnabled(path, false);
            check(!registration.IsEnabled(path), "turning startup off removes its registration (real Run key remains untouched)");
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
        check(ProcessEfficiency.SelectEfficientSets([(1u, (byte)1, (byte)0), (2u, (byte)0, (byte)0), (3u, (byte)0, (byte)2)]).SequenceEqual([2u]),
            "hybrid CPU preference selects the efficient class and excludes processors allocated to other applications");
        check(ProcessEfficiency.SelectEfficientSets([(1u, (byte)0, (byte)0), (2u, (byte)0, (byte)0)]).Length == 0,
            "uniform-core CPUs keep unrestricted scheduling rather than fabricating an efficiency-core group");
        using var process = Process.GetCurrentProcess(); var previous = process.PriorityClass;
        using (var policy = new ProcessEfficiency())
        {
            policy.SetEnabled(true); var state = new Throttling { Version = 1 };
            check(GetProcessInformation(process.Handle, 4, ref state, 12) && (state.Control & state.State & 1) != 0,
                "Windows reports real process EcoQoS enabled for the test process");
            policy.SetEnabled(false); state = new() { Version = 1 };
            check(GetProcessInformation(process.Handle, 4, ref state, 12) && state.Control == 0 && process.PriorityClass == previous,
                "restoring foreground releases EcoQoS override and restores original process priority");
        }
        string eventName = @"Local\Hhub.Tests.Background." + Guid.NewGuid().ToString("N");
        using var publisher = new BackgroundEfficiencySession(eventName);
        check(BackgroundEfficiencySession.ObserveCurrentProcess(true, eventName) is null, "latency-sensitive workers do not opt into background throttling");
        using var observer = BackgroundEfficiencySession.ObserveCurrentProcess(false, eventName);
        publisher.SetEnabled(true);
        var deadline = Stopwatch.StartNew(); var actual = new Throttling { Version = 1 };
        do { await Task.Delay(25); GetProcessInformation(process.Handle, 4, ref actual, 12); } while (actual.Control != 1 && deadline.Elapsed < TimeSpan.FromSeconds(4));
        check(actual.Control == 1, "background worker observes the tray efficiency signal without needing access to the UI process");
        publisher.SetEnabled(false); deadline.Restart();
        do { await Task.Delay(25); GetProcessInformation(process.Handle, 4, ref actual, 12); } while (actual.Control != 0 && deadline.Elapsed < TimeSpan.FromSeconds(4));
        check(actual.Control == 0, "background worker restores normal scheduling when the tray signal clears");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Throttling { public uint Version, Control, State; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(nint process, int kind, ref Throttling state, uint size);
}
