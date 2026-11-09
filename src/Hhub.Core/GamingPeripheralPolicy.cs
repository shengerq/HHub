namespace Hhub.Core;

public static class GamingPeripheralPolicy
{
    public static bool IsVisible(DeviceRecord device, string? pluginCategory = null)
    {
        // Classify broadly for diagnostics, but list only the gaming peripherals the product manages.
        if (device.IsInternal || device.Connection == "内置" || Excluded(device.Category)) return false;
        string category = pluginCategory ?? device.Category;
        if (Excluded(category)) return false;
        if (category is "鼠标" or "键盘" or "键鼠复合设备" or "手柄" or "耳机") return true;
        if (category == "音频") return device.AudioFormFactor is 3 or 5 || device.Connection is "USB" or "蓝牙";
        // A matching plugin can describe additional gaming peripherals without a brand-specific host branch.
        return !string.IsNullOrWhiteSpace(pluginCategory) && pluginCategory is not ("HID" or "其他");
    }

    private static bool Excluded(string category) => category is "摄像头" or "磁盘" or "打印机" or "触控板";
}
