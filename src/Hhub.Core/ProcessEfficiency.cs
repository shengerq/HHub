using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Hhub.Core;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ProcessEfficiency : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly ProcessPriorityClass _priority;
    private bool _enabled, _cpuSetsChanged;
    public ProcessEfficiency() => _priority = _process.PriorityClass;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        var state = new Throttling { Version = 1, ControlMask = enabled ? 1u : 0, StateMask = enabled ? 1u : 0 };
        if (!SetProcessInformation(_process.Handle, 4, ref state, 12)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _enabled = enabled;
        _process.PriorityClass = enabled ? ProcessPriorityClass.BelowNormal : _priority;
        uint memoryPriority = enabled ? 2u : 5u;
        if (!SetMemoryInformation(_process.Handle, 0, ref memoryPriority, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (enabled)
        {
            uint[] efficient = EfficientCpuSets();
            if (efficient.Length > 0)
            {
                if (!SetProcessDefaultCpuSets(_process.Handle, efficient, (uint)efficient.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                _cpuSetsChanged = true;
            }
        }
        else if (_cpuSetsChanged)
        {
            if (!SetProcessDefaultCpuSets(_process.Handle, null, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _cpuSetsChanged = false;
        }
    }

    public static uint[] SelectEfficientSets(IEnumerable<(uint Id, byte Efficiency, byte Flags)> sets)
    {
        var available = sets.Where(set => (set.Flags & 2) == 0 || (set.Flags & 4) != 0).ToArray();
        if (available.Select(set => set.Efficiency).Distinct().Count() < 2) return [];
        byte efficient = available.Min(set => set.Efficiency);
        return available.Where(set => set.Efficiency == efficient).Select(set => set.Id).ToArray();
    }

    private uint[] EfficientCpuSets()
    {
        GetSystemCpuSetInformation(null, 0, out uint required, _process.Handle, 0);
        if (required == 0) return [];
        byte[] buffer = new byte[required];
        if (!GetSystemCpuSetInformation(buffer, required, out uint returned, _process.Handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var sets = new List<(uint, byte, byte)>();
        for (int offset = 0; offset + 8 <= returned;)
        {
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset)));
            if (size < 8 || size > returned - offset) break;
            if (size >= 24 && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4)) == 0)
                sets.Add((BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 8)), buffer[offset + 18], buffer[offset + 19]));
            offset += size;
        }
        return SelectEfficientSets(sets);
    }

    public void Dispose() { try { SetEnabled(false); } finally { _process.Dispose(); } }
    [StructLayout(LayoutKind.Sequential)] private struct Throttling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int kind, ref Throttling state, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMemoryInformation(nint process, int kind, ref uint priority, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(byte[]? buffer, uint length, out uint returned, nint process, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDefaultCpuSets(nint process, uint[]? ids, uint count);
}

// Background workers (including elevated collectors) read one per-user state.
// Each changes only its own scheduling; no access to an elevated child is needed.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BackgroundEfficiencySession : IDisposable
{
    public static string DefaultName
    { get { using var identity = WindowsIdentity.GetCurrent(); return @"Local\Hhub.BackgroundEfficiency." + identity.User!.Value; } }
    private readonly EventWaitHandle _state;
    public BackgroundEfficiencySession(string? name = null)
    {
        _state = new(false, EventResetMode.ManualReset, name ?? DefaultName);
        _state.Reset();
    }
    public void SetEnabled(bool enabled) { if (enabled) _state.Set(); else _state.Reset(); }
    public void Dispose() { _state.Reset(); _state.Dispose(); }

    public static IDisposable? ObserveCurrentProcess(bool latencySensitive, string? name = null)
    {
        if (latencySensitive || !EventWaitHandle.TryOpenExisting(name ?? DefaultName, out var state)) return null;
        return new Observer(state);
    }

    private sealed class Observer : IDisposable
    {
        private readonly EventWaitHandle _state;
        private readonly ProcessEfficiency _policy = new();
        private readonly Timer _timer;
        private readonly object _gate = new();
        private bool _disposed;
        public Observer(EventWaitHandle state)
        {
            _state = state;
            _timer = new(_ => Update(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
        private void Update()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { _policy.SetEnabled(_state.WaitOne(0)); }
                catch (Win32Exception error) { Trace.WriteLine("Hhub efficiency: " + error.Message); }
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true; _timer.Dispose(); _state.Dispose(); _policy.Dispose();
            }
        }
    }
}
