using System.Runtime.InteropServices;

namespace Hhub.Desktop;

internal static class TrayBattery
{
    public static void Register(string program, string id, string name, string? data)
    {
        string payload = string.Join('\n', program, data ?? "", id, name);
        if (!Send(payload, 2)) System.Diagnostics.Trace.WriteLine("托盘电量服务注册失败。");
    }

    public static void PublishStatus(string pluginId, IEnumerable<string> rows) => Send(pluginId + "\n" + string.Join('\n', rows), 5);
    public static void UpdateStatus(string pluginId, string row) => Send(pluginId + "\n" + row, 9);
    public static void SetGlobalVisibility(bool enabled) => Send(enabled ? "1" : "0", 6);
    public static void SetPluginVisibility(string pluginId, bool enabled) => Send((enabled ? "1" : "0") + pluginId, 7);

    public static void RefreshEfficiency()
    {
        nint tray = FindWindow(HostInstallation.TrayWindowClass, null);
        if (tray != 0) PostMessage(tray, 0x8006, 0, 0);
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    public static void LoadVisibility() => Send(string.Join('\n',
        new[] { Pages.PluginPreferences.Load().TrayBattery ? "1" : "0" }.Concat(
            Pages.PluginPreferences.LoadDeviceDisplay().Where(item => !item.Value.TrayBattery).Select(item => item.Key))), 8);

    private static bool Send(string payload, int type)
    {
        nint tray = FindWindow(HostInstallation.TrayWindowClass, null);
        if (tray == 0) return false;
        nint sender = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, -3, 0, 0, 0);
        if (sender == 0) return false;
        nint characters = Marshal.StringToHGlobalUni(payload);
        nint structure = Marshal.AllocHGlobal(Marshal.SizeOf<CopyData>());
        try
        {
            Marshal.StructureToPtr(new CopyData { Type = type, Size = (payload.Length + 1) * 2, Content = characters }, structure, false);
            return SendMessageTimeout(tray, 0x004a, sender, structure, 3, 1000, out nint result) != 0 && result != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(structure);
            Marshal.FreeHGlobal(characters);
            DestroyWindow(sender);
        }
    }

    public static (string? Text, bool Disconnected) Read(string target)
    {
        if (target.Length != 64 || !target.All(Uri.IsHexDigit)) return default;
        nint tray = FindWindow(HostInstallation.TrayWindowClass, null);
        if (tray == 0) return default;
        byte[] hash = Convert.FromHexString(target);
        var pinned = GCHandle.Alloc(hash, GCHandleType.Pinned);
        nint structure = Marshal.AllocHGlobal(Marshal.SizeOf<CopyData>());
        try
        {
            Marshal.StructureToPtr(new CopyData { Type = 4, Size = hash.Length, Content = pinned.AddrOfPinnedObject() }, structure, false);
            if (SendMessageTimeout(tray, 0x004a, 0, structure, 3, 80, out nint result) == 0) return default;
            int value = (int)result - 1;
            return value switch
            {
                >= 0 and <= 100 => ($"{value}%", false),
                >= 102 and <= 202 => ($"{value - 102}%（上次）", false),
                203 => (null, true),
                _ => default
            };
        }
        finally
        {
            Marshal.FreeHGlobal(structure);
            pinned.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public nint Type;
        public int Size;
        public nint Content;
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? name);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);
}
