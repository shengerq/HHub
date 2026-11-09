using System.Text.RegularExpressions;

namespace Hhub.Core;

public sealed record DeviceNode(string Id, string Name, string Category, string? ParentId = null,
    string? ContainerId = null, string? Location = null, string? LocationPath = null,
    string? ProductName = null, string[]? Interfaces = null, string[]? CompatibleIds = null,
    bool IsAudioEndpoint = false, int? AudioFormFactor = null, bool? Removable = null)
{
    public string? AudioControllerId { get; init; }
    public string? RenderEndpointId { get; init; }
    public bool Has35mmJack { get; init; }
}

// Windows enumerates devnodes and HID collections, not a count of physical peripherals.
public static partial class DeviceInventory
{
    public static IReadOnlyList<DeviceRecord> Build(IEnumerable<DeviceNode> source)
    {
        var nodes = source.DistinctBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, (DeviceNode Anchor, string Connection, List<DeviceNode> Members)>(StringComparer.OrdinalIgnoreCase);
        var interfaceOwners = nodes.Values.SelectMany(node => (node.Interfaces ?? []).Select(path => (path, node)))
            .ToLookup(item => item.path, item => item.node, StringComparer.OrdinalIgnoreCase);
        var audioEndpoints = new List<DeviceNode>();
        foreach (var node in nodes.Values)
        {
            var chain = Ancestors(node, nodes).ToArray();
            if (chain.Any(item => Virtual(item.Id))) continue;
            if (node.IsAudioEndpoint) { if (node.AudioFormFactor != 0) audioEndpoints.Add(node); continue; }
            var bluetooth = chain.FirstOrDefault(BluetoothDevice);
            bool isBluetooth = bluetooth is not null || chain.Any(item => item.Id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase));
            var usb = chain.FirstOrDefault(item => item.Id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)
                && !item.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
            // HID over I2C is a real built-in input, while root/software audio and radio controllers are not peripherals.
            bool touchpadUsage = (node.CompatibleIds ?? []).Any(id => id.Contains("UP:000D_U:0005", StringComparison.OrdinalIgnoreCase));
            bool builtInInput = (node.Category is "鼠标" or "键盘" || touchpadUsage) && node.Id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)
                && chain.Any(item => item.Id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)
                    || item.Id.StartsWith("I2C\\", StringComparison.OrdinalIgnoreCase));
            if (!isBluetooth && usb is null && !builtInInput) continue;
            DeviceNode anchor = isBluetooth ? bluetooth ?? chain.First(item => item.Id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
                : usb ?? chain.FirstOrDefault(item => item.Id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)
                    || item.Id.StartsWith("I2C\\", StringComparison.OrdinalIgnoreCase)) ?? node;
            string connection = isBluetooth ? "蓝牙" : usb is not null ? "USB" : "内置";
            string? container = chain.Select(item => item.ContainerId).FirstOrDefault(ValidContainer);
            string key = isBluetooth && bluetooth is null && ValidContainer(container) ? "bluetooth/" + container : anchor.Id;
            if (!groups.TryGetValue(key, out var group)) group = (anchor, connection, []);
            group.Members.Add(node); groups[key] = group;
        }

        // HFP services can sit below a software bus instead of their Bluetooth DEV node.
        // A unique remote-device container is evidence; a shared radio or product name is not.
        foreach (var pair in groups.Where(pair => pair.Value.Connection == "蓝牙" && !BluetoothDevice(pair.Value.Anchor)).ToArray())
        {
            var containers = pair.Value.Members.Append(pair.Value.Anchor).Select(node => node.ContainerId).Where(ValidContainer).ToArray();
            var owners = groups.Where(owner => owner.Key != pair.Key && owner.Value.Connection == "蓝牙"
                && BluetoothDevice(owner.Value.Anchor)
                && owner.Value.Members.Append(owner.Value.Anchor).Any(node => containers.Any(container => SameContainer(container, node.ContainerId)))).ToArray();
            if (owners.Length != 1) continue;
            var owner = owners[0].Value;
            owner.Members.AddRange(pair.Value.Members); owner.Members.Add(pair.Value.Anchor);
            groups[owners[0].Key] = owner; groups.Remove(pair.Key);
        }

        // Audio endpoint devnodes often have a software parent. Join only a unique physical container,
        // rather than discarding every SWD node or merging all outputs of a sound card.
        foreach (var endpoint in audioEndpoints)
        {
            IEnumerable<DeviceNode> ControllerChain(string? controllerId)
            {
                if (string.IsNullOrEmpty(controllerId)) return [];
                var owners = interfaceOwners[controllerId].DistinctBy(node => node.Id, StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
                var controller = nodes.GetValueOrDefault(controllerId) ?? (owners.Length == 1 ? owners[0] : null);
                // Audio topology may report a device-interface path with a reference suffix.
                // Resolve it only when the decoded instance actually exists in the PnP snapshot.
                int classStart = controllerId.IndexOf("#{", StringComparison.Ordinal);
                if (controller is null && controllerId.StartsWith(@"\\?\", StringComparison.Ordinal) && classStart > 4)
                    controller = nodes.GetValueOrDefault(controllerId[4..classStart].Replace('#', '\\'));
                return controller is null ? [] : Ancestors(controller, nodes);
            }
            var chain = ControllerChain(endpoint.AudioControllerId).Concat(ControllerChain(endpoint.ParentId)).ToArray();
            if (chain.Any(parent => Virtual(parent.Id))) continue;
            var matches = chain.Select(parent => groups.Where(pair => !pair.Value.Anchor.IsAudioEndpoint
                && pair.Value.Members.Append(pair.Value.Anchor).Any(member => member.Id.Equals(parent.Id, StringComparison.OrdinalIgnoreCase))).ToArray())
                .FirstOrDefault(owners => owners.Length > 0) ?? [];
            if (matches.Length == 0)
                matches = groups.Where(pair => !pair.Value.Anchor.IsAudioEndpoint && pair.Value.Members.Append(pair.Value.Anchor)
                    .Any(member => SameContainer(member.ContainerId, endpoint.ContainerId))).ToArray();
            // If the immediate endpoint container is missing, use its actual parent chain.
            if (matches.Length == 0)
                matches = groups.Where(pair => pair.Value.Connection == "蓝牙" && pair.Value.Members.Append(pair.Value.Anchor)
                    .Any(member => chain.Any(parent => SameContainer(member.ContainerId, parent.ContainerId)))).ToArray();
            if (matches.Length == 1)
            {
                var group = matches[0].Value;
                if (group.Connection == "USB" && group.Anchor.Removable == false && endpoint.Has35mmJack)
                {
                    // An external analog jack on an internal USB codec is a user-facing output.
                    groups[endpoint.Id] = (endpoint, "声卡接口", [endpoint]);
                    continue;
                }
                group.Members.RemoveAll(member => member.Id.Equals(endpoint.Id, StringComparison.OrdinalIgnoreCase));
                group.Members.Add(endpoint);
                groups[matches[0].Key] = group;
            }
            else
            {
                if (BluetoothAudioService(endpoint.Name)) continue;
                string connection = chain.Any(parent => parent.Id.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase)
                    || parent.Id.StartsWith("INTELAUDIO\\", StringComparison.OrdinalIgnoreCase)) ? "声卡接口" : "系统音频";
                groups[endpoint.Id] = (endpoint, connection, [endpoint]);
            }
        }

        var result = new List<DeviceRecord>();
        foreach (var (_, group) in groups)
        {
            string[] categories = group.Members.Select(member => member.Category)
                .Where(category => category is "鼠标" or "键盘" or "手柄" or "音频" or "HID" or "摄像头" or "磁盘" or "打印机").Distinct().Order().ToArray();
            // USB hubs/controllers and Bluetooth service nodes alone do not count as user peripherals.
            if (categories.Length == 0 && !ControllerUsage(group.Members)) continue;
            var representative = group.Members.OrderBy(member => Rank(member.Category)).ThenBy(member => member.Id, StringComparer.OrdinalIgnoreCase).First();
            var (category, reason) = Classify(group.Anchor, group.Members, categories);
            categories = categories.Append(category).Distinct().ToArray();
            var anchor = group.Anchor;
            // A driver function on the HFP bus is not a paired device in its own right.
            bool hfp = group.Members.Append(anchor).Any(member => BluetoothAudioService(member.Name)
                || member.Id.StartsWith("BTHHFENUM\\", StringComparison.OrdinalIgnoreCase));
            if (hfp && group.Connection == "蓝牙" && !BluetoothDevice(anchor)) continue;
            if (!BluetoothDevice(anchor) && category is ("音频" or "耳机")
                && group.Connection == "蓝牙"
                && !group.Members.Any(member => member.IsAudioEndpoint && !GenericName(member.Name))) continue;
            var ids = UsbIds().Match(anchor.Id);
            if (!ids.Success) ids = group.Members.Select(member => UsbIds().Match(member.Id)).FirstOrDefault(match => match.Success) ?? ids;
            string? vid = ids.Success ? ids.Groups[1].Value.ToUpperInvariant() : null;
            string? pid = ids.Success ? ids.Groups[2].Value.ToUpperInvariant() : null;
            string? product = new[] { anchor }.Concat(group.Members).Select(member => member.ProductName)
                .FirstOrDefault(name => !GenericName(name));
            product ??= new[] { anchor.Name }.Concat(group.Members.Where(member => member.IsAudioEndpoint).Select(member => member.Name))
                .Append(representative.Name).FirstOrDefault(name => !GenericName(name));
            string name = product ?? (vid is not null ? $"{category} · {vid}:{pid}" : representative.Name);
            if (product is null && hfp && group.Connection == "蓝牙")
                name = category == "耳机" ? "蓝牙耳机" : "蓝牙免提音频";
            if (category == "触控板" && product is null) name = group.Connection == "内置" ? "内置触控板" : "触控板";
            else if (group.Connection == "内置" && product is null) name = category == "鼠标" ? "内置指针设备" : "内置" + category;
            else if (name.Equals("Integrated Camera", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Integrated Webcam", StringComparison.OrdinalIgnoreCase)) name = "内置摄像头";
            var members = group.Members.Select(member => member.Id).Append(anchor.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] interfaces = group.Members.SelectMany(member => member.Interfaces ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string? location = anchor.Location ?? group.Members.Select(member => member.Location).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            string? locationPath = anchor.LocationPath ?? group.Members.Select(member => member.LocationPath).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            // Keep the representative legacy instance ID for existing plugin commands. All aliases remain available.
            result.Add(new(representative.Id, name, category, group.Connection, vid, pid,
                (representative.Interfaces ?? []).FirstOrDefault() ?? interfaces.FirstOrDefault() ?? "", location, anchor.ContainerId ?? representative.ContainerId)
            { MemberInstanceIds = members, InterfacePaths = interfaces, Categories = categories, LocationPath = locationPath,
                ClassificationNote = reason, ReportedName = product ?? representative.Name,
                IsInternal = group.Connection == "内置" || group.Connection == "USB" && anchor.Removable == false,
                Has35mmJack = group.Members.Any(member => member.IsAudioEndpoint && member.Has35mmJack),
                AudioRenderEndpointIds = group.Members.Where(member => member.IsAudioEndpoint)
                    .SelectMany(member => member.RenderEndpointId is { } id ? [id] : member.Interfaces ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                AudioFormFactor = group.Members.Where(member => member.IsAudioEndpoint).OrderBy(member => member.AudioFormFactor is 3 or 5 ? 0 : 1)
                    .Select(member => member.AudioFormFactor).FirstOrDefault() });
        }
        return result.OrderBy(device => Rank(device.Category)).ThenBy(device => device.Name)
            .ThenBy(device => device.LocationPath ?? device.InstanceId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static (string Category, string Reason) Classify(DeviceNode anchor, List<DeviceNode> members, string[] categories)
    {
        if (ControllerUsage(members) && !categories.Any(category => category is "摄像头" or "磁盘" or "打印机"))
            return ("手柄", "设备声明了游戏控制器、摇杆、多轴控制器用途或 XUSB 兼容接口。");
        string? physicalKind = new[] { "摄像头", "磁盘", "打印机", "音频" }.FirstOrDefault(categories.Contains);
        if (physicalKind is not null) return (physicalKind == "音频" && members.Any(member => member.IsAudioEndpoint && member.AudioFormFactor is 3 or 5) ? "耳机" : physicalKind, physicalKind == "音频"
            ? "依据 Windows 音频设备或输出端点识别。模拟耳机可能与扬声器共用声卡输出；端点不能证明耳机的具体品牌型号。"
            : "依据 Windows 设备类别识别；附带的 HID 按键接口不改变主类型。");
        var compatible = members.SelectMany(member => member.CompatibleIds ?? []).ToArray();
        if (categories.Contains("手柄")) return ("手柄", "Windows 报告的游戏控制器接口。");
        if (compatible.Any(id => id.Contains("UP:000D_U:0005", StringComparison.OrdinalIgnoreCase)))
            return ("触控板", "设备兼容标识声明了 Digitizers / Touch Pad 用途。");
        if (categories.Contains("鼠标") && categories.Contains("键盘"))
        {
            string description = anchor.ProductName ?? anchor.Name;
            bool namedKeyboard = description.Contains("keyboard", StringComparison.OrdinalIgnoreCase) || description.Contains("键盘");
            bool namedMouse = description.Contains("mouse", StringComparison.OrdinalIgnoreCase) || description.Contains("鼠标");
            if (namedKeyboard != namedMouse) return (namedKeyboard ? "键盘" : "鼠标", "设备产品描述给出主类型，同时保留键盘、鼠标两类接口能力。");
            bool bootKeyboard = compatible.Any(id => id.Equals(@"USB\Class_03&SubClass_01&Prot_01", StringComparison.OrdinalIgnoreCase));
            bool bootMouse = compatible.Any(id => id.Equals(@"USB\Class_03&SubClass_01&Prot_02", StringComparison.OrdinalIgnoreCase));
            if (bootKeyboard != bootMouse) return (bootKeyboard ? "键盘" : "鼠标", "依据唯一的 USB Boot 接口类型选择主分类；复合接口的其他能力仍保留。");
            return ("键鼠复合设备", "同时提供键盘和鼠标接口，现有通用信息不能确定主类型，未强行归为鼠标。可由设备插件补充产品类型。");
        }
        return (categories.FirstOrDefault(category => category is "鼠标" or "键盘") ?? "HID",
            "依据 Windows 接口类别识别功能；接口类别不能证明该控制器的具体产品用途。");
    }

    private static bool ControllerUsage(IEnumerable<DeviceNode> members) => members.SelectMany(member => member.CompatibleIds ?? []).Any(id =>
            id.Contains("UP:0001_U:0004", StringComparison.OrdinalIgnoreCase)
            || id.Contains("UP:0001_U:0005", StringComparison.OrdinalIgnoreCase)
            || id.Contains("UP:0001_U:0008", StringComparison.OrdinalIgnoreCase)
            || id.Equals(@"USB\MS_COMP_XUSB10", StringComparison.OrdinalIgnoreCase)
            || id.Equals(@"USB\MS_COMP_XUSB20", StringComparison.OrdinalIgnoreCase));

    private static bool SameContainer(string? left, string? right) => ValidContainer(left) && ValidContainer(right)
        && Guid.Parse(left!) == Guid.Parse(right!);

    private static IEnumerable<DeviceNode> Ancestors(DeviceNode node, IReadOnlyDictionary<string, DeviceNode> nodes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (seen.Add(node.Id))
        {
            yield return node;
            if (node.ParentId is null || !nodes.TryGetValue(node.ParentId, out var parent)) yield break;
            node = parent;
        }
    }

    private static int Rank(string category) => category switch { "鼠标" => 0, "键盘" => 1, "音频" => 2, "摄像头" or "磁盘" or "打印机" => 3, "HID" => 4, _ => 5 };
    private static readonly Guid ComputerContainer = new("00000000-0000-0000-ffff-ffffffffffff");
    private static bool ValidContainer(string? id) => Guid.TryParse(id, out var guid) && guid != Guid.Empty && guid != ComputerContainer;
    private static bool BluetoothDevice(DeviceNode node) => node.Id.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase)
        || node.Id.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase);
    private static bool BluetoothAudioService(string name) => name.Contains("Microsoft_HFP_Audio_Device", StringComparison.OrdinalIgnoreCase);
    private static bool Virtual(string id) => id.StartsWith("ROOT\\RDP", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("VMBUS\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("UMB\\", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("ROOT\\VIRTUAL", StringComparison.OrdinalIgnoreCase);
    private static bool GenericName(string? name) => string.IsNullOrWhiteSpace(name)
        || name.Contains("HID", StringComparison.OrdinalIgnoreCase) || name.Contains("USB Input", StringComparison.OrdinalIgnoreCase)
        || name.Contains("USB 输入", StringComparison.OrdinalIgnoreCase) || name.Contains("Composite", StringComparison.OrdinalIgnoreCase)
        || name.Contains("复合设备", StringComparison.OrdinalIgnoreCase) || name.Contains("Bluetooth Device", StringComparison.OrdinalIgnoreCase)
        || BluetoothAudioService(name) || name is "USB Audio Device" or "USB 音频设备" or "蓝牙外围设备" or "Bluetooth Peripheral Device";

    [GeneratedRegex("VID_([A-Fa-f0-9]{4}).*PID_([A-Fa-f0-9]{4})")]
    private static partial Regex UsbIds();
}
