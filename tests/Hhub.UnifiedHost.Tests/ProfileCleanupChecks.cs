using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hhub.PluginContracts;
using Hhub.UnifiedHost;

internal static class ProfileCleanupChecks
{
    public static async Task<int> RunAsync(string root, CertificationPolicy policy)
    {
        int passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        async Task Reject(Func<Task> action, string name)
        {
            bool rejected = false;
            try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { rejected = true; }
            Check(rejected, name);
        }
        var store = new PluginStore(Path.Combine(root, "cleanup-profile"), policy);
        string package = Path.Combine(root, "test.mouse.hhub");
        var installed = await store.InstallAsync(package);
        var personal = new PresetDocument(1, "test.mouse", "Keep exact bytes", new() { ["sample.value"] = JsonSerializer.SerializeToElement(800) });
        store.SavePreset(personal); store.SavePreset(personal with { TargetId = "fixture.1" });
        string data = store.DataDirectory("test.mouse"), presetPath = Path.Combine(data, "preset.json");
        string original = File.ReadAllText(presetPath);
        File.WriteAllText(Path.Combine(data, "private-helper.exe"), "must not survive keep-presets");
        foreach (string category in new[] { "downloads", "ui-cache", "logs", "staging", "transactions" })
        {
            Directory.CreateDirectory(Path.Combine(store.Root, category));
            File.WriteAllText(Path.Combine(store.Root, category, "owned.tmp"), "owned");
        }
        string outside = Path.Combine(root, "outside-cleanup.txt"); File.WriteAllText(outside, "not owned");
        string dotnet = Environment.GetEnvironmentVariable("HHUB_TEST_SHARED_HOST") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe");
        await using (var session = await PluginSession.StartAsync(store, installed, dotnet))
        {
            await Reject(() => store.CleanupAsync(true), "whole-profile cleanup refuses a running plugin");
            Check(File.Exists(Path.Combine(installed.Directory, "plugin.json")) && File.Exists(Path.Combine(data, "private-helper.exe")),
                "busy cleanup preflight removes no program or data");
        }
        string unknown = Path.Combine(store.Root, "future-host-file.json"); File.WriteAllText(unknown, "future format");
        await Reject(() => store.CleanupAsync(false), "older cleanup refuses unknown root entries before deletion");
        Check(File.Exists(Path.Combine(installed.Directory, "plugin.json")), "unknown profile category preserves installed files"); File.Delete(unknown);
        string future = original.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"); File.WriteAllText(presetPath, future);
        await Reject(() => store.CleanupAsync(true), "keep-presets cleanup refuses unknown preset formats");
        Check(File.ReadAllText(presetPath) == future && File.Exists(Path.Combine(installed.Directory, "plugin.json")),
            "failed preset preflight leaves original data and programs untouched"); File.WriteAllText(presetPath, original);
        var plan = store.PlanCleanup(true);
        Check(plan.PresetsToKeep == 2 && plan.Directories.Contains("downloads"), "cleanup plan includes owned storage and per-target presets");
        var result = await store.CleanupAsync(true);
        Check(result.PresetsKept == 2 && store.Discover().Plugins.Length == 0, "cleanup removes all plugins and retains chosen presets");
        Check(File.ReadAllText(presetPath) == original && store.LoadPreset("test.mouse", targetId: "fixture.1") is not null,
            "keep-presets preserves exact bytes and target identity");
        Check(!File.Exists(Path.Combine(data, "private-helper.exe")) && !Directory.Exists(Path.Combine(store.Root, "downloads"))
            && !Directory.Exists(Path.Combine(store.Root, "transactions")), "keep-presets removes private programs, downloads and transaction backups");
        Check(File.ReadAllText(outside) == "not owned", "cleanup leaves paths outside the managed profile untouched");
        await store.InstallAsync(package);
        Check(store.LoadPreset("test.mouse")!.Values["sample.value"].GetInt32() == 800, "reinstall recognizes retained personal presets");
        await store.CleanupAsync(false);
        Check(!Directory.Exists(Path.Combine(store.Root, "data")) && store.Discover().Plugins.Length == 0, "delete-data cleanup removes programs and personal presets");
        await store.CleanupAsync(false);
        Check(store.PlanCleanup(false).PresetsToKeep == 0, "repeated completed cleanup is safe");
        var unowned = new PluginStore(Path.Combine(root, "unowned-profile"), policy); File.Delete(Path.Combine(unowned.Root, "profile.json"));
        await Reject(() => unowned.CleanupAsync(false), "cleanup refuses storage without an ownership record");
        if (Environment.GetEnvironmentVariable("HHUB_TEST_SHARED_HOST") is { } sharedHost)
        {
            var info = new ProcessStartInfo(sharedHost) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "--plan-profile-cleanup", store.Root, "--keep-presets" }) info.ArgumentList.Add(argument);
            using (var process = Process.Start(info)!)
            {
                string output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(process.ExitCode == 0 && PluginApi.Read<ProfileCleanupPlan>(output).PresetsToKeep == 0,
                    "published host exposes headless cleanup preflight for an installer");
            }
            string mutexName = "Local\\Hhub-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store.Root.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
            using var active = new Mutex(false, mutexName);
            using (var process = Process.Start(info)!)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(process.ExitCode != 0 && File.Exists(Path.Combine(store.Root, "profile.json")), "installer cleanup refuses an active host");
            }
        }
        store.RemoveEmptyProfile();
        Check(!Directory.Exists(store.Root), "full removal deletes only owned metadata and empty profile directories");
        string installation = Path.Combine(root, "location-check"); Directory.CreateDirectory(installation);
        Check(HostProfileLocation.Resolve(installation, data) == Path.GetFullPath(data), "portable host keeps its default profile location");
        File.WriteAllText(Path.Combine(installation, HostProfileLocation.FileName), data);
        Check(HostProfileLocation.Resolve(installation, "unused") == Path.GetFullPath(data), "installed host uses the installation profile record");
        File.WriteAllText(Path.Combine(installation, HostProfileLocation.FileName), installation);
        await Reject(() => { _ = HostProfileLocation.Resolve(installation, data); return Task.CompletedTask; }, "profile cannot overlap executable installation files");
        return passed;
    }
}
