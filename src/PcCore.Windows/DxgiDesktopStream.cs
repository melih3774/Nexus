using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;
using PcCore.Windows.Interop;

namespace PcCore.Windows;

/// <summary>
/// Live desktop capture through DXGI Desktop Duplication.
/// <para>
/// This is the only place in Nexus that talks to Direct3D, and it exists because the alternative —
/// calling the screenshot path fifteen times a second — is a different thing wearing a stream's
/// clothes. A GDI <c>BitBlt</c> of a 1920x1080 desktop costs tens of milliseconds and the whole
/// frame every time; Desktop Duplication hands over the frame the compositor already produced, only
/// when it actually changed, which is what makes a sustained stream affordable at all.
/// </para>
/// <para>
/// It is also, correspondingly, the part with the most ways to fail: resolution changes, display
/// recreation, driver resets, the lock screen, UAC prompts, and protected content all interrupt a
/// duplication that was working a moment ago. None of them are errors here. Each maps to
/// <see cref="CaptureStatus.Lost"/> or <see cref="CaptureStatus.Unavailable"/> and the stream
/// reopens, because a desktop that briefly could not be read is not a reason to take PC Core down.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DxgiDesktopStream : IDesktopStream
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DxgiDesktopStream> _logger;

    public DxgiDesktopStream(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DxgiDesktopStream>();
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        var factory = IntPtr.Zero;

        try
        {
            var iid = DxgiInterop.IidFactory1;
            if (DxgiInterop.CreateDXGIFactory1(ref iid, out factory) < 0)
            {
                _logger.LogWarning("DXGI is unavailable; no displays can be streamed.");
                return displays;
            }

            for (var adapterIndex = 0u; ; adapterIndex++)
            {
                if (DxgiInterop.EnumAdapters1(factory, adapterIndex, out var adapter) < 0)
                {
                    break;
                }

                try
                {
                    for (var outputIndex = 0u; ; outputIndex++)
                    {
                        if (DxgiInterop.EnumOutputs(adapter, outputIndex, out var output) < 0)
                        {
                            break;
                        }

                        try
                        {
                            if (DxgiInterop.GetOutputDesc(output, out var desc) < 0 || desc.AttachedToDesktop == 0)
                            {
                                continue;
                            }

                            displays.Add(new DisplayInfo(
                                displays.Count,
                                desc.Name,
                                desc.Left,
                                desc.Top,
                                desc.Right - desc.Left,
                                desc.Bottom - desc.Top,
                                // Windows anchors the primary display at the virtual-desktop origin.
                                desc.Left == 0 && desc.Top == 0));
                        }
                        finally
                        {
                            DxgiInterop.Release(ref output);
                        }
                    }
                }
                finally
                {
                    DxgiInterop.Release(ref adapter);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "Direct3D is not present; live streaming is unavailable.");
        }
        finally
        {
            DxgiInterop.Release(ref factory);
        }

        return displays;
    }

    public IDesktopCaptureSource Open(int displayIndex)
    {
        var factory = IntPtr.Zero;
        var adapter = IntPtr.Zero;
        var output = IntPtr.Zero;

        try
        {
            var iid = DxgiInterop.IidFactory1;
            if (DxgiInterop.CreateDXGIFactory1(ref iid, out factory) < 0)
            {
                throw new CapabilityException(ErrorCodes.Unsupported, "DXGI is not available on this machine.");
            }

            DisplayInfo? display = null;
            var seen = 0;

            for (var adapterIndex = 0u; display is null; adapterIndex++)
            {
                if (DxgiInterop.EnumAdapters1(factory, adapterIndex, out var candidateAdapter) < 0)
                {
                    break;
                }

                for (var outputIndex = 0u; ; outputIndex++)
                {
                    if (DxgiInterop.EnumOutputs(candidateAdapter, outputIndex, out var candidateOutput) < 0)
                    {
                        break;
                    }

                    if (DxgiInterop.GetOutputDesc(candidateOutput, out var desc) < 0 || desc.AttachedToDesktop == 0)
                    {
                        DxgiInterop.Release(ref candidateOutput);
                        continue;
                    }

                    if (seen++ == displayIndex)
                    {
                        adapter = candidateAdapter;
                        output = candidateOutput;
                        candidateAdapter = IntPtr.Zero;

                        display = new DisplayInfo(
                            displayIndex,
                            desc.Name,
                            desc.Left,
                            desc.Top,
                            desc.Right - desc.Left,
                            desc.Bottom - desc.Top,
                            desc.Left == 0 && desc.Top == 0);

                        break;
                    }

                    DxgiInterop.Release(ref candidateOutput);
                }

                DxgiInterop.Release(ref candidateAdapter);
            }

            if (display is null)
            {
                throw new CapabilityException(ErrorCodes.InvalidParameters, "No such display.");
            }

            var source = new DxgiCaptureSource(
                adapter,
                output,
                display,
                _loggerFactory.CreateLogger<DxgiCaptureSource>());

            // Ownership moved into the source.
            adapter = IntPtr.Zero;
            output = IntPtr.Zero;

            return source;
        }
        finally
        {
            DxgiInterop.Release(ref output);
            DxgiInterop.Release(ref adapter);
            DxgiInterop.Release(ref factory);
        }
    }
}

