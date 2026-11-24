using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Hhub.Core;
using Hhub.Desktop.Pages;

namespace Hhub.Desktop;

internal sealed class ForegroundScenes : IDisposable
{
    private const uint ForegroundChanged = 3;
    private const uint SkipOwnProcess = 0x0002;
    private readonly WinEventHandler _handler;
    private readonly Action<string?> _display;
    private SceneRule[] _rules = [];
    private string _fallback = SceneCatalog.DefaultScene;
    private string? _foreground;
    private string? _current;
    private nint _hook;
    private bool _enabled = true;
    private IReadOnlyList<ScenePreview> _scenes = [];
    private string? _preferred;
    public string? ForegroundApplication => _foreground;
    public event Action<string?>? ForegroundApplicationChanged;

    public ForegroundScenes(Action<string?> display)
    {
        _display = display;
        _handler = HandleForeground;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (_scenes.Count > 0) Update(_scenes, _preferred);
    }

    public void Update(IReadOnlyList<ScenePreview> scenes, string? preferred, bool selectPreferred = false)
    {
        _scenes = scenes;
        _preferred = preferred;
        _fallback = scenes.FirstOrDefault(scene => scene.Name == preferred)?.Name
            ?? scenes.FirstOrDefault(scene => scene.Name == SceneCatalog.DefaultScene)?.Name
            ?? scenes.FirstOrDefault()?.Name ?? SceneCatalog.DefaultScene;
        _rules = !_enabled ? [] : scenes.SelectMany((scene, index) => scene.Applications
            .Where(application => !SceneCatalog.IsSampleApplication(application))
            .Select((application, entry) => new SceneRule
            {
                Id = $"{index}:{entry}", Name = scene.Name, Application = application,
                SceneId = scene.Name, Priority = scenes.Count - index
            })).ToArray();
        if (_hook == 0)
        {
            // The out-of-context callback runs on this window's dispatcher thread.
            _hook = SetWinEventHook(ForegroundChanged, ForegroundChanged, 0, _handler, 0, 0, SkipOwnProcess);
            if (_hook == 0) Trace.WriteLine(new Win32Exception(Marshal.GetLastWin32Error()));
            Observe(GetForegroundWindow(), refresh: false);
        }
        if (selectPreferred) Select(_fallback);
        else Refresh();
    }

    public void Select(string name)
    {
        _preferred = _fallback = _current = name;
        _display(name);
    }

    internal sealed record RunningApplication(string Executable, string Label);

    public static RunningApplication[] ReadRunningApplications()
    {
        var applications = new Dictionary<string, RunningApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == 0) continue;
                    string executable = process.ProcessName + ".exe";
                    string title = process.MainWindowTitle;
                    applications.TryAdd(executable, new(executable,
                        string.IsNullOrWhiteSpace(title) ? executable : $"{executable} · {title}"));
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
            }
        }
        return applications.Values.OrderBy(application => application.Executable, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private void Refresh()
    {
        string effective = SceneEngine.EffectiveScene(_rules, new ActivitySnapshot(
            _foreground, System.Collections.Frozen.FrozenSet<string>.Empty), _fallback, locked: false);
        if (!string.Equals(_current, effective, StringComparison.Ordinal))
        {
            _current = effective;
            _display(effective);
        }
    }

    private void HandleForeground(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint timestamp)
    {
        if (hook == _hook && eventType == ForegroundChanged) Observe(window);
    }

    private void Observe(nint window, bool refresh = true)
    {
        _ = GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId) return;
        string? application = null;
        if (pid != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                application = process.ProcessName;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        if (_foreground == application) return;
        _foreground = application;
        ForegroundApplicationChanged?.Invoke(application);
        if (refresh) Refresh();
    }

    public void Dispose()
    {
        if (_hook != 0) UnhookWinEvent(_hook);
        _hook = 0;
        _foreground = null;
    }

    private delegate void WinEventHandler(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint timestamp);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventHandler callback,
        uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
