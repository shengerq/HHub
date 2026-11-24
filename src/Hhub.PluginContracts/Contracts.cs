using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hhub.PluginContracts;

// Public wire contracts only: no references to Hhub.Core, WinUI or host storage.
public static class PluginApi
{
    public const int ManifestSchema = 2;
    public const string Protocol = "hhub.plugin/1";
    public const int MaxMessageCharacters = 262144;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, PropertyNameCaseInsensitive = false
    };

    public static T Read<T>(string json)
    {
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 16 });
        UniqueProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("contract.empty");
    }

    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("contract.duplicate-field");
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }

    public static bool ValidId(string id) => id.Length <= 100 && Regex.IsMatch(id, "^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public static void Validate(PluginDefinition manifest)
    {
        if (manifest.SchemaVersion != ManifestSchema || manifest.Protocol != Protocol)
            throw new InvalidDataException("plugin.contract-unsupported");
        if (!ValidId(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120
            || !Version.TryParse(manifest.Version, out _) || manifest.Kind is not ("device" or "function"))
            throw new InvalidDataException("plugin.identity-invalid");
        ValidatePath(manifest.EntryPoint);
        if (!manifest.EntryPoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !manifest.EntryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("plugin.entry-invalid");
        if (manifest.Arguments.Length > 32 || manifest.Arguments.Any(argument => argument is null || argument.Length > 1024)
            || manifest.Capabilities.Length > 256 || manifest.Permissions.Length > 32
            || manifest.Capabilities.Any(item => item is null || item.Value is null)
            || manifest.Permissions.Any(permission => permission is null || !ValidId(permission))
            || manifest.Capabilities.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Capabilities.Length)
            throw new InvalidDataException("plugin.declarations-invalid");
        foreach (var capability in manifest.Capabilities)
        {
            if (!ValidId(capability.Id) || string.IsNullOrWhiteSpace(capability.Name)
                || capability.Persistence is not ("session" or "configuration" or "device"))
                throw new InvalidDataException("capability.definition-invalid");
            capability.Value.ValidateDefinition();
        }
        if (manifest.Navigation is { } navigation
            && (navigation.Title is { Length: > 32 } || navigation.Glyph is { Length: < 1 or > 4 }
                || navigation.PathData is { Length: < 1 or > 1024 }))
            throw new InvalidDataException("plugin.navigation-invalid");
        if (manifest.Provides.Length > 32 || manifest.Requires.Length > 32
            || manifest.Provides.Any(service => service is null || !ValidId(service.Contract) || !Version.TryParse(service.Version, out _)
                || service.CapabilityIds.Length == 0 || service.CapabilityIds.Distinct().Count() != service.CapabilityIds.Length
                || service.CapabilityIds.Any(id => !manifest.Capabilities.Any(capability => capability.Id == id)))
            || manifest.Requires.Any(service => service is null || !ValidId(service.Contract) || service.MajorVersion < 1)
            || manifest.Provides.Select(service => service.Contract).Distinct().Count() != manifest.Provides.Length
            || manifest.Requires.Select(service => service.Contract).Distinct().Count() != manifest.Requires.Length)
            throw new InvalidDataException("service.definition-invalid");
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':')
            || path.StartsWith('/') || path.EndsWith('/') || path.Split('/').Any(part =>
                string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(['<', '>', '"', '|', '?', '*', '\0']) >= 0
                || part.Any(char.IsControl) || Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("package.path-invalid");
    }
}

public sealed record PluginDefinition
{
    [JsonRequired] public int SchemaVersion { get; init; } = PluginApi.ManifestSchema;
    [JsonRequired] public string Protocol { get; init; } = PluginApi.Protocol;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Kind { get; init; }
    public required string EntryPoint { get; init; }
    public string[] Arguments { get; init; } = [];
    public string[] Permissions { get; init; } = [];
    public CapabilityDefinition[] Capabilities { get; init; } = [];
    public ServiceOffer[] Provides { get; init; } = [];
    public ServiceRequirement[] Requires { get; init; } = [];
    public PluginShortcut? Navigation { get; init; }
}

// Opt-in host shortcut: a plugin declares navigation to appear in the sidebar.
public sealed record PluginShortcut
{
    public string? Title { get; init; }
    public string? Glyph { get; init; }
    public string? PathData { get; init; }
}

public sealed record PluginTarget(string Id, string Name, string Kind, string Connection, bool Online, bool Simulated = false);
public sealed record ServiceOffer(string Contract, string Version, string[] CapabilityIds);
public sealed record ServiceRequirement(string Contract, int MajorVersion, bool Optional = true);

public sealed record CapabilityDefinition(string Id, string Name, ValueDefinition Value, bool Writable = false, string Persistence = "session");

// Deliberately bounded schema subset; the SDK and host share exactly this validator.
public sealed record ValueDefinition
{
    public required string Type { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int MaxLength { get; init; } = 256;
    public int MaxItems { get; init; } = 64;
    public ValueDefinition? Items { get; init; }
    public Dictionary<string, ValueDefinition> Properties { get; init; } = [];

    public void ValidateDefinition(int depth = 0)
    {
        if (depth > 8 || Type is not ("boolean" or "integer" or "number" or "string" or "object" or "array")
            || MaxLength is < 1 or > 4096 || MaxItems is < 1 or > 256 || Properties.Count > 64
            || Minimum is { } min && !double.IsFinite(min) || Maximum is { } max && !double.IsFinite(max)
            || Minimum is { } lower && Maximum is { } upper && lower > upper
            || Type == "array" && Items is null || Type != "array" && Items is not null
            || Type != "object" && Properties.Count > 0
            || Type is not ("integer" or "number") && (Minimum is not null || Maximum is not null))
            throw new InvalidDataException("capability.schema-invalid");
        foreach (var (key, property) in Properties)
        {
            if (!PluginApi.ValidId(key)) throw new InvalidDataException("capability.property-invalid");
            if (property is null) throw new InvalidDataException("capability.schema-invalid");
            property.ValidateDefinition(depth + 1);
        }
        Items?.ValidateDefinition(depth + 1);
    }

    public bool Accepts(JsonElement value) => Type switch
    {
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "string" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= MaxLength,
        "integer" or "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            && double.IsFinite(number) && (Type != "integer" || number == Math.Truncate(number))
            && (Minimum is null || number >= Minimum) && (Maximum is null || number <= Maximum),
        "array" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= MaxItems
            && Items is not null && value.EnumerateArray().All(Items.Accepts),
        "object" => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == Properties.Count
            && Properties.All(pair => value.TryGetProperty(pair.Key, out var member) && pair.Value.Accepts(member)),
        _ => false
    };
}

public sealed record RpcRequest(string Type, string RequestId, string Protocol, string PluginId,
    string? CapabilityId = null, JsonElement? Value = null, long? ExpectedRevision = null, string? TargetId = null,
    Dictionary<string, JsonElement>? Values = null);
public sealed record RpcResponse(string Type, string RequestId, string Protocol, string PluginId,
    bool Success, long Revision = 0, Dictionary<string, JsonElement>? Values = null,
    string? ErrorCode = null, bool BackgroundActivity = false, string? TargetId = null, PluginTarget[]? Targets = null);
