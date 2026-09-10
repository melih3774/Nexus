using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;
using PcCore.Windows.Capture;
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

            // Ownership moves before the call, not after it, and this is the fix for a crash rather
            // than a tidy-up.
            //
            // The constructor releases everything it was given if it throws — and it throws on every
            // ordinary Secure Desktop: DuplicateOutput answers DXGI_ERROR_ACCESS_DENIED while a UAC
            // prompt is up. Transferring ownership only on the success path meant the finally below
            // released the same adapter and output a second time, taking the reference count past
            // zero. That is an access violation inside DXGI, and 0xC0000005 does not unwind — it
            // ends the process. PC Core did not exit when UAC appeared; it was killed by its own
            // cleanup, one second later, when the capture loop tried to reopen.
            var ownedAdapter = adapter;
            var ownedOutput = output;
            adapter = IntPtr.Zero;
            output = IntPtr.Zero;

            return new DxgiCaptureSource(
                ownedAdapter,
                ownedOutput,
                display,
                _loggerFactory.CreateLogger<DxgiCaptureSource>());
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

    /// <summary>The desktop as Windows produced it, with no pointer on it.</summary>
    private byte[] _desktop = [];

    /// <summary>What is sent: a copy of the desktop with the pointer composited in.</summary>
    private byte[] _composed = [];

    private byte[] _shapeBuffer = [];

    private int _stride;
    private bool _delivered;

    private PointerShape? _pointer;
    private int _pointerX;
    private int _pointerY;
    private bool _pointerVisible;
    private bool _pointerChanged;

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
                hr switch
                {
                    DxgiInterop.DxgiErrorNotCurrentlyAvailable =>
                        "The display is already being duplicated by another application.",
                    DxgiInterop.DxgiErrorAccessDenied =>
                        "Windows denied access to the desktop, most likely a secure desktop.",
                    _ => "The display could not be duplicated.",
                });
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

        var bytes = (long)_stride * PixelHeight;

        if (bytes <= 0 || bytes > int.MaxValue / 2)
        {
            throw new CapabilityException(ErrorCodes.Unsupported, "The display is too large to capture.");
        }

        _desktop = new byte[bytes];
        _composed = new byte[bytes];
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
            // Nothing changed at all. On a still desktop that is the normal answer — except right at
            // the start, when the client has nothing to show. Priming the first frame from a
            // one-shot GDI grab avoids a black screen that only clears when something moves; after
            // that, silence correctly means "your last frame is still correct".
            return _delivered ? CaptureResult.TimedOut : PrimeFirstFrame();
        }

        if (hr < 0)
        {
            return Classify(hr);
        }

        try
        {
            UpdatePointer(info);

            var desktopChanged = info.AccumulatedFrames > 0;

            if (desktopChanged)
            {
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
            }
            else if (!_delivered)
            {
                return PrimeFirstFrame();
            }
            else if (!_pointerChanged)
            {
                // Neither the desktop nor the pointer moved. Nothing to send.
                return CaptureResult.TimedOut;
            }

            // A pointer-only update is a frame worth sending, and used not to be. The pointer is
            // baked into the image, so "the mouse moved and nothing else did" is a visible change to
            // what the phone is looking at — and on a still desktop it is the only change there is.
            _pointerChanged = false;
            _delivered = true;

            return CaptureResult.Captured(Compose());
        }
        finally
        {
            DxgiInterop.Release(ref resource);
            DxgiInterop.ReleaseFrame(_duplication);
        }
    }

    /// <summary>
    /// Takes whatever pointer news arrived with this frame.
    /// <para>
    /// Position and visibility come on every acquire that saw a mouse update; the shape only when it
    /// actually changed, which is why it is cached. Moving the mouse across a text box changes the
    /// shape once and the position sixty times.
    /// </para>
    /// </summary>
    private void UpdatePointer(DxgiOutduplFrameInfo info)
    {
        if (info.LastMouseUpdateTime != 0)
        {
            var visible = info.PointerVisible != 0;

            if (visible != _pointerVisible || info.PointerX != _pointerX || info.PointerY != _pointerY)
            {
                _pointerChanged = true;
            }

            _pointerVisible = visible;
            _pointerX = info.PointerX;
            _pointerY = info.PointerY;
        }

        if (info.PointerShapeBufferSize == 0)
        {
            return;
        }

        if (_shapeBuffer.Length < info.PointerShapeBufferSize)
        {
            _shapeBuffer = new byte[info.PointerShapeBufferSize];
        }

        var hr = DxgiInterop.GetFramePointerShape(
            _duplication,
            (uint)_shapeBuffer.Length,
            _shapeBuffer,
            out var required,
            out var shapeInfo);

        if (hr < 0)
        {
            _logger.LogDebug("The pointer shape could not be read (0x{Hr:x8}, {Required} bytes).", hr, required);
            return;
        }

        var declared = (long)shapeInfo.Pitch * shapeInfo.Height;

        if (declared <= 0 || declared > _shapeBuffer.Length)
        {
            _logger.LogDebug(
                "Ignoring a pointer shape claiming {Declared} bytes from a {Actual}-byte buffer.",
                declared,
                _shapeBuffer.Length);
            return;
        }

        var bytes = new byte[declared];
        Array.Copy(_shapeBuffer, bytes, bytes.Length);

        var shape = new PointerShape(
            (PointerShapeType)shapeInfo.Type,
            (int)shapeInfo.Width,
            (int)shapeInfo.Height,
            (int)shapeInfo.Pitch,
            shapeInfo.HotSpotX,
            shapeInfo.HotSpotY,
            bytes);

        if (!shape.IsUsable)
        {
            _logger.LogDebug(
                "Ignoring a pointer shape that does not describe itself consistently: type {Type}, {Width}x{Height}, pitch {Pitch}.",
                shapeInfo.Type,
                shapeInfo.Width,
                shapeInfo.Height,
                shapeInfo.Pitch);
            return;
        }

        _pointer = shape;
        _pointerChanged = true;
    }

    /// <summary>
    /// Copies the mapped rows out, honouring the row pitch, which is not always the row width.
    /// </summary>
    private void CopyRows(D3D11MappedSubresource mapped)
    {
        var pitch = (int)mapped.RowPitch;

        for (var y = 0; y < PixelHeight; y++)
        {
            Marshal.Copy(mapped.Data + (y * pitch), _desktop, y * _stride, _stride);
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
                    Marshal.Copy(data.Scan0 + (y * data.Stride), _desktop, y * _stride, _stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            _delivered = true;
            _pointerChanged = false;

            return CaptureResult.Captured(Compose());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ExternalException)
        {
            return CaptureResult.Unavailable("the desktop could not be read for the first frame");
        }
    }

    /// <summary>
    /// Copies the clean desktop and paints the pointer onto the copy.
    /// <para>
    /// Two buffers rather than one, because the pointer now moves independently of the desktop. If
    /// the cursor were drawn into the capture buffer, the next pointer-only update would have
    /// nothing clean to draw over and the desktop would slowly fill with a trail of dead cursors.
    /// </para>
    /// <para>
    /// The pointer is reported to the client as already drawn (<c>CursorBakedIn</c>), so the phone
    /// never draws its own on top. Sending coordinates and letting the client draw an arrow would
    /// be cheaper and would also be a lie: the owner would see an arrow while their PC was showing
    /// an I-beam, a resize handle or a spinner.
    /// </para>
    /// </summary>
    private RawFrame Compose()
    {
        Buffer.BlockCopy(_desktop, 0, _composed, 0, _desktop.Length);

        var cursorX = 0;
        var cursorY = 0;
        var cursorVisible = false;

        if (_pointerVisible)
        {
            if (_pointer is { } shape)
            {
                shape.DrawInto(_composed, PixelWidth, PixelHeight, _stride, _pointerX, _pointerY);

                // The client is told where the pointer actually points, not where its image starts.
                cursorX = _pointerX + shape.HotspotX;
                cursorY = _pointerY + shape.HotspotY;
                cursorVisible = true;
            }
            else
            {
                // Before the first shape arrives — which needs the mouse to move at least once —
                // fall back to what Windows can tell us about the current cursor.
                cursorVisible = DrawSystemCursor(out cursorX, out cursorY);
            }
        }
        else if (_pointer is null)
        {
            cursorVisible = DrawSystemCursor(out cursorX, out cursorY);
        }

        return new RawFrame(
            _composed,
            PixelWidth,
            PixelHeight,
            _stride,
            DateTimeOffset.UtcNow,
            cursorX,
            cursorY,
            cursorVisible);
    }

    /// <summary>
    /// The stop-gap pointer, used only until Desktop Duplication has reported a real shape.
    /// <para>
    /// Kept because a freshly opened stream on a completely still desktop would otherwise show no
    /// pointer at all until the owner moved the mouse — and the first thing they do is look for it.
    /// </para>
    /// </summary>
    private bool DrawSystemCursor(out int cursorX, out int cursorY)
    {
        cursorX = 0;
        cursorY = 0;

        try
        {
            var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };

            if (!InputInterop.GetCursorInfo(ref info)
                || (info.Flags & InputInterop.CursorShowing) == 0
                || info.Cursor == IntPtr.Zero)
            {
                return false;
            }

            // The duplication surface can be larger than the display rectangle when Windows is
            // scaling; map through the ratio rather than assuming they match.
            var scaleX = Display.Width == 0 ? 1.0 : (double)PixelWidth / Display.Width;
            var scaleY = Display.Height == 0 ? 1.0 : (double)PixelHeight / Display.Height;

            var localX = (int)Math.Round((info.ScreenX - Display.Left) * scaleX);
            var localY = (int)Math.Round((info.ScreenY - Display.Top) * scaleY);

            if (localX < 0 || localY < 0 || localX >= PixelWidth || localY >= PixelHeight)
            {
                return false;
            }

            cursorX = localX;
            cursorY = localY;
            DrawCursor(info.Cursor, localX, localY);
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or OutOfMemoryException)
        {
            // A frame without a pointer drawn on it is still a usable frame. GDI+ reports most
            // failures as OutOfMemoryException, which is why it is caught here rather than allowed
            // to look like real memory exhaustion.
            _logger.LogDebug(ex, "The cursor could not be drawn into a captured frame.");
            return false;
        }
    }

    private void DrawCursor(IntPtr cursor, int x, int y)
    {
        if (!InputInterop.GetIconInfo(cursor, out var icon))
        {
            return;
        }

        try
        {
            var handle = GCHandle.Alloc(_composed, GCHandleType.Pinned);

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

        _desktop = [];
        _composed = [];
        _shapeBuffer = [];
        _pointer = null;
    }
}
