using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Indicator;
using PcCore.Windows.Interop;

namespace PcCore.Windows;

/// <summary>
/// The visible sign, on the PC, that a phone is watching or driving it.
/// <para>
/// <b>This is not a security control and does not pretend to be one.</b> It gates nothing and can be
/// dragged behind nothing; it exists so the owner can always tell. That is why there is no mode that
/// turns everything off: an indicator that can be silenced is worth roughly nothing, and a
/// remote-control tool with no sign of operation is a different kind of program altogether.
/// </para>
/// <para>
/// It lives on its own thread with its own message loop. Windows requires a message pump for any
/// window, and putting the badge on the host's threads would mean a slow capture frame could stall
/// a repaint, or worse, the reverse.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteIndicator : IRemoteIndicator
{
    private const string ClassName = "NexusRemoteIndicator";
    private const int BadgeWidth = 176;
    private const int BadgeHeight = 40;
    private const int Margin = 16;
    private const uint TrayIconId = 1;

    private readonly ILogger<WindowsRemoteIndicator> _logger;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Lock _gate = new();

    // Held so the window procedure is not collected while Windows still holds a pointer to it.
    private readonly WindowProcedure _windowProcedure;

    private Thread? _thread;
    private nint _window;
    private nint _trayIcon;
    private bool _trayVisible;
    private IndicatorState _desired = IndicatorState.Idle;
    private IndicatorState _shown = IndicatorState.Idle;
    private volatile bool _failed;

    public WindowsRemoteIndicator(ILogger<WindowsRemoteIndicator> logger)
    {
        _logger = logger;
        _windowProcedure = WindowProc;
    }

    public void Apply(IndicatorState state)
    {
        if (_failed)
        {
            return;
        }

        lock (_gate)
        {
            _desired = state;
        }

        EnsureStarted();

        if (_window != IntPtr.Zero)
        {
            IndicatorInterop.PostMessage(_window, IndicatorInterop.WmAppUpdate, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _thread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "Nexus indicator",
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        // A window that never appeared would leave every later post going nowhere; wait briefly and
        // give up rather than silently doing nothing for the rest of the run.
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _failed = true;
            _logger.LogWarning("The remote-control indicator window did not start; no on-screen badge will appear.");
        }
    }

    private void RunMessageLoop()
    {
        try
        {
            var instance = IndicatorInterop.GetModuleHandle(null);

            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                Instance = instance,
                ClassName = ClassName,
            };

            // A duplicate class registration after a restart within the same process is harmless.
            IndicatorInterop.RegisterClassEx(ref windowClass);

            _window = IndicatorInterop.CreateWindowEx(
                IndicatorInterop.WsExLayered
                | IndicatorInterop.WsExTransparent
                | IndicatorInterop.WsExToolWindow
                | IndicatorInterop.WsExNoActivate
                | IndicatorInterop.WsExTopMost,
                ClassName,
                "Nexus Remote",
                IndicatorInterop.WsPopup,
                0,
                0,
                BadgeWidth,
                BadgeHeight,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);

            if (_window == IntPtr.Zero)
            {
                _logger.LogWarning("The remote-control indicator window could not be created.");
                _failed = true;
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The remote-control indicator could not start.");
            _failed = true;
            return;
        }
        finally
        {
            _ready.Set();
        }

        Update();

        while (IndicatorInterop.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            IndicatorInterop.TranslateMessage(ref message);
            IndicatorInterop.DispatchMessage(ref message);
        }

        RemoveTrayIcon();
    }

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case IndicatorInterop.WmAppUpdate:
                Update();
                return IntPtr.Zero;

            case IndicatorInterop.WmAppQuit:
                RemoveTrayIcon();
                IndicatorInterop.DestroyWindow(window);
                return IntPtr.Zero;

            case IndicatorInterop.WmDestroy:
                IndicatorInterop.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return IndicatorInterop.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private void Update()
    {
        IndicatorState state;

        lock (_gate)
        {
            state = _desired;

            if (state == _shown)
            {
                return;
            }

            _shown = state;
        }

        try
        {
            ApplyTray(state);
            ApplyOverlay(state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The remote-control indicator could not be drawn.");
        }
    }

    private void ApplyOverlay(IndicatorState state)
    {
        if (!state.Overlay)
        {
            IndicatorInterop.ShowWindow(_window, IndicatorInterop.SwHide);
            return;
        }

        using var badge = DrawBadge(state.Text);

        var position = BadgePosition();
        var screenDc = IndicatorInterop.GetDC(IntPtr.Zero);
        var memoryDc = IndicatorInterop.CreateCompatibleDC(screenDc);

        // Zero means "make black transparent"; the bitmap is premultiplied so alpha is per pixel.
        var bitmap = badge.GetHbitmap(Color.FromArgb(0));
        var previous = IndicatorInterop.SelectObject(memoryDc, bitmap);

        try
        {
            var size = new NativeSize { Width = BadgeWidth, Height = BadgeHeight };
            var source = new Interop.Point { X = 0, Y = 0 };
            var blend = new BlendFunction
            {
                BlendOp = IndicatorInterop.AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = IndicatorInterop.AcSrcAlpha,
            };

            IndicatorInterop.UpdateLayeredWindow(
                _window,
                screenDc,
                ref position,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                IndicatorInterop.UlwAlpha);

            IndicatorInterop.ShowWindow(_window, IndicatorInterop.SwShowNoActivate);
        }
        finally
        {
            IndicatorInterop.SelectObject(memoryDc, previous);
            IndicatorInterop.DeleteObject(bitmap);
            IndicatorInterop.DeleteDC(memoryDc);
            IndicatorInterop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Bottom-right of the work area: visible without being in front of anything. The work area
    /// excludes the taskbar, so the badge never sits on top of it.
    /// </summary>
    private static Interop.Point BadgePosition()
    {
        var work = new NativeRect();

        if (!IndicatorInterop.SystemParametersInfo(IndicatorInterop.SpiGetWorkArea, 0, ref work, 0))
        {
            return new Interop.Point { X = Margin, Y = Margin };
        }

        return new Interop.Point
        {
            X = work.Right - BadgeWidth - Margin,
            Y = work.Bottom - BadgeHeight - Margin,
        };
    }

    private static Bitmap DrawBadge(string text)
    {
        var bitmap = new Bitmap(BadgeWidth, BadgeHeight, PixelFormat.Format32bppPArgb);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.Transparent);

        using var background = new SolidBrush(Color.FromArgb(226, 11, 15, 20));
        using var path = RoundedRectangle(new Rectangle(0, 0, BadgeWidth - 1, BadgeHeight - 1), 12);
        graphics.FillPath(background, path);

        using var border = new Pen(Color.FromArgb(90, 47, 216, 168));
        graphics.DrawPath(border, path);

        using var dot = new SolidBrush(Color.FromArgb(255, 47, 216, 168));
        graphics.FillEllipse(dot, 14, (BadgeHeight - 9) / 2, 9, 9);

        using var font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        using var foreground = new SolidBrush(Color.FromArgb(240, 232, 237, 242));
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };

        graphics.DrawString(
            text,
            font,
            foreground,
            new RectangleF(32, 0, BadgeWidth - 40, BadgeHeight),
            format);

        return bitmap;
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    private void ApplyTray(IndicatorState state)
    {
        if (!state.Tray)
        {
            RemoveTrayIcon();
            return;
        }

        _trayIcon = _trayIcon == IntPtr.Zero ? CreateTrayIcon() : _trayIcon;

        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _window,
            Id = TrayIconId,
            Flags = IndicatorInterop.NifIcon | IndicatorInterop.NifTip,
            Icon = _trayIcon,
            Tip = state.Text,
            Info = string.Empty,
            InfoTitle = string.Empty,
        };

        // Added on first use, modified afterwards, so the tooltip follows viewing versus controlling.
        var message = _trayVisible ? IndicatorInterop.NimModify : IndicatorInterop.NimAdd;

        if (IndicatorInterop.ShellNotifyIcon(message, ref data))
        {
            _trayVisible = true;
        }
    }

    private void RemoveTrayIcon()
    {
        if (!_trayVisible)
        {
            return;
        }

        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _window,
            Id = TrayIconId,
            Tip = string.Empty,
            Info = string.Empty,
            InfoTitle = string.Empty,
        };

        IndicatorInterop.ShellNotifyIcon(IndicatorInterop.NimDelete, ref data);
        _trayVisible = false;

        if (_trayIcon != IntPtr.Zero)
        {
            IndicatorInterop.DeleteObject(_trayIcon);
            _trayIcon = IntPtr.Zero;
        }
    }

    /// <summary>A filled dot in the Nexus accent, drawn rather than shipped as a resource.</summary>
    private static nint CreateTrayIcon()
    {
        using var bitmap = new Bitmap(16, 16, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var brush = new SolidBrush(Color.FromArgb(255, 47, 216, 168));
            graphics.FillEllipse(brush, 2, 2, 12, 12);
        }

        return bitmap.GetHicon();
    }

    public void Dispose()
    {
        var window = _window;

        if (window != IntPtr.Zero)
        {
            IndicatorInterop.PostMessage(window, IndicatorInterop.WmAppQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
        _window = IntPtr.Zero;
    }
}
