using Hhub.Core;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class PluginMarketChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string hash = new('a', 64);
        var source = new Uri("https://example.test/market/index.json");
        string Index(params MarketPlugin[] plugins) => JsonSerializer.Serialize(new { schemaVersion = 1, plugins }, JsonFormat.Wire);
        var old = new MarketPlugin("test.mouse", "Mouse", "1.0.0", "mouse.hhub", hash);
        var latest = old with { Version = "2.0.0" };
        var parsed = PluginMarket.Parse(Index(old, latest), source);
        check(parsed.Length == 1 && parsed[0].Version == "2.0.0" && parsed[0].PackageUrl == "https://example.test/market/mouse.hhub",
            "market resolves relative package URLs and chooses the latest version by plugin identity");
        void Reject(Action action, string description)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, description);
        }
        Reject(() => PluginMarket.Address("http://example.test/index.json"), "remote market transport requires HTTPS");
        Reject(() => PluginMarket.Address("https://user:secret@example.test/index.json"), "market URL rejects embedded credentials");
        Reject(() => PluginMarket.Parse(Index(old with { PackageUrl = "file:///C:/plugin.hhub" }), source), "market cannot download local or non-HTTP package URLs");
        Reject(() => PluginMarket.Parse(Index(old with { Sha256 = "bad" }), source), "market package requires a complete SHA-256");
        Reject(() => PluginMarket.Parse(Index(old, old), source), "duplicate market identity and version are rejected");
        Reject(() => PluginMarket.Parse("{\"schemaVersion\":\"1\",\"plugins\":[]}", source), "malformed index schema does not masquerade as an empty update list");
        check(PluginPolicies.UnifiedMode("always") == "onDemand" && PluginPolicies.UnifiedMode("disabled") == "disabled",
            "legacy always-running preference becomes on-demand while explicit disable survives");
        var manifest = new PluginManifest { Id = "test.persistent", Name = "Persistent task", Version = "1.0.0", EntryPoint = "worker.exe",
            Transport = "stdio-v1", SupportsBackgroundActivity = true, PersistentTaskReason = "Keep a user-selected sensor stream active",
            RequiredCapabilities = ["background-persistent.v1"] };
        PluginCatalog.Validate(manifest);
        bool demand = PluginPolicies.HasDemand(manifest, "settings", null, null, null, false);
        check(demand && PluginPolicies.ShouldRun("onDemand", demand) && !PluginPolicies.ShouldRun("disabled", demand),
            "declared persistent task can start off-page but cannot override the user's disable choice");
        Reject(() => PluginCatalog.Validate(manifest with { PersistentTaskReason = " " }), "persistent task needs a meaningful reason");
        Reject(() => PluginCatalog.Validate(manifest with { RequiredCapabilities = [] }), "persistent task declares host compatibility capability");
        Reject(() => PluginCatalog.Validate(manifest with { SupportsBackgroundActivity = false }), "persistent task needs background activity support");

        string root = Path.Combine(Path.GetTempPath(), "hhub-market-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] package;
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                    writer.Write(JsonSerializer.Serialize(manifest, JsonFormat.Options));
                package = memory.ToArray();
            }
            string digest = Convert.ToHexString(SHA256.HashData(package));
            async Task Serve(Func<string, Task> run)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var reply = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    var request = new List<byte>(); byte[] one = new byte[1];
                    while (request.Count < 8192 && await stream.ReadAsync(one) > 0)
                    {
                        request.Add(one[0]);
                        if (request.Count >= 4 && request.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {package.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header); await stream.WriteAsync(package);
                });
                try { await run($"http://127.0.0.1:{port}/plugin.hhub"); await reply.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { listener.Stop(); }
            }
            string path = Path.Combine(root, "download.hhub");
            await Serve(async address => await PluginMarket.DownloadPackageAsync(new(manifest.Id, manifest.Name, manifest.Version, address, digest), path));
            check(File.Exists(path) && PluginCatalog.ReadPackageManifest(path).Id == manifest.Id, "download checks hash and package identity before handing off to installer");
            File.Delete(path);
            await Serve(async address =>
            {
                bool rejected = false;
                try { await PluginMarket.DownloadPackageAsync(new(manifest.Id, manifest.Name, manifest.Version, address, hash), path); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected && !File.Exists(path), "incorrect package hash cannot reach installation or leave a downloaded file");
            });
            await Serve(async address =>
            {
                bool rejected = false;
                try { await PluginMarket.DownloadPackageAsync(new("wrong.id", manifest.Name, manifest.Version, address, digest), path); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected && !File.Exists(path), "hash-valid package with mismatched identity is rejected and cleaned up");
            });
        }
        finally { foreach (string file in Directory.GetFiles(root)) File.Delete(file); Directory.Delete(root); }
    }
}
