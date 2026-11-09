using System.Reflection;
using System.Runtime.Loader;

namespace Hhub.Core;

public static class ManagedPluginEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        int entryIndex = args.Length > 0 && args[0] == "--plugin" ? 1 : 0;
        if (args.Length <= entryIndex)
        {
            Console.Error.WriteLine("Missing plugin assembly.dll"); return 2;
        }
        try
        {
            string path = Path.GetFullPath(args[entryIndex]);
            if (!File.Exists(path) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Plugin entry assembly is missing");
            Environment.SetEnvironmentVariable("HHUB_PLUGIN_ENTRY", path);
            using var efficiency = OperatingSystem.IsWindows() ? BackgroundEfficiencySession.ObserveCurrentProcess(Environment.GetEnvironmentVariable("HHUB_LATENCY_SENSITIVE") == "1") : null;
            var context = new PluginContext(path);
            var assembly = context.LoadFromAssemblyPath(path);
            var entry = assembly.EntryPoint ?? throw new InvalidDataException("Plugin assembly has no entry point");
            object? result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [args[(entryIndex + 1)..]]);
            return result switch { Task<int> task => await task, Task task => await Complete(task), int code => code, _ => 0 };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException { InnerException: { } inner } ? inner : error); return 1;
        }
    }
    private static async Task<int> Complete(Task task) { await task; return 0; }
    private sealed class PluginContext(string path) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        private readonly string _directory = Path.GetDirectoryName(path)!;
        protected override Assembly? Load(AssemblyName name)
        {
            string? resolved = _resolver.ResolveAssemblyToPath(name);
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            string? resolved = _resolver.ResolveUnmanagedDllToPath(name);
            if (resolved is null && Path.GetFileName(name) == name)
            {
                string candidate = Path.Combine(_directory, name);
                if (File.Exists(candidate)) resolved = candidate;
            }
            return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved);
        }
    }
}
