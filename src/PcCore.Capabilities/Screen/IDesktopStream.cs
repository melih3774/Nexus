using PcCore.Protocol;

namespace PcCore.Capabilities.Screen;

/// <summary>
/// One display, as the rest of the system refers to it.
/// <para>
/// <see cref="Index"/> is the stable handle a client uses to pick a display and to address input at
/// it. The rectangle is in Windows virtual-desktop coordinates, which is the same space input
/// injection works in — so the mapping from "a touch on the phone" to "a point on that monitor"
/// never has to guess at a second coordinate system.
/// </para>
/// </summary>
public sealed record DisplayInfo(
    int Index,
    string Name,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public override string ToString() => $"{Name} {Width}x{Height} at ({Left},{Top}){(IsPrimary ? " primary" : "")}";
}

/// <summary>A captured desktop image in BGRA, before encoding.</summary>
/// <param name="Pixels">BGRA rows, <paramref name="Stride"/> bytes apart. May be longer than needed.</param>
/// <param name="MayRepeat">
/// False when this picture must only be sent while the source is still producing it. A frame from the
/// Secure Desktop is the case that matters: the authorisation to capture it is checked on every
/// frame, and the stream must not go on sending a consent prompt after that check has stopped passing.
/// </param>
public sealed record RawFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    DateTimeOffset CapturedAtUtc,
    int CursorX,
    int CursorY,
    bool CursorVisible,
    bool MayRepeat = true);

/// <summary>What one attempt to take a frame produced.</summary>
public enum CaptureStatus
{
    /// <summary>A frame is available.</summary>
    Frame,

    /// <summary>Nothing changed within the timeout. Normal on a still desktop, not an error.</summary>
    Timeout,

    /// <summary>
    /// The capture became invalid — a resolution change, a display recreation, a session switch, or
    /// a mode change. Recoverable: the caller disposes this source and opens a new one.
    /// </summary>
    Lost,

    /// <summary>
    /// Capture cannot proceed right now, for example a locked or secure desktop. The caller backs
    /// off and retries rather than tearing the stream down.
    /// </summary>
    Unavailable,
}

/// <param name="Diagnostic">Safe to log and to show. Never contains pixel data.</param>
public readonly record struct CaptureResult(CaptureStatus Status, RawFrame? Frame, string? Diagnostic)
{
    public static CaptureResult Captured(RawFrame frame) => new(CaptureStatus.Frame, frame, null);

    public static readonly CaptureResult TimedOut = new(CaptureStatus.Timeout, null, null);

    public static CaptureResult Lost(string diagnostic) => new(CaptureStatus.Lost, null, diagnostic);

    public static CaptureResult Unavailable(string diagnostic) => new(CaptureStatus.Unavailable, null, diagnostic);
}

/// <summary>
/// Where the displays are.
/// <para>
/// Separate from <see cref="IDesktopStream"/> because input needs it and streaming needs it, and
/// they need it for the same reason: a point on a phone screen only means something once you know
/// which rectangle of the Windows virtual desktop it belongs to. Keeping it its own interface is
/// what lets the input service be tested with a two-monitor layout and no capture at all.
/// </para>
/// </summary>
public interface IDisplayCatalog
{
    /// <summary>Displays available right now. Re-enumerated on each call, since monitors come and go.</summary>
    IReadOnlyList<DisplayInfo> GetDisplays();
}

/// <summary>
/// An open capture on one display.
/// <para>
/// Deliberately blocking rather than event-driven: Desktop Duplication is a blocking-acquire API,
/// and wrapping it in a callback would only move the loop somewhere less obvious.
/// </para>
/// </summary>
public interface IDesktopCaptureSource : IDisposable
{
    DisplayInfo Display { get; }

    /// <summary>The capture surface size in physical pixels, which DPI scaling can make differ from the display rectangle.</summary>
    int PixelWidth { get; }

    int PixelHeight { get; }

    /// <summary>Waits up to <paramref name="timeout"/> for the next frame. Never throws for expected conditions.</summary>
    CaptureResult TryGetFrame(TimeSpan timeout);
}

/// <summary>
/// Sustained desktop capture.
/// <para>
/// Separate from <see cref="IScreenCapture"/> on purpose, and the two share no implementation. A
/// screenshot is one image on demand and must keep working when nothing else does; a stream is a
/// long-lived pipeline with its own failure modes. Building the second out of repeated calls to the
/// first would have made both worse — and would have meant a streaming fault could break the
/// screenshot that diagnoses it.
/// </para>
/// </summary>
public interface IDesktopStream : IDisplayCatalog
{
    /// <summary>
    /// Opens a capture on a display.
    /// </summary>
    /// <exception cref="CapabilityException">If the display does not exist or capture cannot start.</exception>
    IDesktopCaptureSource Open(int displayIndex);
}

/// <summary>One encoded image, ready to put on the wire.</summary>
public sealed record EncodedFrame(byte[] Bytes, int Width, int Height);

/// <summary>
/// Turns a captured frame into transmittable bytes.
/// <para>
/// Behind an interface because the codec is the part of this design most likely to be replaced. The
/// stream, the framing, the backpressure and the client all work in terms of "an encoded image of
/// this size"; swapping JPEG for something better later is one class, not a redesign.
/// </para>
/// </summary>
public interface IFrameEncoder
{
    FrameEncoding Encoding { get; }

    /// <summary>
    /// Encodes, downscaling proportionally so neither dimension exceeds the bounds.
    /// </summary>
    /// <param name="quality">1-100. Meaning is codec-specific; JPEG treats it as the quality factor.</param>
    EncodedFrame Encode(RawFrame frame, int maxWidth, int maxHeight, int quality);
}
