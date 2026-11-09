using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Hhub.PluginContracts;
using Hhub.UnifiedHost;

try
{
    if (args is ["schema", var schemaOutput])
    {
        Directory.CreateDirectory(schemaOutput);
        var options = new JsonSerializerOptions(PluginApi.Json) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        foreach (var (type, name) in new[] { (typeof(PluginDefinition), "manifest"), (typeof(RpcRequest), "request"), (typeof(RpcResponse), "response") })
        {
            JsonNode schema = options.GetJsonSchemaAsNode(type);
            schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            if (type == typeof(PluginDefinition))
            {
                schema["properties"]!["schemaVersion"]!["const"] = PluginApi.ManifestSchema;
                schema["properties"]!["protocol"]!["const"] = PluginApi.Protocol;
                schema["properties"]!["kind"]!["enum"] = new JsonArray("device", "function");
            }
            File.WriteAllText(Path.Combine(schemaOutput, name + ".schema.json"), schema.ToJsonString(new() { WriteIndented = true }));
        }
        Console.WriteLine("Structural schemas generated; SDK validation also enforces limits and capability semantics."); return;
    }
    if (args is ["certify", var source, var output, var pfx])
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfx, Environment.GetEnvironmentVariable("HHUB_SIGNING_PASSWORD"), X509KeyStorageFlags.EphemeralKeySet);
        CertifiedPackages.CertifyDirectory(source, output, certificate);
        Console.WriteLine("Certified " + output); return;
    }
    if (args is ["verify", var package, var certificatePath])
    {
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        var verified = CertifiedPackages.Verify(package, new CertificationPolicy([certificate.GetCertHashString(HashAlgorithmName.SHA256)]));
        Console.WriteLine(JsonSerializer.Serialize(verified, PluginApi.Json)); return;
    }
    if (args is ["test-kit", var sampleOutput, var kitOutput])
    {
        string kit = Path.GetFullPath(kitOutput); Directory.CreateDirectory(kit);
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Hhub SIMULATION ONLY", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(14));
        File.WriteAllBytes(Path.Combine(kit, "test-certification.cer"), certificate.Export(X509ContentType.Cert));
        File.WriteAllText(Path.Combine(kit, "test-fingerprint.txt"), certificate.GetCertHashString(HashAlgorithmName.SHA256));
        foreach (string kind in new[] { "device", "function" })
        {
            string sourceDirectory = Path.Combine(kit, kind); Directory.CreateDirectory(sourceDirectory);
            foreach (string file in Directory.GetFiles(sampleOutput)) File.Copy(file, Path.Combine(sourceDirectory, Path.GetFileName(file)), overwrite: false);
            var definition = new PluginDefinition
            {
                Id = "hhub.simulation." + kind, Name = kind == "device" ? "模拟鼠标（无硬件操作）" : "模拟 EQ（不处理音频）",
                Version = "1.0.0", Kind = kind, EntryPoint = "Hhub.SamplePlugin.dll",
                Provides = kind == "function" ? [new("audio.processing", "1.0", ["audio.enabled", "audio.preamp", "audio.preset"])] : [],
                Capabilities = kind == "device"
                    ? [new("mouse.dpi", "DPI", new() { Type = "integer", Minimum = 50, Maximum = 45000 }, true, "configuration"),
                        new("mouse.stages", "DPI 档位", new() { Type = "array", MaxItems = 6, Items = new() { Type = "integer", Minimum = 50, Maximum = 45000 } }, true, "configuration")]
                    : [new("audio.enabled", "处理开关（模拟）", new() { Type = "boolean" }, true),
                        new("audio.preamp", "前级增益", new() { Type = "number", Minimum = -12, Maximum = 12 }, true, "configuration"),
                        new("audio.preset", "预设名称", new() { Type = "string" }, true, "configuration")]
            };
            File.WriteAllText(Path.Combine(sourceDirectory, "plugin.json"), JsonSerializer.Serialize(definition, PluginApi.Json));
            CertifiedPackages.CertifyDirectory(sourceDirectory, Path.Combine(kit, "hhub.simulation." + kind + ".hhub"), certificate);
        }
        Console.WriteLine("Simulation kit created. Its ephemeral private key was not saved."); return;
    }
    Console.Error.WriteLine("schema <directory> | certify <source> <output.hhub> <certificate.pfx> | verify <package.hhub> <certificate.cer> | test-kit <sample-output> <new-kit-directory>");
    Environment.ExitCode = 2;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
