using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PcAssistant.Windows.Capture;

/// <summary>
/// Windows Graphics Capture, the only mechanism on Windows that hands back a specific
/// window's own pixels rather than whatever is on screen where that window happens to be.
///
/// That distinction is the whole point: DWM keeps a composed surface per window, so a window
/// sitting behind three others still has current content, and this reads it without touching
/// the owner's desktop. Nothing here activates, raises, restores or focuses anything.
///
/// Measured on this machine: a WGC frame covers <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>, not
/// <c>GetWindowRect</c> — for a Notepad window those were 1111x806 at (132,125) and 1125x813
/// at (125,125) respectively. Coordinate translation depends on that, so the geometry a
/// capture reports is always the frame bounds it actually rendered.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class GraphicsCaptureEngine
{
    private static readonly object DeviceLock = new();
    private static IDirect3DDevice? device;

    /// <summary>False on Windows before 1903, or where the capture service is unavailable.</summary>
    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Captures one frame of <paramref name="hwnd"/>. <paramref name="maxEdge"/> bounds the
    /// longest side of the returned image; the frame is scaled by the encoder rather than
    /// cropped, so the image still covers the whole window.
    /// </summary>
    /// <returns>PNG bytes and the pixel size actually produced.</returns>
    public static (byte[] Png, int Width, int Height) Capture(
        nint hwnd, int maxEdge, bool includeCursor, CancellationToken cancellationToken)
    {
        var item = CreateItemForWindow(hwnd);
        var size = item.Size;
        if (size.Width < 1 || size.Height < 1)
            throw new InvalidOperationException("The window reports no capturable surface.");

        var d3d = SharedDevice();
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        Direct3D11CaptureFrame? frame = null;
        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                d3d, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            session = pool.CreateCaptureSession(item);

            // Neither of these exists on every supported build, and neither is worth failing a
            // capture over: a cursor or a highlight border is cosmetic, the pixels are not.
            TrySet(() => session.IsCursorCaptureEnabled = includeCursor);
            TrySet(() => session.IsBorderRequired = false);

            session.StartCapture();

            // A window that is not being redrawn still produces its first frame promptly; this
            // budget is for a busy compositor, not for waiting on the application.
            var deadline = Environment.TickCount64 + 1500;
            while (frame is null && Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                frame = pool.TryGetNextFrame();
                if (frame is null) Thread.Sleep(10);
            }

            if (frame is null)
                throw new InvalidOperationException(
                    "Windows Graphics Capture produced no frame for this window within the wait. " +
                    "A minimized, cloaked or capture-protected window behaves this way.");

            using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)
                .AsTask(cancellationToken).GetAwaiter().GetResult();

            return Encode(bitmap, maxEdge, cancellationToken);
        }
        finally
        {
            frame?.Dispose();
            session?.Dispose();
            pool?.Dispose();
        }
    }

    /// <summary>PNG-encodes a captured frame, scaling it down only when it exceeds the budget.</summary>
    private static (byte[] Png, int Width, int Height) Encode(
        SoftwareBitmap bitmap, int maxEdge, CancellationToken cancellationToken)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var longest = Math.Max(width, height);
        if (maxEdge > 0 && longest > maxEdge)
        {
            var scale = (double)maxEdge / longest;
            width = Math.Max(1, (int)Math.Round(width * scale));
            height = Math.Max(1, (int)Math.Round(height * scale));
        }

        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask(cancellationToken).GetAwaiter().GetResult();

        // The frame pool hands back premultiplied BGRA; the PNG encoder wants exactly that,
        // and Convert is a no-op when the frame already matches.
        using var source = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        encoder.SetSoftwareBitmap(source);
        if (width != bitmap.PixelWidth || height != bitmap.PixelHeight)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)width;
            encoder.BitmapTransform.ScaledHeight = (uint)height;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        encoder.FlushAsync().AsTask(cancellationToken).GetAwaiter().GetResult();

        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken).GetAwaiter().GetResult();
            reader.ReadBytes(bytes);
        }
        return (bytes, width, height);
    }

    private static void TrySet(Action set)
    {
        try { set(); }
        catch (Exception) { /* Older build, or a policy that forbids it. Cosmetic either way. */ }
    }

    /// <summary>
    /// One D3D device for the process. Recreated if it is ever lost, because a stale device
    /// makes every later capture fail for a reason that has nothing to do with the window.
    /// </summary>
    private static IDirect3DDevice SharedDevice()
    {
        lock (DeviceLock)
        {
            if (device is not null) return device;

            var hr = D3D11CreateDevice(0, DriverTypeHardware, 0, BgraSupport, 0, 0, D3D11SdkVersion,
                out var d3dDevice, out _, out var context);
            if (hr < 0)
            {
                // No hardware device (remote session, no GPU, driver reset): WARP still composes.
                hr = D3D11CreateDevice(0, DriverTypeWarp, 0, BgraSupport, 0, 0, D3D11SdkVersion,
                    out d3dDevice, out _, out context);
            }
            if (hr < 0) throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("No Direct3D device.");

            try
            {
                var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in dxgiIid, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    try { return device = WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable); }
                    finally { Marshal.Release(inspectable); }
                }
                finally { Marshal.Release(dxgi); }
            }
            finally
            {
                if (context != 0) Marshal.Release(context);
                if (d3dDevice != 0) Marshal.Release(d3dDevice);
            }
        }
    }

    /// <summary>Drops the cached device so the next capture builds a fresh one.</summary>
    public static void ResetDevice()
    {
        lock (DeviceLock)
        {
            (device as IDisposable)?.Dispose();
            device = null;
        }
    }

    /// <summary>
    /// GraphicsCaptureItem has no public constructor for an HWND; the documented route is the
    /// activation factory's <c>IGraphicsCaptureItemInterop</c>. Reached through
    /// <c>RoGetActivationFactory</c> directly so this does not depend on CsWinRT internals.
    /// </summary>
    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string RuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

        Marshal.ThrowExceptionForHR(WindowsCreateString(RuntimeClass, RuntimeClass.Length, out var name));
        nint factory;
        try { Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in interopIid, out factory)); }
        finally { WindowsDeleteString(name); }

        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            var pointer = interop.CreateForWindow(hwnd, ref itemIid);
            try { return GraphicsCaptureItem.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
        }
        finally { Marshal.Release(factory); }
    }

    private const uint DriverTypeHardware = 1;
    private const uint DriverTypeWarp = 5;
    private const uint BgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }
}
