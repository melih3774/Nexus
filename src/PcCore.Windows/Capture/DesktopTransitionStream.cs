using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Screen;
using PcCore.NativeInput;

namespace PcCore.Windows.Capture;

/// <summary>
/// The desktop, whichever desktop that currently is.
/// <para>
/// <b>Why the switch lives inside the capture source.</b> The alternative is to let the stream fail
/// and restart, and that is what used to happen: Desktop Duplication returned "access denied" the
/// moment UAC appeared, the stream service disposed the source and reopened it a second later, and
/// the phone saw a stream that had stopped. Owning the transition here means the stream id, the
/// session, the encoder and the socket all survive a desktop change — the picture simply starts
/// coming from somewhere else for a few seconds and then comes back.
/// </para>
/// <para>
/// <b>Nothing is captured off the Secure Desktop without an owner-authenticated session.</b> That is
/// checked here on every frame rather than once when the stream started, so an authorisation that
/// lapses while a consent prompt is up stops the picture at the same moment it stops the input.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopTransitionStream : IDesktopStream
{
    private readonly IDesktopStream _ordinary;
    private readonly SecureDesktopCapture _secure;
    private readonly Func<bool> _secureCaptureAuthorized;
    private readonly Func<InputDesktopState> _desktop;
    private readonly ILoggerFactory _loggers;

    public DesktopTransitionStream(
        IDesktopStream ordinary,
        SecureDesktopCapture secure,
        Func<bool> secureCaptureAuthorized,
        ILoggerFactory loggers,
        Func<InputDesktopState>? desktop = null)
    {
        _ordinary = ordinary;
        _secure = secure;
        _secureCaptureAuthorized = secureCaptureAuthorized;
        _loggers = loggers;
        _desktop = desktop ?? InputDesktop.Current;
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => _ordinary.GetDisplays();

    public IDesktopCaptureSource Open(int displayIndex)
    {
        var displays = _ordinary.GetDisplays();

        var display = displays.FirstOrDefault(d => d.Index == displayIndex)
            ?? displays.FirstOrDefault(d => d.IsPrimary)
            ?? displays.FirstOrDefault()
            ?? throw new PcCore.Capabilities.Abstractions.CapabilityException(
                PcCore.Protocol.ErrorCodes.Unsupported, "No display is available to capture.");

        return new DesktopTransitionSource(
            _ordinary,
            _secure,
            _secureCaptureAuthorized,
            _desktop,
            display,
            _loggers.CreateLogger<DesktopTransitionSource>());
    }
}

[SupportedOSPlatform("windows")]
internal sealed class DesktopTransitionSource : IDesktopCaptureSource
{
    /// <summary>
    /// The Secure Desktop is captured with a whole-screen GDI copy, and a consent prompt does not
    /// move. Four frames a second is more than enough to see it and to see the pointer land on a
    /// button, and it keeps an eight-megabyte copy off the pipe fifteen times a second.
    /// </summary>
    private static readonly TimeSpan SecureInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long to wait before trying to reopen duplication after it refused.</summary>
    private static readonly TimeSpan ReopenBackoff = TimeSpan.FromMilliseconds(500);

    private readonly IDesktopStream _ordinary;
    private readonly SecureDesktopCapture _secure;
    private readonly Func<bool> _secureCaptureAuthorized;
    private readonly Func<InputDesktopState> _desktop;
    private readonly ILogger<DesktopTransitionSource> _logger;

    private IDesktopCaptureSource? _inner;
    private DateTimeOffset _nextOpen = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSecureFrame = DateTimeOffset.MinValue;
    private InputDesktopKind _lastKind = InputDesktopKind.Default;
    private bool _refusalLogged;

    public DesktopTransitionSource(
        IDesktopStream ordinary,
        SecureDesktopCapture secure,
        Func<bool> secureCaptureAuthorized,
        Func<InputDesktopState> desktop,
        DisplayInfo display,
        ILogger<DesktopTransitionSource> logger)
    {
        _ordinary = ordinary;
        _secure = secure;
        _secureCaptureAuthorized = secureCaptureAuthorized;
        _desktop = desktop;
        _logger = logger;
        Display = display;
    }

    public DisplayInfo Display { get; }

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public CaptureResult TryGetFrame(TimeSpan timeout)
    {
        var desktop = _desktop();

        if (desktop.Kind != _lastKind)
        {
            _logger.LogInformation(
                "Capture is following the desktop change to {Desktop}.", desktop.Name ?? desktop.Kind.ToString());

            _lastKind = desktop.Kind;
            _refusalLogged = false;
        }

        return desktop.IsInteractive ? Ordinary(timeout) : Secure();
    }

    private CaptureResult Ordinary(TimeSpan timeout)
    {
        // The worker is a SYSTEM process; it does not stay running because the desktop might change
        // again. It is started in well under a second when it is needed.
        if (_secure.IsConnected)
        {
            _secure.Release();
        }

        if (_inner is null)
        {
            if (DateTimeOffset.UtcNow < _nextOpen)
            {
                return CaptureResult.TimedOut;
            }

            try
            {
                _inner = _ordinary.Open(Display.Index);
                PixelWidth = _inner.PixelWidth;
                PixelHeight = _inner.PixelHeight;
            }
            catch (Exception ex) when (ex is PcCore.Capabilities.Abstractions.CapabilityException
                                          or InvalidOperationException)
            {
                _nextOpen = DateTimeOffset.UtcNow + ReopenBackoff;

                if (!_refusalLogged)
                {
                    _logger.LogInformation("Duplication is not available yet ({Reason}); retrying.", ex.Message);
                    _refusalLogged = true;
                }

                return CaptureResult.TimedOut;
            }
        }

        var result = _inner.TryGetFrame(timeout);

        if (result.Status is CaptureStatus.Lost or CaptureStatus.Unavailable)
        {
            // Absorbed rather than propagated. Every one of these is recoverable, and reporting them
            // upward would tear down the stream the owner is watching so it could be rebuilt
            // identically a second later.
            _logger.LogInformation("Duplication was interrupted ({Reason}); reopening.", result.Diagnostic);

            _inner.Dispose();
            _inner = null;
            _nextOpen = DateTimeOffset.UtcNow + ReopenBackoff;

            return CaptureResult.TimedOut;
        }

        return result;
    }

    private CaptureResult Secure()
    {
        if (_inner is not null)
        {
            // Duplication cannot read this desktop and holding it open across the transition is what
            // used to produce a stream of access-denied errors.
            _inner.Dispose();
            _inner = null;
            _nextOpen = DateTimeOffset.MinValue;
        }

        if (!_secureCaptureAuthorized())
        {
            if (!_refusalLogged)
            {
                _logger.LogInformation(
                    "Windows is showing a secure screen and no owner-authenticated session is live; "
                    + "nothing is being captured.");

                _refusalLogged = true;
            }

            if (_secure.IsConnected)
            {
                _secure.Release();
            }

            return CaptureResult.TimedOut;
        }

        if (DateTimeOffset.UtcNow < _nextSecureFrame)
        {
            return CaptureResult.TimedOut;
        }

        _nextSecureFrame = DateTimeOffset.UtcNow + SecureInterval;

        var frame = _secure.TryCapture();

        if (frame is null)
        {
            return CaptureResult.TimedOut;
        }

        PixelWidth = frame.Width;
        PixelHeight = frame.Height;

        return CaptureResult.Captured(frame);
    }

    public void Dispose()
    {
        _inner?.Dispose();
        _inner = null;
        _secure.Release();
    }
}
