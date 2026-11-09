using System.Runtime.InteropServices;
using Hhub.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Hhub.App;

// Read-only enumeration. This service never opens an audio stream or changes routing/volume.
public static class AudioDeviceInventory
{
    private static readonly PropertyKey ContainerKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    public static IReadOnlyList<DeviceNode> Capture(List<string> issues)
    {
        var nodes = new List<DeviceNode>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        var properties = device.Properties;
                        object? Read(PropertyKey key) => properties[key]?.Value;
                        string? container = Read(ContainerKey)?.ToString();
                        string? parent = Read(PropertyKeys.PKEY_Device_ControllerDeviceId)?.ToString();
                        object? form = Read(PropertyKeys.PKEY_AudioEndpoint_FormFactor);
                        int? kind = form is null ? null : Convert.ToInt32(form);
                        string endpointId = device.ID;
                        var topology = AudioJackTopology.Read(endpointId, kind is 3 or 5);
                        string id = @"SWD\MMDEVAPI\" + endpointId;
                        nodes.Add(new(id, device.FriendlyName, "音频", parent, container,
                            Interfaces: [endpointId], IsAudioEndpoint: true, AudioFormFactor: kind)
                            { AudioControllerId = topology.Controller, RenderEndpointId = endpointId, Has35mmJack = topology.Has35mmJack });
                    }
                    catch (COMException ex) { issues.Add("音频输出已变化：" + ex.Message); }
                }
            }
        }
        catch (COMException ex) { issues.Add("音频输出枚举失败：" + ex.Message); }
        return nodes;
    }

    public static (string[] Compatible, string? Default) ReadPlaybackTargets(IEnumerable<string> associatedIds)
    {
        var ids = associatedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = device.ID;
        }

        var compatible = new List<string>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                if (!ids.Contains(device.ID)) continue;
                try
                {
                    using var client = device.AudioClient;
                    var format = client.MixFormat;
                    // Match the format requirements enforced when FXsound starts playback.
                    if (format.Channels >= 2 && format.SampleRate >= 32000)
                        compatible.Add(device.ID);
                }
                catch (COMException) { /* The endpoint may have disconnected during enumeration. */ }
            }
        }
        return (compatible.ToArray(), defaultId);
    }

    public static IDisposable Watch(Action changed) => new Notifications(changed);

    [ComVisible(true)]
    private sealed class Notifications : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly Action _changed;
        private bool _disposed;
        public Notifications(Action changed)
        {
            _changed = changed;
            try { Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(this)); }
            catch { _enumerator.Dispose(); throw; }
        }
        public void OnDeviceStateChanged(string id, DeviceState state) => _changed();
        public void OnDeviceAdded(string id) => _changed();
        public void OnDeviceRemoved(string id) => _changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) => _changed();
        public void OnPropertyValueChanged(string id, PropertyKey key) => _changed();
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _enumerator.Dispose();
        }
    }
}
