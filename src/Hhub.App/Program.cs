using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;
using Hhub.Core;

namespace Hhub.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if HHUB_UNIFIED
        if (args.Length > 0 && args[0] is "--cleanup-profile" or "--plan-profile-cleanup" or "--initialize-profile" or "--remove-profile")
            return RunProfileMaintenance(args);
#endif
        if (args.Length > 0 && (args[0] == "--plugin" || args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            return ManagedPluginEntry.RunAsync(args).GetAwaiter().GetResult();
        // Older plugins spawn Environment.ProcessPath with worker flags only.
        // Their children inherit the entry identity from the managed launcher.
        if (args.Length > 0 && Environment.GetEnvironmentVariable("HHUB_PLUGIN_ENTRY") is { Length: > 0 } entry)
            return ManagedPluginEntry.RunAsync([entry, .. args]).GetAwaiter().GetResult();
        StartInterface();
        return 0;
    }

#if HHUB_UNIFIED
    private static int RunProfileMaintenance(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[2] is not ("--keep-presets" or "--delete-data"))
                throw new InvalidDataException("Usage: --cleanup-profile|--plan-profile-cleanup <profile-root> --keep-presets|--delete-data");
            string root = Path.GetFullPath(args[1]);
            if (args[0] == "--initialize-profile")
            {
                if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(Path.Combine(root, "profile.json")))
                    throw new InvalidDataException("profile.ownership-required");
            }
            else if (!File.Exists(Path.Combine(root, "profile.json"))) throw new InvalidDataException("profile.ownership-required");
            if (args[0] == "--remove-profile" && args[2] != "--delete-data") throw new InvalidDataException("profile.delete-choice-required");
            using var lease = InstanceLease.TryAcquire(root) ?? throw new IOException("profile.host-running");
            string[] trusted = [];
#if HHUB_TEST_CERTIFICATION
            trusted = typeof(Program).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                .Where(item => item.Key == "Hhub.TestCertification" && !string.IsNullOrWhiteSpace(item.Value)).Select(item => item.Value!).ToArray();
#endif
            var store = new Hhub.UnifiedHost.PluginStore(root, new(trusted), Environment.ProcessPath!);
            bool keep = args[2] == "--keep-presets";
            object result = args[0] is "--plan-profile-cleanup" or "--initialize-profile" ? store.PlanCleanup(keep)
                : store.CleanupAsync(keep).GetAwaiter().GetResult();
            if (args[0] == "--remove-profile") store.RemoveEmptyProfile();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, Hhub.PluginContracts.PluginApi.Json));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
#endif

    // Keep WinUI initialization out of headless backend processes.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartInterface()
    {
        SetCurrentProcessExplicitAppUserModelID("Hhub.App");
        XamlGeneratedProgram.XamlGeneratedMain();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