/// <summary>One duplicated display, held open for the life of a stream.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DxgiCaptureSource : IDesktopCaptureSource
{
    private readonly ILogger<DxgiCaptureSource> _logger;

    private IntPtr _adapter;
    private IntPtr _output;
    private IntPtr _output1;
    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _duplication;
    private IntPtr _staging;

    private byte[] _pixels = [];
    private int _stride;
    private bool _delivered;

    public DxgiCaptureSource(IntPtr adapter, IntPtr output, DisplayInfo display, ILogger<DxgiCaptureSource> logger)
    {
        _adapter = adapter;
        _output = output;
        _logger = logger;
        Display = display;

        try
        {
            Initialise();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public DisplayInfo Display { get; }

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    private void Initialise()
    {
        // Feature level 11_0 down to 10_0: Desktop Duplication needs none of the higher tiers, and
        // accepting older hardware costs nothing.
        int[] featureLevels = [0xb000, 0xa100, 0xa000];

        var hr = DxgiInterop.D3D11CreateDevice(
            _adapter,
            DxgiInterop.D3DDriverTypeUnknown,
            IntPtr.Zero,
            0,
            featureLevels,
            (uint)featureLevels.Length,
            DxgiInterop.D3D11SdkVersion,
            out _device,
            out _,
            out _context);

        if (hr < 0)
        {
            throw new CapabilityException(
                ErrorCodes.Unsupported, "A Direct3D device could not be created for screen capture.");
        }

        var output1Iid = DxgiInterop.IidOutput1;
        if (DxgiInterop.QueryInterface(_output, ref output1Iid, out _output1) < 0)
        {
            throw new CapabilityException(ErrorCodes.Unsupported, "This display does not support duplication.");
        }

        hr = DxgiInterop.DuplicateOutput(_output1, _device, out _duplication);

        if (hr < 0)
        {
            throw new CapabilityException(
                ErrorCodes.Unsupported,
                hr == DxgiInterop.DxgiErrorNotCurrentlyAvailable
                    ? "The display is already being duplicated by another application."
                    : "The display could not be duplicated.");
        }

        DxgiInterop.GetDuplicationDesc(_duplication, out var desc);

        PixelWidth = (int)desc.Width;
        PixelHeight = (int)desc.Height;

        CreateStaging();

        _logger.LogInformation(
            "Duplicating {Display}: {Width}x{Height} physical pixels.", Display.Name, PixelWidth, PixelHeight);
    }

    private void CreateStaging()
    {
        var description = new D3D11Texture2DDesc
        {
            Width = (uint)PixelWidth,
            Height = (uint)PixelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiInterop.DxgiFormatB8G8R8A8Unorm,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = DxgiInterop.D3D11UsageStaging,
            BindFlags = 0,
            CpuAccessFlags = DxgiInterop.D3D11CpuAccessRead,
            MiscFlags = 0,
        };

        if (DxgiInterop.CreateTexture2D(_device, ref description, out _staging) < 0)
        {
            throw new CapabilityException(ErrorCodes.Unsupported, "A staging texture could not be created.");
        }

        _stride = PixelWidth * 4;
        _pixels = new byte[(long)_stride * PixelHeight <= int.MaxValue ? _stride * PixelHeight : 0];

        if (_pixels.Length == 0)
        {
            throw new CapabilityException(ErrorCodes.Unsupported, "The display is too large to capture.");
        }
    }

    public CaptureResult TryGetFrame(TimeSpan timeout)
    {
        if (_duplication == IntPtr.Zero)
        {
            return CaptureResult.Lost("capture is not open");
        }

        var timeoutMs = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, 5000);
        var hr = DxgiInterop.AcquireNextFrame(_duplication, timeoutMs, out var info, out var resource);

        if (hr == DxgiInterop.DxgiErrorWaitTimeout)
        {
            // Nothing changed. On a still desktop that is the normal answer — except right at the
            // start, when the client has nothing at all to show. Priming the first frame from a
            // one-shot GDI grab avoids a black screen that only clears when something happens to
            // move; after that, silence correctly means "your last frame is still correct".
            return _delivered ? CaptureResult.TimedOut : PrimeFirstFrame();
        }

        if (hr < 0)
        {
            return Classify(hr);
        }

        try
        {
            if (info.AccumulatedFrames == 0 && _delivered)
            {
                // Pointer-only update: the desktop image is unchanged and the texture holds nothing
                // worth encoding.
                return CaptureResult.TimedOut;
            }

            if (info.AccumulatedFrames == 0 && !_delivered)
            {
                return PrimeFirstFrame();
            }

            var textureIid = DxgiInterop.IidTexture2D;
            if (DxgiInterop.QueryInterface(resource, ref textureIid, out var texture) < 0)
            {
                return CaptureResult.Lost("the acquired frame was not a texture");
            }

            try
            {
                DxgiInterop.CopyResource(_context, _staging, texture);

                if (DxgiInterop.Map(_context, _staging, out var mapped) < 0)
                {
                    return CaptureResult.Lost("the captured frame could not be read");
                }

                try
                {
                    CopyRows(mapped);
                }
                finally
                {
                    DxgiInterop.Unmap(_context, _staging);
                }
            }
            finally
            {
                DxgiInterop.Release(ref texture);
            }

            if (info.ProtectedContentMaskedOut != 0)
            {
                // The frame is real; parts of it are black because Windows refuses to hand over
                // protected content. Worth saying once, not once a frame.
                _logger.LogDebug("Protected content was masked out of a captured frame.");
            }

            _delivered = true;
            return CaptureResult.Captured(BuildFrame());
        }
        finally
        {
            DxgiInterop.Release(ref resource);
            DxgiInterop.ReleaseFrame(_duplication);
        }
    }

    /// <summary>
    /// Copies the mapped rows out, honouring the row pitch, which is not always the row width.
    /// </summary>
    private void CopyRows(D3D11MappedSubresource mapped)
    {
        var pitch = (int)mapped.RowPitch;

        for (var y = 0; y < PixelHeight; y++)
        {
            Marshal.Copy(mapped.Data + (y * pitch), _pixels, y * _stride, _stride);
        }
    }

    /// <summary>
    /// A single GDI grab, used only to give the client something to look at before the desktop first
    /// changes. Not a fallback stream: if this were called every frame it would be exactly the
    /// design this class exists to avoid.
    /// </summary>
    private CaptureResult PrimeFirstFrame()
    {
        try
        {
            using var bitmap = new Bitmap(PixelWidth, PixelHeight, PixelFormat.Format32bppArgb);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(Display.Left, Display.Top, 0, 0, new Size(PixelWidth, PixelHeight));
            }

            var data = bitmap.LockBits(
                new Rectangle(0, 0, PixelWidth, PixelHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                for (var y = 0; y < PixelHeight; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * data.Stride), _pixels, y * _stride, _stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            _delivered = true;
            return CaptureResult.Captured(BuildFrame());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ExternalException)
        {
            return CaptureResult.Unavailable("the desktop could not be read for the first frame");
        }
    }

    /// <summary>
    /// Wraps the pixel buffer, drawing the real cursor into it.
    /// <para>
    /// Desktop Duplication does not include the pointer in the frame; it reports the shape
    /// separately. Drawing the actual cursor here rather than sending coordinates for the phone to
    /// draw its own means the owner sees the I-beam, the resize arrows and the busy spinner they
    /// would see sitting at the machine — and it makes double-rendering impossible, because the
    /// frame's flags tell the client the pointer is already in the image.
    /// </para>
    /// </summary>
    private RawFrame BuildFrame()
    {
        var cursorX = 0;
        var cursorY = 0;
        var cursorVisible = false;

        try
        {
            var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };

            if (InputInterop.GetCursorInfo(ref info) && info.Flags == InputInterop.CursorShowing
                && info.Cursor != IntPtr.Zero)
            {
                // The duplication surface can be larger than the display rectangle when Windows is
                // scaling; map through the ratio rather than assuming they match.
                var scaleX = Display.Width == 0 ? 1.0 : (double)PixelWidth / Display.Width;
                var scaleY = Display.Height == 0 ? 1.0 : (double)PixelHeight / Display.Height;

                var localX = (int)Math.Round((info.ScreenX - Display.Left) * scaleX);
                var localY = (int)Math.Round((info.ScreenY - Display.Top) * scaleY);

                if (localX >= 0 && localY >= 0 && localX < PixelWidth && localY < PixelHeight)
                {
                    cursorX = localX;
                    cursorY = localY;
                    cursorVisible = true;
                    DrawCursor(info.Cursor, localX, localY);
                }
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // A frame without a pointer drawn on it is still a usable frame.
            _logger.LogDebug(ex, "The cursor could not be drawn into a captured frame.");
        }

        return new RawFrame(
            _pixels,
            PixelWidth,
            PixelHeight,
            _stride,
            DateTimeOffset.UtcNow,
            cursorX,
            cursorY,
            cursorVisible);
    }

    private void DrawCursor(IntPtr cursor, int x, int y)
    {
        if (!InputInterop.GetIconInfo(cursor, out var icon))
        {
            return;
        }

        try
        {
            var handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);

            try
            {
                using var bitmap = new Bitmap(
                    PixelWidth, PixelHeight, _stride, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());

                using var graphics = Graphics.FromImage(bitmap);
                var hdc = graphics.GetHdc();

                try
                {
                    // The hotspot is where the cursor points, not where its image starts.
                    InputInterop.DrawIconEx(
                        hdc,
                        x - icon.HotspotX,
                        y - icon.HotspotY,
                        cursor,
                        0,
                        0,
                        0,
                        IntPtr.Zero,
                        InputInterop.DiNormal);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            InputInterop.DeleteObject(icon.MaskBitmap);
            InputInterop.DeleteObject(icon.ColorBitmap);
        }
    }

    private CaptureResult Classify(int hr) => hr switch
    {
        DxgiInterop.DxgiErrorAccessLost or DxgiInterop.DxgiErrorInvalidCall =>
            CaptureResult.Lost("the display mode or desktop changed"),

        DxgiInterop.DxgiErrorSessionDisconnected =>
            CaptureResult.Unavailable("the session is disconnected or on a secure desktop"),

        DxgiInterop.DxgiErrorAccessDenied =>
            CaptureResult.Unavailable("Windows denied access to the desktop, most likely a secure desktop"),

        DxgiInterop.DxgiErrorNotCurrentlyAvailable =>
            CaptureResult.Unavailable("the desktop is not available for duplication right now"),

        DxgiInterop.DxgiErrorUnsupported =>
            CaptureResult.Unavailable("this display cannot be duplicated"),

        _ => CaptureResult.Lost($"capture failed (0x{hr:x8})"),
    };

    public void Dispose()
    {
        DxgiInterop.Release(ref _staging);
        DxgiInterop.Release(ref _duplication);
        DxgiInterop.Release(ref _output1);
        DxgiInterop.Release(ref _context);
        DxgiInterop.Release(ref _device);
        DxgiInterop.Release(ref _output);
        DxgiInterop.Release(ref _adapter);

        _pixels = [];
    }
}
