using System.Runtime.InteropServices;
using System.Windows.Media;
using Windows.UI.Composition;

namespace alpha_island.Interop;

/// <summary>Direct2D geometry only; shares a vector mask with DWM without allocating capture textures.</summary>
internal sealed class CompositionContour : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private readonly struct FloatPoint
    { public readonly float X, Y; public FloatPoint(double x, double y) { X = (float)x; Y = (float)y; } }
    [DllImport("d2d1.dll", ExactSpelling = true)] private static extern int D2D1CreateFactory(uint type, in Guid iid, IntPtr options, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreatePath(IntPtr self, out IntPtr path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenSink(IntPtr self, out IntPtr sink);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetMode(IntPtr self, uint mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BeginFigure(IntPtr self, FloatPoint start, uint begin);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void AddLines(IntPtr self, [In] FloatPoint[] points, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void EndFigure(IntPtr self, uint end);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CloseSink(IntPtr self);
    private IntPtr _factory;
    private readonly CreatePath _create;
    private static T Function<T>(IntPtr self, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));

    public CompositionContour()
    {
        Marshal.ThrowExceptionForHR(D2D1CreateFactory(1, new Guid("06152247-6F50-465A-9245-118BFD3B6007"), IntPtr.Zero, out _factory));
        _create = Function<CreatePath>(_factory, 10);
    }

    public CompositionPath Create(Geometry outline)
    {
        if (_factory == IntPtr.Zero) throw new ObjectDisposedException(nameof(CompositionContour));
        Marshal.ThrowExceptionForHR(_create(_factory, out IntPtr path));
        IntPtr sink = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Function<OpenSink>(path, 17)(path, out sink));
            Function<SetMode>(sink, 3)(sink, 1); // winding fill: overlapping island/bridge figures form a union.
            var begin = Function<BeginFigure>(sink, 5); var lines = Function<AddLines>(sink, 6);
            var end = Function<EndFigure>(sink, 8);
            var flattened = outline.GetFlattenedPathGeometry(.22, ToleranceType.Absolute);
            var transform = flattened.Transform?.Value ?? Matrix.Identity;
            foreach (var figure in flattened.Figures)
            {
                var start = transform.Transform(figure.StartPoint);
                begin(sink, new FloatPoint(start.X, start.Y), 0);
                foreach (var segment in figure.Segments)
                {
                    if (segment is PolyLineSegment poly)
                    {
                        var points = poly.Points.Select(point => transform.Transform(point)).Select(point => new FloatPoint(point.X, point.Y)).ToArray();
                        if (points.Length > 0) lines(sink, points, (uint)points.Length);
                    }
                    else if (segment is LineSegment line)
                    { var point = transform.Transform(line.Point); lines(sink, new[] { new FloatPoint(point.X, point.Y) }, 1); }
                }
                end(sink, figure.IsClosed ? 1u : 0u);
            }
            Marshal.ThrowExceptionForHR(Function<CloseSink>(sink, 9)(sink));
            var source = new Direct2DContourSource(path); path = IntPtr.Zero; // source now owns this reference.
            IntPtr unknown = Marshal.GetIUnknownForObject(source);
            try { return new CompositionPath(WinRT.MarshalInspectable<Windows.Graphics.IGeometrySource2D>.FromAbi(unknown)); }
            finally { Marshal.Release(unknown); GC.KeepAlive(source); }
        }
        finally { if (sink != IntPtr.Zero) Marshal.Release(sink); if (path != IntPtr.Zero) Marshal.Release(path); }
    }

    public void Dispose() { var factory = Interlocked.Exchange(ref _factory, IntPtr.Zero); if (factory != IntPtr.Zero) Marshal.Release(factory); }
}

// IGeometrySource2D has no members beyond IInspectable. Explicit ABI declarations avoid
// mixing the CLR CCW with a second C#/WinRT CCW for the same managed source object.
[ComVisible(true), Guid("CAFF7902-670C-4181-A624-DA977203B845"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IContourGeometrySourceAbi
{
    [PreserveSig] int GetIids(out uint count, out IntPtr ids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr name);
    [PreserveSig] int GetTrustLevel(out int trust);
}
[ComVisible(true), Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IContourInspectableAbi
{
    [PreserveSig] int GetIids(out uint count, out IntPtr ids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr name);
    [PreserveSig] int GetTrustLevel(out int trust);
}
[ComVisible(true), Guid("0657AF73-53FD-47CF-84FF-C8492D2A80A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IContourGeometryInteropAbi
{
    [PreserveSig] int GetGeometry(out IntPtr geometry);
    [PreserveSig] int TryGetGeometryUsingFactory(IntPtr factory, out IntPtr geometry);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class Direct2DContourSource : IContourGeometrySourceAbi, IContourInspectableAbi, IContourGeometryInteropAbi
{
    private readonly IntPtr _geometry;
    internal Direct2DContourSource(IntPtr geometry) { _geometry = geometry; GC.AddMemoryPressure(1024); }
    ~Direct2DContourSource() { if (_geometry != IntPtr.Zero) Marshal.Release(_geometry); GC.RemoveMemoryPressure(1024); }
    public int GetIids(out uint count, out IntPtr ids)
    {
        count = 1; ids = Marshal.AllocCoTaskMem(16);
        Marshal.StructureToPtr(new Guid("CAFF7902-670C-4181-A624-DA977203B845"), ids, false); return 0;
    }
    public int GetRuntimeClassName(out IntPtr name) { name = IntPtr.Zero; return 0; }
    public int GetTrustLevel(out int trust) { trust = 0; return 0; }
    public int GetGeometry(out IntPtr geometry) { geometry = _geometry; Marshal.AddRef(geometry); return 0; }
    public int TryGetGeometryUsingFactory(IntPtr factory, out IntPtr geometry) { geometry = IntPtr.Zero; return unchecked((int)0x80004001); }
}
