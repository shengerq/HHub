using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hhub.App;

internal static class NativeWindowActivation
{
    public static bool BringToFront(nint window) => window != 0 && SetForegroundWindow(window);

    public static bool RequestExisting(uint message)
    {
        using var current = Process.GetCurrentProcess();
        var candidates = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id != current.Id && process.SessionId == current.SessionId) candidates.Add((uint)process.Id);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        bool requested = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (!candidates.Contains(owner)) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "Hhub") return true;
            // The newly launched process can pass its user-initiated foreground
            // permission to the existing instance, including a hidden tray window.
            AllowSetForegroundWindow(owner);
            requested = PostMessage(window, message, 0, 0);
            return !requested;
        }, 0);
        return requested;
    }

    private delegate bool WindowVisitor(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowVisitor visitor, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
