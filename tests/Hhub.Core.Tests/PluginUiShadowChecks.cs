using Hhub.Core;

internal static class PluginUiShadowChecks
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "hhub-ui-update-" + Guid.NewGuid().ToString("N"));
        string pluginRoot = Path.Combine(root, "plugin"), ui = Path.Combine(pluginRoot, "ui"), cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(ui);
        string assembly = Path.Combine(ui, "Module.dll"), resource = Path.Combine(ui, "settings.json");
        File.Copy(typeof(PluginManifest).Assembly.Location, assembly); File.WriteAllText(resource, "{\"revision\":1}");
        var plugin = new InstalledPlugin(new PluginManifest { Id = "test.ui-update", Name = "UI update", Version = "1.0.0",
            EntryPoint = "worker.dll", UiAssembly = "ui/Module.dll" }, pluginRoot);
        try
        {
            string first = PluginUiShadow.Stage(plugin, cache);
            check(first != assembly && File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(assembly))
                && File.ReadAllText(Path.Combine(Path.GetDirectoryName(first)!, "settings.json")) == "{\"revision\":1}",
                "UI staging keeps the assembly and sibling resources together outside the installed package");
            using (var openModule = File.Open(first, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                File.WriteAllText(resource, "{\"revision\":2}");
                string second = PluginUiShadow.Stage(plugin, cache);
                check(second != first && File.ReadAllText(Path.Combine(Path.GetDirectoryName(first)!, "settings.json")).Contains('1')
                    && File.ReadAllText(Path.Combine(Path.GetDirectoryName(second)!, "settings.json")).Contains('2'),
                    "updating UI sidecars creates a new immutable copy while the old module is still locked");
                File.WriteAllBytes(assembly, [1, 2, 3, 4]);
                check(File.ReadAllBytes(assembly).Length == 4, "an open staged UI does not prevent replacing the package's original DLL");
            }
            string third = PluginUiShadow.Stage(plugin, cache);
            check(third != first && PluginUiShadow.Stage(plugin, cache) == third,
                "changed assembly content creates a new UI copy and unchanged content reuses it without rewriting a loaded DLL");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
