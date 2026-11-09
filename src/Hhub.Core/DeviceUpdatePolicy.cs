namespace Hhub.Core;

public static class DeviceUpdatePolicy
{
    private static readonly HashSet<string> InventoryProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.ItemNameDisplay", "System.Devices.FriendlyName", "System.Devices.ClassGuid",
        "System.Devices.DeviceInstanceId", "System.Devices.Parent", "System.Devices.ContainerId",
        "System.Devices.Connected", "System.Devices.IsPresent",
        "{a45c254e-df1c-4efd-8020-67d146a850e0} 37", // DEVPKEY_Device_LocationPaths
        "{a45c254e-df1c-4efd-8020-67d146a850e0} 15", // DEVPKEY_Device_LocationInfo
        "{a45c254e-df1c-4efd-8020-67d146a850e0} 4", // Compatible IDs can affect the main input category.
        "{a45c254e-df1c-4efd-8020-67d146a850e0} 17", // Physical device removability affects peripheral scope.
        "{540b947e-8b40-45bc-a8a2-6a0b894cbda2} 4" // Bus reported description
    };

    public static bool RequiresInventoryRefresh(IEnumerable<string> changedProperties) => changedProperties.Any(InventoryProperties.Contains);
}
