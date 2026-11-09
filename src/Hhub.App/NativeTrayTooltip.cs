using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hhub.App;

// A standard Windows tooltip with dynamically allocated text, avoiding szTip's 127-character limit.
internal sealed class NativeTrayTooltip : IDisposable
{
    private readonly nint _window;
    private ToolInfo _tool;
    private nint _text;
    public NativeTrayTooltip(nint owner)
    {
        var controls = new CommonControls { Size = 8, Classes = 0xff }; InitCommonControlsEx(ref controls);
        // It must remain visible when the main window is hidden or minimized.
        _window = CreateWindowEx(0x08000088, "tooltips_class32", null, 0x80000003, 0, 0, 0, 0, 0, 0, 0, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _tool = new() { Size = (uint)Marshal.SizeOf<ToolInfo>(), Flags = 0x20, Window = owner, Id = 1, Text = 0 };
        SendTool(_window, 0x432, 0, ref _tool); // TTM_ADDTOOLW
        SendMessage(_window, 0x418, 0, 420); // TTM_SETMAXTIPWIDTH
    }
    public void Show(string text, bool dark, int x, int y)
    {
        SetWindowTheme(_window, dark ? "DarkMode_Explorer" : "Explorer", null);
        var previous = _text;
        _text = Marshal.StringToHGlobalUni(text); _tool.Text = _text;
        SendTool(_window, 0x439, 0, ref _tool); // TTM_UPDATETIPTEXTW
        if (previous != 0) Marshal.FreeHGlobal(previous);
        SendMessage(_window, 0x412, 0, (nint)((y & 0xffff) << 16 | (x & 0xffff))); // TTM_TRACKPOSITION
        SendTool(_window, 0x411, 1, ref _tool); // TTM_TRACKACTIVATE
    }
    public void Hide() => SendTool(_window, 0x411, 0, ref _tool);
    public void Dispose() { DestroyWindow(_window); if (_text != 0) Marshal.FreeHGlobal(_text); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CommonControls { public uint Size, Classes; }
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitCommonControlsEx(ref CommonControls controls);
    [StructLayout(LayoutKind.Sequential)] private struct ToolInfo
    { public uint Size, Flags; public nint Window; public nuint Id; public Rect Rect; public nint Instance, Text, Param, Reserved; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extended, string name, string? title, uint style, int x, int y, int width, int height, nint owner, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendTool(nint window, uint message, nuint wParam, ref ToolInfo tool);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(nint window, string? app, string? ids);
}
