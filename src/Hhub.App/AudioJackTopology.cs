using System.Runtime.InteropServices;

namespace Hhub.App;

// Read the physical connector through DeviceTopology. The NAudio 2.2.1 jack
// wrapper marshals GetJackDescription as a string, but Windows returns a struct.
internal static class AudioJackTopology
{
    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private const uint InProcessServer = 1;

    public static (string? Controller, bool Has35mmJack) Read(string endpointId, bool readJack)
    {
        object? enumerator = null, device = null, topology = null, connector = null, hardware = null, jack = null;
        string? controller = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, throwOnError: true)!);
            if (((DeviceEnumerator)enumerator!).GetDevice(endpointId, out device) < 0
                || ((EndpointDevice)device).Activate(typeof(DeviceTopology).GUID, InProcessServer, 0, out topology) < 0
                || ((DeviceTopology)topology).GetConnector(0, out connector) < 0) return default;

            var endpoint = (TopologyConnector)connector;
            if (endpoint.GetDeviceIdConnectedTo(out var connectedId) >= 0) controller = connectedId;
            if (!readJack || endpoint.GetConnectedTo(out hardware) < 0 || hardware is not TopologyPart part
                || part.Activate(InProcessServer, typeof(JackDescription).GUID, out jack) < 0
                || ((JackDescription)jack).GetJackCount(out uint count) < 0) return (controller, false);

            var description = (JackDescription)jack;
            for (uint index = 0; index < count; index++)
                if (description.GetJackDescription(index, out var info) >= 0
                    && info.ConnectionType == JackConnection.ThreePointFiveMm
                    && info.PortConnection is JackPort.Jack or JackPort.IntegratedAndJack)
                    return (controller, true);
            return (controller, false);
        }
        catch (COMException) { return (controller, false); }
        finally
        {
            foreach (var item in new[] { jack, hardware, connector, topology, device, enumerator })
                if (item is not null) Marshal.ReleaseComObject(item);
        }
    }

    private enum JackConnection : uint { ThreePointFiveMm = 1 }
    private enum JackPort : uint { Jack = 0, IntegratedAndJack = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct JackInfo
    {
        public uint ChannelMapping, Color;
        public JackConnection ConnectionType;
        public uint GeoLocation, GenLocation;
        public JackPort PortConnection;
        public int IsConnected;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, [MarshalAs(UnmanagedType.Interface)] out object devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, [MarshalAs(UnmanagedType.Interface)] out object device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out object device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface EndpointDevice
    {
        [PreserveSig] int Activate(in Guid iid, uint clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, [MarshalAs(UnmanagedType.Interface)] out object connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TopologyConnector
    {
        [PreserveSig] int GetType(out uint type);
        [PreserveSig] int GetDataFlow(out uint flow);
        [PreserveSig] int ConnectTo(nint connector);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out int connected);
        [PreserveSig] int GetConnectedTo([MarshalAs(UnmanagedType.Interface)] out object connector);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TopologyPart
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetPartType(out uint type);
        [PreserveSig] int GetSubType(out Guid type);
        [PreserveSig] int GetControlInterfaceCount(out uint count);
        [PreserveSig] int GetControlInterface(uint index, out nint control);
        [PreserveSig] int EnumPartsIncoming(out nint parts);
        [PreserveSig] int EnumPartsOutgoing(out nint parts);
        [PreserveSig] int GetTopologyObject(out nint topology);
        [PreserveSig] int Activate(uint clsCtx, in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("4509F757-2D46-4637-8E62-CE7DB944F57B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface JackDescription
    {
        [PreserveSig] int GetJackCount(out uint count);
        [PreserveSig] int GetJackDescription(uint index, out JackInfo description);
    }
}
