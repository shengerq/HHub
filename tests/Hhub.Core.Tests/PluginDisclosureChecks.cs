using Hhub.Core;
using System.IO.Compression;
using System.Text.Json;

internal static class PluginDisclosureChecks
{
    public static void Run(Action<bool, string> check)
    {
        var component = new PluginComponent("Example", "1.0", "https://example.test/project", "MIT", "licenses/MIT.txt", "Example contributors");
        var disclosure = new PluginDisclosure { License = "MIT", LicenseFile = "LICENSE",
            Runtimes = [new(".NET", "10", "x64", "host", "Plugin backend")], OpenSourceProjects = [component], Dependencies = [] };
        var manifest = new PluginManifest { Id = "test.disclosure", Name = "Disclosure", Version = "1.0.0", EntryPoint = "worker.exe", Disclosure = disclosure };
        PluginCatalog.Validate(manifest);
        check(PluginDisclosures.LicenseFiles(manifest).Count() == 2, "plugin disclosure collects its own and upstream licenses");
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, message);
        }
        Reject(() => PluginDisclosures.Validate(disclosure with { Runtimes = [] }), "disclosure requires explicit runtime requirements");
        Reject(() => PluginDisclosures.Validate(disclosure with { LicenseFile = "../outside.txt" }), "disclosure license paths stay inside the package");
        Reject(() => PluginDisclosures.Validate(disclosure with { OpenSourceProjects = [component with { Attribution = "" }] }), "open-source declarations preserve attribution");
        Reject(() => PluginDisclosures.Validate(disclosure with { Dependencies = [component with { ProjectUrl = "file:///C:/outside" }] }), "dependency links cannot execute local files");
        Reject(() => PluginDisclosures.Validate(disclosure with { Runtimes = [new(".NET", "10", "x64", "unknown", "Backend")] }), "runtime provisioning must be explicit and supported");
        PluginCatalog.Validate(manifest with { Disclosure = null });
        check(true, "installed legacy plugins retain compatibility without pretending to declare dependencies");
        Reject(() => PluginCatalog.Validate(manifest with { RequiredCapabilities = ["plugin.disclosure.v1"], Disclosure = null }), "plugins requiring disclosure cannot omit their declaration");
        string root = Path.Combine(Path.GetTempPath(), "hhub-disclosure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string package = Path.Combine(root, "test.hhub");
            void Package(bool includeLicenses)
            {
                using var stream = File.Create(package);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
                using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open())) writer.Write(JsonSerializer.Serialize(manifest, JsonFormat.Options));
                using (var writer = new StreamWriter(zip.CreateEntry("worker.exe").Open())) writer.Write("fixture");
                if (includeLicenses)
                    foreach (string file in PluginDisclosures.LicenseFiles(manifest))
                        using (var writer = new StreamWriter(zip.CreateEntry(file).Open())) writer.Write("fixture license");
            }
            Package(false);
            Reject(() => PluginCatalog.ReadPackageManifest(package), "package preflight rejects declared but absent license files");
            var catalog = new PluginCatalog(new AppStorage(Path.Combine(root, "data")));
            Reject(() => catalog.Install(package), "installation rejects absent licenses before replacing an installed plugin");
            Package(true);
            var installed = catalog.Install(package);
            check(installed.Manifest.Disclosure?.OpenSourceProjects[0].License == "MIT", "complete plugin disclosure survives package installation");
            File.Delete(Path.Combine(installed.Directory, "licenses", "MIT.txt"));
            Reject(() => PluginCatalog.Read(installed.Directory), "missing installed license is reported rather than silently hidden");
        }
        finally { Directory.Delete(root, true); }
    }
}
