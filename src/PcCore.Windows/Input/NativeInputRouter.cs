using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Input;
using PcCore.NativeInput;

namespace PcCore.Windows.Input;

/// <summary>
/// Chooses the input path, on every event, without ever asking the owner.
/// <para>
/// <b>This is the whole point of the native input work.</b> There is no "enable admin input" and no
/// mode to turn on each session: if the Nexus Input Broker is installed and healthy, it carries the
/// input, and if it is not, PC Core injects for itself exactly as it always did. The phone sends the
/// same messages either way. Direct and Touchpad stay what they always were — a choice about how the
/// phone behaves — and have nothing to do with which of these two paths is live.
/// </para>
/// <para>
/// <b>Failure is a fallback, not an error.</b> A broker that dies mid-session takes its held buttons
/// and contacts with it — it releases everything on disconnect — and the next event goes through the
/// local path. The owner sees the status change from ready to not installed; nothing stops working
/// except the ability to drive an elevated window.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeInputRouter : IInputController, IDisposable
{
    private readonly BrokerInputChannel _broker;
    private readonly WindowsInputController _local;
    private readonly NativeInputInstaller _installer;
    private readonly ILogger<NativeInputRouter> _logger;
    private readonly Lock _gate = new();

    private bool _usingBroker;

    public NativeInputRouter(
        BrokerInputChannel broker,
        WindowsInputController local,
        NativeInputInstaller installer,
        ILogger<NativeInputRouter> logger)
    {
        _broker = broker;
        _local = local;
        _installer = installer;
        _logger = logger;
    }

    public (int Left, int Top, int Width, int Height) VirtualDesktop => _local.VirtualDesktop;

    public NativeInputStatus Status
    {
        get
        {
            var info = _broker.Refresh();

            if (info is not null)
            {
                return new NativeInputStatus
                {
                    Path = InputPath.NativeBroker,
                    State = info.Elevated ? NativeInputState.Ready : NativeInputState.Degraded,
                    ReachesElevatedWindows = info.Elevated,
                    TouchAvailable = info.TouchAvailable,
                    PhysicalDigitizer = info.PhysicalDigitizer,
                    Desktop = info.Desktop,
                    BrokerVersion = info.BrokerVersion,

                    // Said plainly rather than glossed over. A broker running without elevation is
                    // the one state where claiming "native input: ready" would be a lie the owner
                    // could only discover by trying to click something and failing.
                    Diagnostic = info.Elevated
                        ? null
                        : "The Nexus Input Broker is running without administrator rights, so elevated "
                          + "application windows are still out of reach. Repair native input to fix this.",
                };
            }

            var installation = _installer.GetStatus();
            var local = _local.Status;

            return local with
            {
                State = installation.Registered ? NativeInputState.Unavailable : NativeInputState.NotInstalled,
                Diagnostic = installation.Registered
                    ? _broker.Diagnostic
                      ?? "Native input is installed but not responding; PC Core is injecting input itself."
                    : "Native input is not installed. PC Core is injecting input itself, so elevated "
                      + "application windows are out of reach.",
            };
        }
    }

    public InputResult MovePointerAbsolute(int x, int y) =>
        Route(
            new BrokerRequest { Op = BrokerProtocol.Ops.MoveAbsolute, X = x, Y = y },
            () => _local.MovePointerAbsolute(x, y));

    public InputResult MovePointerRelative(int dx, int dy) =>
        Route(
            new BrokerRequest { Op = BrokerProtocol.Ops.MoveRelative, Dx = dx, Dy = dy },
            () => _local.MovePointerRelative(dx, dy));

    public InputResult Button(PointerButton button, bool down) =>
        Route(
            new BrokerRequest { Op = BrokerProtocol.Ops.Button, Button = (int)button, Down = down },
            () => _local.Button(button, down));

    public InputResult Scroll(int horizontalNotches, int verticalNotches) =>
        Route(
            new BrokerRequest
            {
                Op = BrokerProtocol.Ops.Scroll,
                Horizontal = horizontalNotches,
                Vertical = verticalNotches,
            },
            () => _local.Scroll(horizontalNotches, verticalNotches));

    public InputResult Key(NexusKey key, bool down)
    {
        var (virtualKey, extended) = WindowsKeyMap.Map(key);

        return Route(
            new BrokerRequest
            {
                Op = BrokerProtocol.Ops.Key,
                VirtualKey = virtualKey,
                Extended = extended,
                Down = down,
            },
            () => _local.Key(key, down));
    }

    public InputResult Text(string text) =>
        Route(
            new BrokerRequest { Op = BrokerProtocol.Ops.Text, Text = text },
            () => _local.Text(text));

    public InputResult Touch(int contactId, TouchAction action, int x, int y)
    {
        var result = Route(
            new BrokerRequest
            {
                Op = BrokerProtocol.Ops.Touch,
                Contact = contactId,
                Phase = action switch
                {
                    TouchAction.Down => BrokerProtocol.Phases.Down,
                    TouchAction.Move => BrokerProtocol.Phases.Move,
                    TouchAction.Up => BrokerProtocol.Phases.Up,
                    _ => BrokerProtocol.Phases.Cancel,
                },
                X = x,
                Y = y,
            },
            () => _local.Touch(contactId, action, x, y));

        // A broker on a machine that cannot inject touch still has a pointer. Falling back through
        // the same broker keeps Direct mode working there, and — importantly — keeps it working
        // against elevated windows, which the local path could not manage either way.
        return result.Delivery == InputDelivery.Unsupported
            ? TouchFallback.Apply(this, contactId, action, x, y)
            : result;
    }

    /// <summary>
    /// Releases held input on both paths.
    /// <para>
    /// Both, unconditionally, and not only the one currently in use. After a switch — a broker that
    /// died mid-drag, or one that appeared mid-session — the button Windows believes is down may
    /// have been pressed by the other one.
    /// </para>
    /// </summary>
    public int ReleaseAllHeld()
    {
        var released = _local.ReleaseAllHeld();
        var response = _broker.Send(new BrokerRequest { Op = BrokerProtocol.Ops.ReleaseAll });

        return released + (response?.Released ?? 0);
    }

    private InputResult Route(BrokerRequest request, Func<InputResult> local)
    {
        var response = _broker.Send(request);

        if (response is null)
        {
            Note(usingBroker: false);
            return local();
        }

        Note(usingBroker: true);

        var delivery = BrokerProtocol.Deliveries.To(response.Delivery);

        return WindowsInputController.Translate(new NativeInjectionResult(delivery, response.Diagnostic));
    }

    /// <summary>Logs the path changing, once, rather than the path in use, constantly.</summary>
    private void Note(bool usingBroker)
    {
        lock (_gate)
        {
            if (_usingBroker == usingBroker)
            {
                return;
            }

            _usingBroker = usingBroker;
        }

        if (usingBroker)
        {
            _logger.LogInformation("Input is now going through the Nexus Input Broker.");
        }
        else
        {
            _logger.LogWarning(
                "Input has fallen back to PC Core's own injection; elevated application windows "
                + "are out of reach until the Nexus Input Broker is available again.");
        }
    }

    public void Dispose() => _broker.Dispose();
}
