using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Screen;

/// <summary>Which display to capture.</summary>
/// <param name="Kind">Virtual desktop, primary display, or a specific index.</param>
/// <param name="Index">Zero-based display index; only meaningful when <see cref="Kind"/> is <see cref="DisplaySelectionKind.Index"/>.</param>
public readonly record struct DisplaySelection(DisplaySelectionKind Kind, int Index)
{
    public static readonly DisplaySelection Virtual = new(DisplaySelectionKind.Virtual, 0);

    public override string ToString() => Kind switch
    {
        DisplaySelectionKind.Virtual => "virtual",
        DisplaySelectionKind.Primary => "primary",
        _ => Index.ToString(),
    };
}

public enum DisplaySelectionKind
{
    Virtual,
    Primary,
    Index,
}

/// <summary>A single captured frame, already encoded.</summary>
/// <param name="Png">PNG bytes.</param>
/// <param name="Width">Width of the returned image.</param>
/// <param name="Height">Height of the returned image.</param>
/// <param name="SourceWidth">Width of the captured region before any downscale.</param>
/// <param name="SourceHeight">Height of the captured region before any downscale.</param>
public sealed record CapturedFrame(byte[] Png, int Width, int Height, int SourceWidth, int SourceHeight);

/// <summary>
/// One request, one frame.
/// <para>
/// This interface has no session, subscription, or frame-sequence concept, and that is deliberate:
/// it cannot accidentally grow into a streaming API. When streaming arrives it will be a separate
/// capability backed by DXGI Desktop Duplication, and this contract can stay exactly as it is for
/// the single-shot case (SPEC.md 2).
/// </para>
/// </summary>
public interface IScreenCapture
{
    /// <summary>Captures one frame, optionally downscaled so its width does not exceed <paramref name="maxWidth"/>.</summary>
    /// <exception cref="CapabilityException">If the display selection is invalid or capture is unavailable.</exception>
    CapturedFrame Capture(DisplaySelection selection, int? maxWidth);
}

public sealed class ScreenOptions
{
    public const string SectionName = "PcCore:Screen";

    /// <summary>When false, <c>screen.capture</c> returns <c>unsupported</c> without touching the desktop.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Upper bound accepted for the <c>max_width</c> parameter.</summary>
    public int MaxWidthLimit { get; set; } = 7680;
}

public sealed record CaptureScreenParameters
{
    /// <summary><c>virtual</c> (default), <c>primary</c>, or a zero-based display index as a string.</summary>
    public string? Display { get; init; }

    /// <summary>Optional proportional downscale bound. Screenshots are large; this keeps payloads sane.</summary>
    public int? MaxWidth { get; init; }
}

public sealed record CaptureScreenResult
{
    public required string Display { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
    public required string ContentType { get; init; }
    public required string Encoding { get; init; }

    /// <summary>Base64 PNG. See SPEC.md 5 for why the image is inline rather than a blob handle.</summary>
    public required string Data { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }
}

public sealed class CaptureScreenAction : CapabilityAction<CaptureScreenParameters, CaptureScreenResult>
{
    private readonly IScreenCapture _capture;
    private readonly ScreenOptions _options;

    public CaptureScreenAction(IScreenCapture capture, ScreenOptions options)
    {
        _capture = capture;
        _options = options;
    }

    public override string Name => "screen.capture";

    public override string Description => "Capture a single PNG screenshot of the desktop or one display.";

    protected override ValueTask<CaptureScreenResult> ExecuteAsync(
        CaptureScreenParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new CapabilityException(ErrorCodes.Unsupported, "Screen capture is disabled by configuration.");
        }

        var selection = ParseDisplay(parameters.Display);
        var maxWidth = ValidateMaxWidth(parameters.MaxWidth);

        cancellationToken.ThrowIfCancellationRequested();

        var frame = _capture.Capture(selection, maxWidth);

        return ValueTask.FromResult(new CaptureScreenResult
        {
            Display = selection.ToString(),
            Width = frame.Width,
            Height = frame.Height,
            SourceWidth = frame.SourceWidth,
            SourceHeight = frame.SourceHeight,
            ContentType = "image/png",
            Encoding = "base64",
            Data = Convert.ToBase64String(frame.Png),
            CapturedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    internal static DisplaySelection ParseDisplay(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return DisplaySelection.Virtual;
        }

        var value = display.Trim();

        if (value.Equals("virtual", StringComparison.OrdinalIgnoreCase))
        {
            return DisplaySelection.Virtual;
        }

        if (value.Equals("primary", StringComparison.OrdinalIgnoreCase))
        {
            return new DisplaySelection(DisplaySelectionKind.Primary, 0);
        }

        if (int.TryParse(value, out var index) && index >= 0)
        {
            return new DisplaySelection(DisplaySelectionKind.Index, index);
        }

        throw new CapabilityException(
            ErrorCodes.InvalidParameters,
            "Parameter 'display' must be 'virtual', 'primary', or a non-negative display index.");
    }

    private int? ValidateMaxWidth(int? maxWidth)
    {
        if (maxWidth is null)
        {
            return null;
        }

        if (maxWidth < 1 || maxWidth > _options.MaxWidthLimit)
        {
            throw new CapabilityException(
                ErrorCodes.InvalidParameters,
                $"Parameter 'max_width' must be between 1 and {_options.MaxWidthLimit}.");
        }

        return maxWidth;
    }
}
