using Hhub.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class PluginMarketsChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string[] current = ["https://example.test/one.json"];
        foreach (string? invalid in new[] { null, "", "   ", "invalid", "http://remote.test/index.json", "https://EXAMPLE.test/one.json" })
        {
            var rejected = PluginMarkets.EditAddress(current, null, invalid);
            check(!rejected.Success && rejected.Addresses.SequenceEqual(current) && !string.IsNullOrEmpty(rejected.Error),
                "market form rejects invalid or duplicate input without throwing or changing saved addresses");
        }
        var added = PluginMarkets.EditAddress(current, null, " https://example.test/two.json ");
        check(added.Success && added.Addresses.Length == 2 && current.Length == 1, "valid market addition creates a new normalized list without mutating the original");
        var edited = PluginMarkets.EditAddress(added.Addresses, 0, "https://example.test/three.json");
        check(edited.Success && edited.Addresses[0].EndsWith("three.json"), "market address editing updates only the chosen entry");
        check(!PluginMarkets.EditAddress(current, 9, "https://example.test/new.json").Success, "stale market editor reports an error instead of indexing outside the list");
        var legacy = new UserPreferences { PluginMarketUrl = "https://example.test/index.json" };
        check(PluginMarkets.Configured(legacy).Single() == legacy.PluginMarketUrl, "single-market preferences migrate without losing the existing address");
        check(PluginMarkets.Configured(legacy with { PluginMarketUrls = [] }).Length == 0, "removing the last market does not resurrect the legacy address");
        var normalized = PluginMarkets.Normalize([" https://EXAMPLE.test/a.json ", "https://example.test/a.json", "https://example.test/b.json"]);
        check(normalized.Length == 2 && normalized[0] == "https://example.test/a.json", "multiple markets normalize hosts and remove duplicate addresses");
        var restored = JsonSerializer.Deserialize<UserPreferences>(JsonSerializer.Serialize(legacy with { PluginMarketUrls = normalized }, JsonFormat.Options), JsonFormat.Options)!;
        check(PluginMarkets.Configured(restored).SequenceEqual(normalized), "multiple market addresses survive preference persistence");
        bool insecure = false;
        try { PluginMarkets.Normalize(["http://remote.test/index.json"]); } catch (InvalidDataException) { insecure = true; }
        check(insecure, "all configured market sources retain HTTPS transport validation");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string origin = $"http://127.0.0.1:{port}";
        string hash = new('a', 64);
        string Index(params MarketPlugin[] plugins) => JsonSerializer.Serialize(new { schemaVersion = 1, plugins }, JsonFormat.Wire);
        string first = Index(new("test.shared", "Shared", "1.0.0", "old.hhub", hash), new("test.first", "First", "1.0.0", "first.hhub", hash));
        string second = Index(new MarketPlugin("test.shared", "Shared", "2.0.0", "new.hhub", hash));
        var server = Task.Run(async () =>
        {
            var replies = new List<Task>();
            for (int i = 0; i < 3; i++)
            {
                var client = await listener.AcceptTcpClientAsync();
                replies.Add(Task.Run(async () =>
                {
                    using (client)
                    {
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        string request = await reader.ReadLineAsync() ?? "";
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                        bool broken = request.Contains("/broken ");
                        byte[] body = Encoding.UTF8.GetBytes(broken ? "failed" : request.Contains("/one ") ? first : second);
                        string status = broken ? "503 Service Unavailable" : "200 OK";
                        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header); await stream.WriteAsync(body);
                    }
                }));
            }
            await Task.WhenAll(replies);
        });
        try
        {
            var result = await PluginMarkets.ReadAsync([origin + "/one", origin + "/two", origin + "/broken"]);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            check(result.Plugins.Length == 2 && result.Plugins.Single(item => item.Id == "test.shared").Version == "2.0.0",
                "multiple markets merge unique plugins and choose the newest advertised version");
            var shared = result.Plugins.Single(item => item.Id == "test.shared");
            check(shared.SourceUrl == origin + "/two" && shared.PackageUrl == origin + "/new.hhub", "merged plugin preserves its source and resolves download paths within that market");
            check(result.Errors.Length == 1 && result.Errors[0].Contains("/broken"), "a failed market is reported without discarding successful markets");
        }
        finally { listener.Stop(); }
    }
}
