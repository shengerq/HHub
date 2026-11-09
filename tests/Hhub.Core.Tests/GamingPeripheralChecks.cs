using Hhub.Core;

internal static class GamingPeripheralChecks
{
    public static void Run(Action<bool, string> check)
    {
        DeviceRecord Device(string category, string connection = "USB", bool internalDevice = false) =>
            new("test", "Test", category, connection, null, null, "") { IsInternal = internalDevice };
        check(new[] { "鼠标", "键盘", "键鼠复合设备", "手柄", "耳机" }.All(category => GamingPeripheralPolicy.IsVisible(Device(category))),
            "gaming peripheral list includes keyboards, mice, headsets and controllers");
        check(!GamingPeripheralPolicy.IsVisible(Device("鼠标", "内置")) && !GamingPeripheralPolicy.IsVisible(Device("键盘", internalDevice: true)),
            "internal pointer and internal USB keyboard controller do not appear as gaming peripherals");
        check(new[] { "摄像头", "打印机", "磁盘", "触控板", "HID" }.All(category => !GamingPeripheralPolicy.IsVisible(Device(category))),
            "cameras, drives, printers, touchpads and unknown HID controls are excluded from the product device list");
        check(!GamingPeripheralPolicy.IsVisible(Device("摄像头") with { Categories = ["键盘"] }, "键盘"),
            "a camera does not become a gaming keyboard because it exposes HID buttons");
        check(GamingPeripheralPolicy.IsVisible(Device("耳机", "声卡接口") with { AudioFormFactor = 3 })
            && !GamingPeripheralPolicy.IsVisible(Device("音频", "声卡接口") with { AudioFormFactor = 1 }),
            "analog headphones remain visible while internal speakers are excluded");
        check(GamingPeripheralPolicy.IsVisible(Device("HID"), "方向盘") && !GamingPeripheralPolicy.IsVisible(Device("HID", internalDevice: true), "方向盘"),
            "matching plugins can add an external gaming peripheral type without exposing an internal controller");

        const string usb = @"USB\VID_1234&PID_5678\DEVICE";
        var internalKeyboard = DeviceInventory.Build([
            new(usb, "USB controller", "USB", Removable: false),
            new(@"HID\KEYBOARD", "HID Keyboard Device", "键盘", usb)
        ]).Single();
        check(internalKeyboard.IsInternal && !GamingPeripheralPolicy.IsVisible(internalKeyboard),
            "non-removable physical USB parent marks its keyboard interfaces as internal");
        var externalKeyboard = DeviceInventory.Build([
            new(usb, "USB keyboard", "USB", Removable: true),
            new(@"HID\KEYBOARD", "HID Keyboard Device", "键盘", usb, Removable: false)
        ]).Single();
        check(!externalKeyboard.IsInternal && GamingPeripheralPolicy.IsVisible(externalKeyboard),
            "external USB device remains visible when its individual child interfaces are non-removable");
        var gamepad = DeviceInventory.Build([
            new(usb, "Controller", "USB", Removable: true),
            new(@"HID\CONTROLLER", "HID controller", "HID", usb, CompatibleIds: ["HID_DEVICE_UP:0001_U:0005"]),
            new("controller-audio", "Headset jack", "音频", usb)
        ]).Single();
        check(gamepad.Category == "手柄" && GamingPeripheralPolicy.IsVisible(gamepad),
            "HID gamepad with an audio jack remains a gaming controller");
        var xusb = DeviceInventory.Build([
            new(usb, "Controller", "USB", CompatibleIds: [@"USB\MS_COMP_XUSB10"], Removable: true)
        ]).Single();
        check(xusb.Category == "手柄" && GamingPeripheralPolicy.IsVisible(xusb),
            "XUSB compatible controller does not require a keyboard or mouse collection to appear");
    }
}
