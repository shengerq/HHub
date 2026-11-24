using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Hhub.Desktop;

// Explorer offers local files as CF_HDROP. Register the top-level HWND so that
// drops can reach the installer even when XAML does not route the drag events.
internal sealed class ExplorerPluginDrop : IDisposable
{
    private readonly nint _window;
    private readonly DropTarget _target;

    private ExplorerPluginDrop(nint window, DropTarget target) => (_window, _target) = (window, target);

    public static ExplorerPluginDrop? TryRegister(nint window, Func<string[], bool> receive)
    {
        if (OleInitialize(0) < 0) return null;
        var target = new DropTarget(receive);
        int result = RegisterDragDrop(window, target);
        if (result >= 0) return new(window, target);
        System.Diagnostics.Trace.WriteLine($"Explorer drop registration: 0x{result:X8}");
        OleUninitialize();
        return null;
    }

    public void Dispose()
    {
        RevokeDragDrop(_window);
        GC.KeepAlive(_target);
        OleUninitialize();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    [ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        [PreserveSig] int DragEnter(IDataObject data, uint keys, Point point, ref uint effects);
        [PreserveSig] int DragOver(uint keys, Point point, ref uint effects);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IDataObject data, uint keys, Point point, ref uint effects);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropTarget(Func<string[], bool> receive) : IDropTarget
    {
        private const uint Copy = 1;
        private bool _files;
        private static FORMATETC FileFormat() => new() { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1, tymed = TYMED.TYMED_HGLOBAL };

        public int DragEnter(IDataObject data, uint keys, Point point, ref uint effects)
        {
            var format = FileFormat();
            _files = data.QueryGetData(ref format) == 0;
            return DragOver(keys, point, ref effects);
        }

        public int DragOver(uint keys, Point point, ref uint effects)
        {
            effects = _files ? effects & Copy : 0;
            return 0;
        }

        public int DragLeave() { _files = false; return 0; }

        public int Drop(IDataObject data, uint keys, Point point, ref uint effects)
        {
            bool accepted = _files && (effects & Copy) != 0;
            _files = false;
            effects = 0;
            if (!accepted) return 0;
            try
            {
                var format = FileFormat();
                data.GetData(ref format, out var medium);
                try
                {
                    if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == 0) return 0;
                    var paths = new List<string>();
                    uint count = Math.Min(DragQueryFile(medium.unionmember, uint.MaxValue, null, 0), 512);
                    for (uint index = 0; index < count && paths.Count < 16; index++)
                    {
                        uint length = DragQueryFile(medium.unionmember, index, null, 0);
                        if (length == 0 || length > 32767) continue;
                        var path = new StringBuilder((int)length + 1);
                        if (DragQueryFile(medium.unionmember, index, path, length + 1) != length) continue;
                        string file = path.ToString();
                        if (Path.GetExtension(file).Equals(".hhub", StringComparison.OrdinalIgnoreCase)) paths.Add(file);
                    }
                    if (receive(paths.ToArray())) effects = Copy;
                }
                finally { ReleaseStgMedium(ref medium); }
            }
            catch (Exception error) when (error is COMException or ArgumentException)
            { System.Diagnostics.Trace.WriteLine(error); }
            return 0;
        }
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint window, [MarshalAs(UnmanagedType.Interface)] IDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint window);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint drop, uint index, StringBuilder? path, uint capacity);
}
