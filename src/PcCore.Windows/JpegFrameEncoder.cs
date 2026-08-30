using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;

namespace PcCore.Windows;

/// <summary>
/// Encodes captured frames as JPEG.
/// <para>
/// <b>Why JPEG and not a video codec.</b> A remote-control stream at fifteen frames a second over a
/// private tunnel does not need interframe compression to be usable, and H.264 would bring a Media
/// Foundation pipeline, hardware-encoder capability differences, keyframe and bitrate management, and
/// a decoder on the Android side with its own surface lifecycle — a milestone's worth of work whose
/// benefit is bandwidth this link is not short of. Every frame here is independent, which also means
/// a dropped frame costs exactly one repaint instead of corrupting everything until the next
/// keyframe. That property is what makes the drop-stale-frames backpressure policy safe.
/// </para>
/// <para>
/// The interface it implements is the point: when a measured need for a video codec appears, it
/// replaces this class and nothing else.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JpegFrameEncoder : IFrameEncoder, IDisposable
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    private readonly Lock _gate = new();
    private readonly MemoryStream _buffer = new(512 * 1024);

    private Bitmap? _scaled;

    public FrameEncoding Encoding => FrameEncoding.Jpeg;

    public EncodedFrame Encode(RawFrame frame, int maxWidth, int maxHeight, int quality)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var (width, height) = Fit(frame.Width, frame.Height, maxWidth, maxHeight);

        lock (_gate)
        {
            // The capture buffer is reused between frames, so it is pinned only for as long as the
            // encode takes and never handed out.
            var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);

            try
            {
                using var source = new Bitmap(
                    frame.Width,
                    frame.Height,
                    frame.Stride,
                    PixelFormat.Format32bppRgb,
                    handle.AddrOfPinnedObject());

                var image = width == frame.Width && height == frame.Height ? source : Downscale(source, width, height);

                _buffer.SetLength(0);

                using var parameters = new EncoderParameters(1);
                using var qualityParameter = new EncoderParameter(
                    Encoder.Quality, (long)Math.Clamp(quality, 1, 100));

                parameters.Param[0] = qualityParameter;

                image.Save(_buffer, JpegCodec, parameters);

                return new EncodedFrame(_buffer.ToArray(), width, height);
            }
            finally
            {
                handle.Free();
            }
        }
    }

    private Bitmap Downscale(Bitmap source, int width, int height)
    {
        if (_scaled is null || _scaled.Width != width || _scaled.Height != height)
        {
            _scaled?.Dispose();
            _scaled = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        }

        using var graphics = Graphics.FromImage(_scaled);

        // Bilinear rather than bicubic: at fifteen frames a second the cost difference is real and
        // the visible difference on downscaled desktop text is not.
        graphics.InterpolationMode = InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.DrawImage(source, 0, 0, width, height);

        return _scaled;
    }

    /// <summary>
    /// Scales proportionally to fit the bounds, never enlarging. Public so the sizing rule the
    /// client depends on can be tested without a desktop.
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            return (Math.Max(1, width), Math.Max(1, height));
        }

        var limitWidth = maxWidth <= 0 ? width : maxWidth;
        var limitHeight = maxHeight <= 0 ? height : maxHeight;

        var scale = Math.Min((double)limitWidth / width, (double)limitHeight / height);

        if (scale >= 1)
        {
            return (width, height);
        }

        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _scaled?.Dispose();
            _scaled = null;
            _buffer.Dispose();
        }
    }
}
