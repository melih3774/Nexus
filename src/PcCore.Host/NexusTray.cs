using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Windows.Forms;

using Microsoft.Extensions.Logging;

using PcCore.Capabilities.Media;

namespace PcCore.Host;

/// <summary>Everything the tray menu needs to describe the running system.</summary>
/// <param name="Version">The product version, from the one source that has it.</param>
/// <param name="Input">What is carrying input, in words.</param>
/// <param name="Security">The security state.</param>
/// <param name="Phone">The paired phone, or a note that there is none.</param>
/// <param name="Connected">Whether the phone is connected right now.</param>
/// <param name="Live">Live sensor streams, if any.</param>
/// <param name="Update">One line about updates: up to date, available, updating, or how the last one ended.</param>
/// <param name="UpdateInstallable">Whether "Install update" should be offered.</param>
public sealed record TrayStatus(
    string Version,
    string Input,
    string Security,
    string Phone,
    bool Connected,
    LiveSensorState Live,
    string Update = "Updates: not checked yet",
    bool UpdateInstallable = false);

/// <summary>
/// Nexus in the notification area.
/// <para>
/// <b>Why there is a tray icon at all.</b> Before it, the only evidence that PC Core was running was
/// a console window, and the only way to reach the control panel was to know a port number. An
/// application that runs at sign-in and controls the machine should be visible and closable without
/// a terminal — and the same icon is where the owner goes to stop a live camera stream, which makes
/// it a security control rather than only a convenience.
/// </para>
/// <para>
/// <b>Why its own thread.</b> A <c>NotifyIcon</c> needs a message pump, and PC Core's main thread is
/// running a web host. The tray gets a dedicated STA thread with its own <c>Application.Run</c>,
/// which means a fault in the menu cannot take down the host and a busy host cannot freeze the menu.
/// Nothing on this thread touches capability state directly: it reads a snapshot through
/// <see cref="TrayStatus"/> and raises actions the host handles.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NexusTray : IDisposable
{
    private readonly Func<TrayStatus> _status;
    private readonly string _controlPanelUrl;
    private readonly ILogger _logger;

    private readonly Action _onExit;
    private readonly Action _onStopLive;
    private readonly Action? _onCheckUpdates;
    private readonly Action? _onInstallUpdate;

    private Thread? _thread;
    private NotifyIcon? _icon;
    private ApplicationContext? _context;
    private Icon? _normalIcon;
    private Icon? _liveIcon;
    private System.Windows.Forms.Timer? _poll;
    private bool _lastLive;
    private SynchronizationContext? _sync;

    /// <summary>Where PC Core's log is, so the tray can open it. Null when it could not be created.</summary>
    private readonly string? _logPath;

    public NexusTray(
        Func<TrayStatus> status,
        string controlPanelUrl,
        Action onExit,
        Action onStopLive,
        ILogger logger,
        string? logPath = null,
        Action? onCheckUpdates = null,
        Action? onInstallUpdate = null)
    {
        _status = status;
        _controlPanelUrl = controlPanelUrl;
        _onExit = onExit;
        _onStopLive = onStopLive;
        _logger = logger;
        _logPath = logPath;
        _onCheckUpdates = onCheckUpdates;
        _onInstallUpdate = onInstallUpdate;
    }

    /// <summary>
    /// A notification-area balloon. Used for update outcomes, which usually arrive in a PC Core that
    /// was not running when the update began and has no other way to tell the owner what happened.
    /// </summary>
    public void Notify(string title, string text, bool warning = false)
    {
        var icon = _icon;

        if (icon is null)
        {
            return;
        }

        // Raised on the tray's own thread, which owns the icon.
        _sync?.Post(_ =>
        {
            try
            {
                icon.ShowBalloonTip(8000, title, text.Length > 250 ? text[..247] + "..." : text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The tray could not show a notification.");
            }
        }, null);
    }

    /// <summary>
    /// Starts the tray. Failure is logged and swallowed: a missing tray icon must never stop PC Core
    /// from serving, because the tray is how you reach the product and not the product itself.
    /// </summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "Nexus tray",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Pump()
    {
        try
        {
            _normalIcon = BuildIcon(live: false);
            _liveIcon = BuildIcon(live: true);

            _icon = new NotifyIcon
            {
                Icon = _normalIcon,
                Visible = true,
                Text = "Nexus",
                ContextMenuStrip = BuildMenu(),
            };

            // Double-click is the ordinary Windows gesture for "open this".
            _icon.DoubleClick += (_, _) => OpenControlPanel();

            // The icon changes while a sensor is live, so the notification area itself carries the
            // warning even when the menu is closed and the on-screen indicator is behind a window.
            _poll = new System.Windows.Forms.Timer { Interval = 1000 };
            _poll.Tick += (_, _) => RefreshIcon();
            _poll.Start();

            RefreshIcon();

            // Creating the menu installed the Windows Forms context on this thread; anything that
            // needs the icon from elsewhere posts through it.
            _sync = SynchronizationContext.Current;

            _context = new ApplicationContext();
            Application.Run(_context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Nexus tray icon could not be started; PC Core continues without it.");
        }
        finally
        {
            Cleanup();
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        // Rebuilt on open rather than kept in sync: the menu is looked at a few times a day and the
        // alternative is a second copy of the system's state that can be stale exactly when it
        // matters.
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();

            TrayStatus status;

            try
            {
                status = _status();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The tray could not read PC Core's status.");
                menu.Items.Add(new ToolStripMenuItem("Nexus — status unavailable") { Enabled = false });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("Exit Nexus", null, (_, _) => Exit()));
                return;
            }

            menu.Items.Add(new ToolStripMenuItem($"Nexus {status.Version}") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());

            // The live state goes first and is actionable, because it is the one thing in this menu
            // the owner might need to stop in a hurry.
            if (status.Live.Any)
            {
                menu.Items.Add(new ToolStripMenuItem($"⏺  {status.Live.Describe()}") { Enabled = false });

                var stop = new ToolStripMenuItem("Stop live camera and microphone", null, (_, _) => StopLive())
                {
                    Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
                };

                menu.Items.Add(stop);
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add(new ToolStripMenuItem("Open Nexus", null, (_, _) => OpenControlPanel()));

            // The tray is where the owner arrives when something is wrong, so the log is one click
            // from it. Without this, PC Core's account of what happened exists but is unreachable
            // by anyone who does not already know the path.
            if (_logPath is not null)
            {
                menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()));
            }

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem($"Input: {status.Input}") { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem($"Security: {status.Security}") { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem($"Phone: {status.Phone}") { Enabled = false });

            menu.Items.Add(new ToolStripMenuItem(
                status.Connected ? "Connection: phone connected" : "Connection: waiting for the phone")
            {
                Enabled = false,
            });

            menu.Items.Add(new ToolStripSeparator());

            // Updates: one status line and at most two actions. Installing restarts PC Core, so it is
            // only offered when there is something to install.
            menu.Items.Add(new ToolStripMenuItem(status.Update) { Enabled = false });

            if (status.UpdateInstallable && _onInstallUpdate is not null)
            {
                menu.Items.Add(new ToolStripMenuItem("Install update (restarts Nexus)", null, (_, _) => Run(_onInstallUpdate, "install the update"))
                {
                    Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
                });
            }

            if (_onCheckUpdates is not null)
            {
                menu.Items.Add(new ToolStripMenuItem("Check for updates", null, (_, _) => Run(_onCheckUpdates, "check for updates")));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit Nexus", null, (_, _) => Exit()));
        };

        return menu;
    }

    private void RefreshIcon()
    {
        if (_icon is null)
        {
            return;
        }

        try
        {
            var status = _status();
            var live = status.Live.Any;

            _icon.Text = Truncate(
                live
                    ? $"Nexus {status.Version} — {status.Live.Describe()}"
                    : $"Nexus {status.Version} — {status.Input}");

            if (live != _lastLive)
            {
                _icon.Icon = live ? _liveIcon : _normalIcon;
                _lastLive = live;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The tray could not refresh its status.");
        }
    }

    /// <summary>The tooltip is capped by Windows at 63 characters and silently truncated past it.</summary>
    private static string Truncate(string text) => text.Length <= 63 ? text : text[..60] + "...";

    private void OpenControlPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_controlPanelUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Nexus control panel could not be opened.");
        }
    }

    /// <summary>
    /// Opens the log in whatever the owner uses for text files.
    /// <para>
    /// Selected in Explorer rather than opened directly: PC Core holds the file open for appending,
    /// and handing the path to a viewer that wants exclusive access is how "open log" becomes an
    /// error dialog. Explorer also puts the previous generation right next to it.
    /// </para>
    /// </summary>
    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_logPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Nexus log could not be opened.");
        }
    }

    /// <summary>Off the tray thread: a check hashes the install directory, and the menu must not freeze for it.</summary>
    private void Run(Action action, string what) => _ = Task.Run(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The tray could not {What}.", what);
        }
    });

    private void StopLive()
    {
        try
        {
            _onStopLive();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live capture could not be stopped from the tray.");
        }
    }

    private void Exit()
    {
        try
        {
            _onExit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nexus could not be stopped from the tray.");
        }
    }

    /// <summary>
    /// Draws the icon rather than shipping one.
    /// <para>
    /// A generated icon means no binary asset to keep in the repository and no chance of the file
    /// going missing from a build output. The live variant is red on purpose and is the same signal
    /// as the on-screen indicator: while a sensor is being sent to the phone, the notification area
    /// says so without the owner opening anything.
    /// </para>
    /// </summary>
    private static Icon BuildIcon(bool live)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var background = live ? Color.FromArgb(200, 40, 40) : Color.FromArgb(38, 44, 54);

        using (var brush = new SolidBrush(background))
        {
            graphics.FillEllipse(brush, 1, 1, 30, 30);
        }

        using (var pen = new Pen(live ? Color.FromArgb(255, 160, 160) : Color.FromArgb(120, 190, 255), 2f))
        {
            graphics.DrawEllipse(pen, 1, 1, 29, 29);
        }

        // An "N", drawn with lines so it renders identically whatever fonts are installed.
        using (var pen = new Pen(Color.White, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            graphics.DrawLine(pen, 11, 22, 11, 10);
            graphics.DrawLine(pen, 11, 10, 21, 22);
            graphics.DrawLine(pen, 21, 22, 21, 10);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private void Cleanup()
    {
        try
        {
            if (_poll is not null)
            {
                _poll.Stop();
                _poll.Dispose();
                _poll = null;
            }

            if (_icon is not null)
            {
                // Hidden before disposal: an icon disposed while visible leaves a dead entry in the
                // notification area until the user hovers over it.
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }

            _normalIcon?.Dispose();
            _liveIcon?.Dispose();
            _normalIcon = null;
            _liveIcon = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The Nexus tray icon did not clean up cleanly.");
        }
    }

    public void Dispose()
    {
        try
        {
            _context?.ExitThread();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The Nexus tray thread did not stop cleanly.");
        }

        _thread = null;
    }
}
