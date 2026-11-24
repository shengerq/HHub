namespace Hhub.PluginContracts;

/// <summary>Inventory of currently connected external peripherals.</summary>
public static class PeripheralDiscovery
{
    public const string Service = "devices.discovery";
    public const int MajorVersion = 1;
    public const string Connected = "devices.connected";
}

public sealed record ConnectedPeripheral(string Id, string Name, string Category, string Connection)
{
    public string? Artwork { get; init; }
    public bool IsPreciselyIdentified { get; init; }
    public bool HasDevicePlugin { get; init; }
    public string? DeviceProvider { get; init; }
    public string? OverviewProvider { get; init; }
}

public static class DeviceOverview
{
    public const string Service = "device.overview";
    public const string Parameters = "device.overview.parameters";
    public const string BatteryPercent = "device.overview.battery-percent"; // Optional integer array: empty or one value from 0 through 100.
    public const string BatteryCharging = "device.overview.battery-charging"; // Optional boolean array, paired with the percent sample.
    public const string TrayVariables = "device.overview.tray-variables"; // Optional array of { label, value } for the same target.
    public const string AlternateConnections = "device.overview.alternate-connections"; // Target IDs superseded by this device connection.
    public const string PhysicalConnections = "device.overview.physical-connections"; // Inventory IDs served by a single stable target.

    public static bool IsProvidedBy(PluginDefinition plugin) => plugin.Provides.Any(service =>
        service.Contract == Service && Version.TryParse(service.Version, out var version) && version.Major == 1
        && service.CapabilityIds.Contains(Parameters));
}

public sealed record DeviceOverviewRow(string Label, string Value, bool Compact);
