using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hhub.App;

internal sealed class NativeTray : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 73;
    private const string ShowMessageName = "Hhub.ShowMainWindow.v1";
    private readonly nint _window;
    private readonly nint _icon;
    private readonly SubclassProcedure _procedure;
    private readonly Action<Action> _dispatch;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly uint _showMessage;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private NativeTrayTooltip? _tooltip;
    private bool _hovering;
    private int _hoverX, _hoverY;
    private string? _tooltipText;
    public Func<string>? BatteryText { get; set; }
    public Func<bool>? IsDark { get; set; }
    public Action? Hovered { get; set; }
    private bool _disposed;
    public bool IsAvailable { get; private set; }

    internal static int NativeDataSize => Marshal.SizeOf<NotifyIconData>();

    internal static void ValidateIconFile(string path)
    {
        nint icon = LoadImage(0, path, 1, 32, 32, 0x10);
        if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        DestroyIcon(icon);
    }

    public NativeTray(nint window, string iconPath, Action<Action> dispatch, Action show, Action exit)
    {
        _window = window; _dispatch = dispatch; _show = show; _exit = exit;
        _showMessage = RegisterWindowMessage(ShowMessageName);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(0, iconPath, 1, 32, 32, 0x10);
        if (_icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _procedure = WindowProcedure;
        if (!SetWindowSubclass(window, _procedure, 1, 0))
        {
            DestroyIcon(_icon);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        _data = new NotifyIconData
        {
            Size = (uint)NativeDataSize, Window = window, Id = 1,
            Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = _icon,
            Tip = "Hhub " + System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(NativeTray).Assembly)?.InformationalVersion, Info = "", InfoTitle = ""
        };
        if (!AddIcon())
        {
            RemoveWindowSubclass(window, _procedure, 1); DestroyIcon(_icon);
            throw new Win32Exception("无法添加系统托盘图标");
        }
    }

    private bool AddIcon()
    {
        IsAvailable = ShellNotifyIcon(0, ref _data);
        if (IsAvailable) { _data.Version = 4; ShellNotifyIcon(4, ref _data); }
        return IsAvailable;
    }

    public static void RequestShowExisting()
    {
        uint message = RegisterWindowMessage(ShowMessageName);
        if (!NativeWindowActivation.RequestExisting(message)) PostMessage((nint)0xffff, message, 0, 0);
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (!_disposed)
        {
            if (message == _showMessage) { _show(); return 0; }
            if (message == _taskbarCreated) AddIcon();
            if (message == CallbackMessage)
            {
                uint notification = (uint)(lParam.ToInt64() & 0xffff);
                if (notification is 0x202 or 0x400 or 0x401)
                {
                    _hovering = false; _tooltip?.Hide();
                    _show();
                }
                else if (notification == 0x7b) ShowMenu();
                else if (notification == 0x406) _dispatch(() =>
                {
                    _hovering = true; GetCursorPos(out var point); _hoverX = point.X; _hoverY = point.Y;
                    UpdateBatteryTip(force: true); Hovered?.Invoke();
                });
                else if (notification == 0x407) _dispatch(() => { _hovering = false; _tooltip?.Hide(); });
                return 0;
            }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void UpdateBatteryTip(bool force = false)
    {
        if (!_hovering || _disposed) return;
        string text = BatteryText?.Invoke() ?? "Hhub";
        if (!force && text == _tooltipText) return;
        _tooltipText = text;
        try
        {
            _tooltip ??= new NativeTrayTooltip(_window);
            _tooltip.Show(text, IsDark?.Invoke() == true, _hoverX, _hoverY);
        }
        catch (Win32Exception)
        {
            // Keep a working system tooltip if a native popup cannot be created.
            _data.Tip = text.Length > 127 ? text[..127] : text;
            if (_data.Tip.Length > 0 && char.IsHighSurrogate(_data.Tip[^1])) _data.Tip = _data.Tip[..^1];
            _data.Flags |= 0x80; ShellNotifyIcon(1, ref _data);
        }
    }

    private void ShowMenu()
    {
        _hovering = false; _tooltip?.Hide();
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, 0, 1, "打开 Hhub");
            AppendMenu(menu, 0, 2, "退出 Hhub");
            GetCursorPos(out var point); SetForegroundWindow(_window);
            uint selected = TrackPopupMenu(menu, 0x100 | 0x80 | 2, point.X, point.Y, 0, _window, 0);
            PostMessage(_window, 0, 0, 0);
            if (selected == 1) _show();
            else if (selected == 2) _dispatch(_exit);
            else ShellNotifyIcon(3, ref _data);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; IsAvailable = false;
        _tooltip?.Dispose();
        ShellNotifyIcon(2, ref _data);
        RemoveWindowSubclass(_window, _procedure, 1); DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags; public uint Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}
