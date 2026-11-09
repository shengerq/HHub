using Hhub.Core;

internal static class DeviceInventoryChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string usbA = @"USB\VID_24AE&PID_1421\A";
        const string usbB = @"USB\VID_24AE&PID_1421\B";
        const string container = "8c512359-5623-494d-b751-2234bfda8bab";
        var source = new[]
        {
            new DeviceNode(usbA, "USB Composite Device", "USB", ContainerId: container, Location: "Port_#0001.Hub_#0002", ProductName: "Rapoo Receiver"),
            new DeviceNode(usbA + "&MI_00", "USB Input Device", "HID", usbA, Interfaces: ["control-a"]),
            new DeviceNode(@"HID\A&MOUSE", "HID-compliant mouse", "鼠标", usbA + "&MI_00", Interfaces: ["mouse-a"]),
            new DeviceNode(@"HID\A&KEYBOARD", "HID Keyboard Device", "键盘", usbA + "&MI_00", Interfaces: ["keyboard-a"]),
            // Even a bad/shared container ID must not merge two USB device instances.
            new DeviceNode(usbB, "USB Composite Device", "USB", ContainerId: container, Location: "Port_#0002.Hub_#0002", ProductName: "Rapoo Receiver"),
            new DeviceNode(@"HID\B&MOUSE", "HID-compliant mouse", "鼠标", usbB, Interfaces: ["mouse-b"]),
            new DeviceNode(@"ROOT\RDP_KBD\0000", "Remote Keyboard", "键盘"),
            new DeviceNode(@"VMBUS\FAKE", "Hyper-V", "USB"),
            new DeviceNode(@"HID\VIRTUAL", "Virtual Keyboard", "键盘", @"VMBUS\FAKE"),
            new DeviceNode(@"SWD\MMDEVAPI\REMOTE", "Remote Audio", "音频"),
            new DeviceNode(@"USB\ROOT_HUB30\1", "USB Hub", "USB")
        };
        var devices = DeviceInventory.Build(source);
        check(devices.Count == 2, "composite HID collections collapse to two actual USB peripherals; remote nodes and hubs do not count");
        var first = devices.Single(device => device.Location!.Contains("0001"));
        check(first.Name == "Rapoo Receiver" && first.Vid == "24AE" && first.Pid == "1421" && first.Connection == "USB", "physical USB description and identity replace generic HID labels");
        check(first.InterfacePaths.Length == 3 && first.ContainsInstance(@"HID\A&KEYBOARD") && first.ContainsInstance(usbA), "all child interface paths and instance aliases survive grouping for plugin enrichment");
        check(devices.Select(device => device.InstanceId).Distinct().Count() == 2, "identical VID/PID and container never merge independent USB ports");
        var keyboardPlugin = new PluginManifest { Id = "test.keyboard", Name = "Keyboard", Version = "1.0", EntryPoint = "test.exe", DeviceMatches = [new(Category: "键盘")] };
        check(PluginPolicies.MatchesDevice(keyboardPlugin, first), "grouped secondary categories still match existing plugin declarations");
        check(DeviceInventory.Build(source.Reverse()).Select(device => device.InstanceId).SequenceEqual(devices.Select(device => device.InstanceId)), "enumeration order does not change grouped device selection identity");

        var bluetooth = DeviceInventory.Build([
            new(@"BTHENUM\DEV_ABC\1", "Headphones", "蓝牙", ContainerId: container),
            new(@"BTHENUM\AUDIO\1", "Audio service", "音频", @"BTHENUM\DEV_ABC\1", container),
            new(@"BTHENUM\HID\1", "HID service", "HID", @"BTHENUM\DEV_ABC\1", container),
            new(@"BTHENUM\DEV_DEF\1", "Other Headphones", "蓝牙", ContainerId: container),
            new(@"BTHENUM\AUDIO\2", "Audio service", "音频", @"BTHENUM\DEV_DEF\1", container)
        ]);
        check(bluetooth.Count == 2 && bluetooth.All(device => device.Connection == "蓝牙"), "Bluetooth services group by device without becoming USB radio peripherals");
        var cycle = DeviceInventory.Build([new(@"HID\CYCLE", "Internal keyboard", "键盘", @"ACPI\parent"), new(@"ACPI\parent", "Parent", "其他", @"HID\CYCLE")]);
        check(cycle.Count == 1 && cycle[0].Connection == "内置", "built-in input remains visible and a cyclic parent graph terminates");

        var keyboard = DeviceInventory.Build([
            new(usbA, "Example 63", "USB"),
            new(usbA + "&MI_00", "USB Input Device", "HID", usbA,
                CompatibleIds: [@"USB\Class_03&SubClass_01&Prot_01"]),
            new(@"HID\KB", "HID Keyboard Device", "键盘", usbA + "&MI_00"),
            new(@"HID\KB&MOUSE", "HID-compliant mouse", "鼠标", usbA)
        ]).Single();
        check(keyboard.Category == "键盘" && keyboard.Categories.Contains("鼠标"), "keyboard with an auxiliary mouse collection uses its unique keyboard boot interface without losing capabilities");
        var composite = DeviceInventory.Build(source.Take(4)).Single();
        check(composite.Category == "键鼠复合设备", "ambiguous composite input is not guessed to be a mouse from a fixed category priority");
        var mouse = DeviceInventory.Build([
            new(usbA, "Gaming Mouse", "USB"), new(@"HID\M", "Mouse", "鼠标", usbA), new(@"HID\K", "Keyboard", "键盘", usbA)
        ]).Single();
        check(mouse.Category == "鼠标", "mouse with macro keyboard interfaces remains a mouse when the product description supplies evidence");
        var camera = DeviceInventory.Build([
            new(usbA, "Integrated Camera", "USB"), new("camera", "Camera", "摄像头", usbA), new("button", "Keyboard", "键盘", usbA)
        ]).Single();
        check(camera.Category == "摄像头" && camera.Name == "内置摄像头", "camera HID buttons do not turn the camera into an input device and its standard description is readable");
        var touchpad = DeviceInventory.Build([
            new(@"ACPI\PAD", "I2C HID Device", "其他"),
            new(@"HID\PAD", "HID-compliant touch pad", "HID", @"ACPI\PAD", CompatibleIds: ["HID_DEVICE_UP:000D_U:0005"]),
            new(@"HID\PAD&MOUSE", "HID-compliant mouse", "鼠标", @"ACPI\PAD")
        ]).Single();
        check(touchpad.Category == "触控板" && touchpad.Name == "内置触控板", "touchpad is recognized from its digitizer usage instead of its mouse compatibility class");
        var unknownPointer = DeviceInventory.Build([
            new(@"ACPI\PAD", "I2C HID Device", "其他"), new(@"HID\VID_0001&PID_0002\PAD", "HID-compliant mouse", "鼠标", @"ACPI\PAD")
        ]).Single();
        check(unknownPointer.Name == "内置指针设备", "generic built-in mouse compatibility is labeled as an internal pointer without guessing a touchpad model");
        var analog = DeviceInventory.Build([
            new(@"HDAUDIO\ADAPTER", "Sound card", "音频", ContainerId: container),
            new(@"SWD\MMDEVAPI\HEADPHONES", "Headphones (Sound card)", "音频", @"HDAUDIO\ADAPTER", container, IsAudioEndpoint: true, AudioFormFactor: 3),
            new(@"SWD\MMDEVAPI\SPEAKERS", "Speakers (Sound card)", "音频", @"HDAUDIO\ADAPTER", container, IsAudioEndpoint: true, AudioFormFactor: 1),
            new(@"SWD\MMDEVAPI\REMOTE", "Remote Audio", "音频", IsAudioEndpoint: true, AudioFormFactor: 0)
        ]);
        check(analog.Count == 2 && analog.Any(device => device.Category == "耳机") && analog.All(device => device.Connection == "声卡接口"),
            "active analog headphone and speaker outputs are represented separately; remote audio is excluded");
        check(analog.All(device => device.Vid is null && device.Pid is null), "analog output never invents the passive headphone vendor or product identity");
        var usbAudio = DeviceInventory.Build([
            new(usbA, "USB Headset", "USB", ContainerId: container),
            new("audio-hw", "Audio hardware", "音频", usbA, container),
            new("swd-audio", "Headphones", "音频", ContainerId: container, IsAudioEndpoint: true, AudioFormFactor: 3)
        ]);
        check(usbAudio.Count == 1 && usbAudio[0].ContainsInstance("swd-audio") && usbAudio[0].Connection == "USB", "software audio endpoint joins its unique physical USB container without creating a duplicate headset");
        var bluetoothAudio = DeviceInventory.Build([
            new(@"BTHENUM\DEV_123\1", "Bluetooth headset", "蓝牙", ContainerId: container),
            new("bt-audio", "Headphones", "音频", ContainerId: container, IsAudioEndpoint: true, AudioFormFactor: 3)
        ]);
        check(bluetoothAudio.Count == 1 && bluetoothAudio[0].Category == "耳机" && bluetoothAudio[0].Connection == "蓝牙", "Bluetooth headset survives even when its audio endpoint has no Bluetooth parent");

        const string hfp = @"BTHHFENUM\BTHHFPAUDIO\1";
        var hfpNode = new DeviceNode(hfp, "Microsoft_HFP_Audio_Device_97", "音频", ContainerId: container);
        var hfpEndpoint = new DeviceNode("hfp-render", hfpNode.Name, "音频", hfp, container, Interfaces: ["hfp-output"],
            IsAudioEndpoint: true, AudioFormFactor: 5);
        check(DeviceInventory.Build([hfpNode, hfpEndpoint]).Count == 0,
            "an orphaned Bluetooth HFP service and its generic endpoint are not counted as a peripheral");
        check(DeviceInventory.Build([hfpNode, hfpEndpoint with { Name = "Headset hands-free output" }]).Count == 0,
            "a friendly endpoint label does not turn an orphaned HFP function into a physical peripheral");
        var paired = DeviceInventory.Build([hfpNode, hfpEndpoint,
            new(@"BTHENUM\DEV_ABC\1", "Bluetooth headset", "蓝牙", ContainerId: container)]).Single();
        check(paired.Name == "Bluetooth headset" && paired.ContainsInstance(hfp) && paired.AudioRenderEndpointIds.SequenceEqual(["hfp-output"]),
            "HFP service and output remain aliases of the identified Bluetooth headset");
        var unnamed = DeviceInventory.Build([hfpNode, hfpEndpoint,
            new(@"BTHENUM\DEV_ABC\1", "Bluetooth Device", "蓝牙", ContainerId: container)]).Single();
        check(unnamed.Name == "蓝牙耳机" && unnamed.ReportedName == hfpNode.Name,
            "anonymous paired headsets retain HFP evidence without using a driver node name on the card");

        string controllerPath = @"\\?\USB#VID_24AE&PID_1421#A#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\wave";
        var outputs = DeviceInventory.Build([
            new(usbA, "USB Headset", "USB", ContainerId: container),
            new(usbB, "Another USB Headset", "USB", ContainerId: container),
            new("usb-render", "Headphones", "音频", ContainerId: container, Interfaces: ["usb-output", "interface-alias"], IsAudioEndpoint: true, AudioFormFactor: 3)
                { AudioControllerId = controllerPath, RenderEndpointId = "usb-output" },
            new(@"HDAUDIO\ADAPTER", "Sound card", "音频"),
            new("analog-render", "Headphones", "音频", @"HDAUDIO\ADAPTER", IsAudioEndpoint: true, AudioFormFactor: 3)
                { Has35mmJack = true }
        ]);
        check(outputs.Count == 2 && outputs.Single(device => device.Connection == "USB") is { Has35mmJack: false, Name: "USB Headset" } usbOutput
            && usbOutput.AudioRenderEndpointIds.SequenceEqual(["usb-output"]) && outputs.Single(device => device.Connection == "声卡接口").Has35mmJack,
            "controller evidence distinguishes a USB headset from an identical endpoint label with a confirmed 3.5 mm jack");
        var onboardUsb = DeviceInventory.Build([
            new(usbA, "Onboard USB codec", "音频", Removable: false),
            new("jack-render", "Headphones", "音频", usbA, IsAudioEndpoint: true, AudioFormFactor: 3) { Has35mmJack = true }
        ]).Where(device => GamingPeripheralPolicy.IsVisible(device)).Single();
        check(onboardUsb.Connection == "声卡接口" && onboardUsb.Has35mmJack && onboardUsb.Vid is null,
            "an internal USB codec does not hide its external analog headphone jack or lend it the codec's USB identity");

        var classificationPlugin = new InstalledPlugin(new PluginManifest
        {
            Id = "test.category", Name = "Category", Version = "1.0", EntryPoint = "unused.exe",
            DevicePresentations = [new("24AE", "1421", "Example peripheral") { Category = "绘图板" }]
        }, Path.GetTempPath());
        check(DeviceMetadata.Resolve(classificationPlugin, first, new Dictionary<string, System.Text.Json.JsonElement>(), false).Category == "绘图板",
            "plugin can supply an additional device category without adding a brand or category branch to the host");
        var snapshot = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["devices"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { new { instanceId = @"HID\A&KEYBOARD", category = "键盘" } })
        };
        check(DeviceMetadata.Resolve(classificationPlugin, first, snapshot, true).Category == "键盘", "per-instance plugin category overrides static category through a grouped member alias");
        var overviewPlugin = classificationPlugin with { Manifest = classificationPlugin.Manifest with
        {
            Category = "device", Transport = "stdio-v1", RequiredCapabilities = ["device.overview.v1"],
            DeviceMatches = [new("24AE", "1421")], DeviceOverview = new(ApplyCommand: "apply"),
            Commands = [new("apply", "Apply", "device.config") { Parameters = [new("name", "Name", "string")] }]
        } };
        PluginCatalog.Validate(overviewPlugin.Manifest);
        var overviewSnapshot = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["devices"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { new
            {
                instanceId = @"HID\A&KEYBOARD", battery = 0,
                overview = new[] { new { label = "DPI", value = "800" } },
                currentConfiguration = "板载配置", configurations = new[] { "FPS", "FPS", "" }
            } })
        };
        var freshOverview = DeviceMetadata.Resolve(overviewPlugin, first, overviewSnapshot, true);
        check(freshOverview.Battery == 0 && freshOverview.Overview is { Current: "板载配置", Configurations: ["FPS"] }
            && freshOverview.Overview.Rows.Single() == new DeviceOverviewRow("DPI", "800"),
            "home overview reads only valid, instance-scoped plugin data and keeps zero battery");
        check(DeviceMetadata.Resolve(overviewPlugin, first, overviewSnapshot, false) is { Battery: null, Overview: null },
            "stale snapshots cannot supply home status, configuration options or battery");
    }
}
