using Hhub.PluginContracts;

namespace Hhub.UnifiedHost;

public sealed record ServiceBinding(string ProviderId, string Contract, string Version, string[] CapabilityIds);

public static class ServiceCatalog
{
    public static ServiceBinding? Resolve(ServiceRequirement requirement, IEnumerable<InstalledPackage> verifiedPackages,
        string? selectedProvider = null)
    {
        var matches = verifiedPackages.SelectMany(plugin => plugin.Definition.Provides.Where(service =>
            service.Contract == requirement.Contract && Version.Parse(service.Version).Major == requirement.MajorVersion)
            .Select(service => new ServiceBinding(plugin.Definition.Id, service.Contract, service.Version, service.CapabilityIds))).ToArray();
        if (selectedProvider is not null)
            return matches.SingleOrDefault(service => service.ProviderId == selectedProvider)
                ?? throw new InvalidDataException("service.selected-provider-unavailable");
        return matches.Length switch
        {
            0 when requirement.Optional => null,
            0 => throw new InvalidDataException("service.required-provider-missing"),
            1 => matches[0],
            _ => throw new InvalidDataException("service.provider-selection-required")
        };
    }
}
