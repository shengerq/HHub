using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Hhub.Core;

namespace Hhub.App;

public partial class App : Application
{
#if HHUB_UNIFIED
    private UnifiedWindow? _window;
#else
    private MainWindow? _window;
#endif
#if !HHUB_UNIFIED
    private InstanceLease? _instanceLease;
#endif
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if HHUB_UNIFIED
        _window = new UnifiedWindow();
        _window.Activate();
#else
        string root = AppStorage.DefaultRoot;
        _instanceLease = InstanceLease.TryAcquire(root);
        if (_instanceLease is null && !HasOlderInstance())
        {
            if (DifferentHostPath() is { } existingPath)
            {
                MessageBox(0, "另一个版本的 Hhub 仍在运行：\n" + existingPath
                    + "\n\n请从托盘菜单退出该版本，再打开此版本。仅关闭窗口会保留后台，无法切换版本。", "Hhub · 版本尚未切换", 0x40);
                Exit(); return;
            }
            NativeTray.RequestShowExisting(); Exit(); return;
        }
        if (_instanceLease is null || HasOlderInstance())
        {
            _instanceLease?.Dispose();
            _instanceLease = null;
            MessageBox(0, "Hhub 已在运行。请使用已经打开的窗口；如果它是旧版，请先关闭旧版 Hhub，再打开此版本。", "Hhub", 0x40);
            Exit();
            return;
        }
        _window = new MainWindow();
        _window.Closed += (_, _) => { _instanceLease?.Dispose(); _instanceLease = null; };
        _window.Launch();
#endif
    }

    private static string? DifferentHostPath()
    {
        using var current = Process.GetCurrentProcess();
        var candidates = Process.GetProcessesByName(current.ProcessName);
        byte[] expected = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(PluginCompatibility).Assembly.Location));
        byte[] expectedUi = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(App).Assembly.Location));
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    if (process.Id == current.Id || process.SessionId != current.SessionId || process.HasExited) continue;
                    if (process.MainModule?.FileName is not { } path) continue;
                    string core = Path.Combine(Path.GetDirectoryName(path)!, "Hhub.Core.dll");
                    string ui = Path.Combine(Path.GetDirectoryName(path)!, "Hhub.App.dll");
                    if (File.Exists(core) && (!expected.AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(core)))
                        || File.Exists(ui) && !expectedUi.AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ui)))))
                        return path;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
                { /* An exiting or inaccessible process cannot establish its build identity. */ }
            }
            return null;
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }

    private static bool HasOlderInstance()
    {
        using var current = Process.GetCurrentProcess();
        // Earlier previews do not participate in the shared instance lease.
        var candidates = Process.GetProcessesByName(current.ProcessName);
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    if (process.Id == current.Id || process.SessionId != current.SessionId || process.HasExited) continue;
                    if (process.MainModule?.FileName is not { } path) continue;
                    var version = FileVersionInfo.GetVersionInfo(path);
                    bool isOfficialRelease = version.ProductVersion == "0.5";
                    if (!isOfficialRelease && new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) < new Version(0, 5, 3)) return true;
                }
                catch (InvalidOperationException) { /* Another instance exited during enumeration. */ }
                catch (System.ComponentModel.Win32Exception) { /* The process is no longer accessible. */ }
            }
            return false;
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string message, string title, uint flags);
}
