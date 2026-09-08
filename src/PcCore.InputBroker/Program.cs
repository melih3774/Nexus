using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using PcCore.NativeInput;

[assembly: SupportedOSPlatform("windows10.0.17763.0")]

namespace PcCore.InputBroker;

/// <summary>
/// The Nexus Input Broker.
/// <para>
/// <b>What it is for.</b> Windows refuses input sent from a lower integrity level to a higher one,
/// so an unelevated process cannot drive a window belonging to an Administrator application. That is
/// a real protection and Nexus does not disable it. Instead this one small process — and only this
/// one — runs at high integrity, accepts a fixed list of input actions from PC Core over a local
/// pipe, and performs them. PC Core itself stays an ordinary standard-user process, as it always was.
/// </para>
/// <para>
/// <b>What it deliberately is not.</b> It is not a service, it is not always-on across users, it
/// does not listen on the network, it does not know what Tailscale or the Primary Phone are, and it
/// has no operation that reads a file, starts a process or takes a window handle. Its entire
/// vocabulary is <see cref="BrokerProtocol.Ops"/>. If a request is not on that list it is refused
/// before anything else happens to it.
/// </para>
/// <para>
/// <b>The Secure Desktop.</b> The broker is capable of producing input that a UAC consent prompt
/// would accept, and that is exactly why it checks, on every single batch, whether the ordinary
/// interactive desktop is still the one receiving input — and sends nothing when it is not. The
/// check lives in <see cref="NativeInjector"/>, below the protocol, so no ordering mistake in the
/// message loop can skip it.
/// </para>
/// </summary>
internal static class Program
{
    private static readonly NativeInjector Injector;
    private static readonly string PcCorePath;

    static Program()
    {
        // Per-monitor DPI awareness must match PC Core's, or the pixel coordinates PC Core computes
        // would mean something different here. A mismatch shows up as a pointer that lands slightly
        // off on a scaled display, which is the kind of bug that gets blamed on the phone.
        NativeInjector.EnablePerMonitorDpiAwareness();

        Injector = new NativeInjector();
        PcCorePath = Path.Combine(AppContext.BaseDirectory, "PcCore.Host.exe");
    }

    private static async Task<int> Main(string[] args)
    {
        var interactive = args.Contains("--console");
        BrokerLog.Open(interactive);

        if (args.Contains("--status"))
        {
            return ReportStatus();
        }

        var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;

        if (sid is null)
        {
            BrokerLog.Write("FATAL no user SID on the current token; refusing to start.");
            return 2;
        }

        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        BrokerLog.Write(
            $"Nexus Input Broker {Version} starting. pid={Environment.ProcessId} elevated={elevated} "
            + $"touch={Injector.TouchAvailable} digitizer={NativeInjector.HasPhysicalDigitizer}");

        if (!elevated)
        {
            // Not fatal. An unelevated broker still proves the pipe, the protocol and touch, and PC
            // Core is told plainly that it has no more reach than PC Core itself — which is the one
            // thing that must never be claimed falsely.
            BrokerLog.Write(
                "WARNING running without administrator rights. Elevated application windows will "
                + "stay out of reach; run 'PcCore.Host --install-native-input' to fix this.");
        }

        using var stopping = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        try
        {
            await ServeAsync(sid, elevated, stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            BrokerLog.Write($"FATAL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            var released = Injector.ReleaseAll();

            if (released > 0)
            {
                BrokerLog.Write($"Released {released} held input(s) on shutdown.");
            }
        }

        return 0;
    }

    /// <summary>
    /// One client at a time, forever.
    /// <para>
    /// PC Core restarting must not need the broker restarted, so the loop goes back to listening
    /// after every disconnect. Every disconnect also neutralises the desktop, which is what makes a
    /// dropped connection during a drag safe rather than a stuck mouse button.
    /// </para>
    /// </summary>
    private static async Task ServeAsync(string sid, bool elevated, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var handle = BrokerPipe.CreateServer(sid);
            await using var pipe = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);

            BrokerLog.Write("Listening.");
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

            var client = BrokerPipe.ClientImagePath(handle);

            if (!BrokerPipe.SameImage(client, PcCorePath))
            {
                // The pipe already admits only this user. This check is the second half of that:
                // being the owner is not the same as being the owner's copy of PC Core, and the
                // elevated input path is not something to hand to whatever asked for it first.
                BrokerLog.Write($"REFUSED a connection from an unexpected executable: {Describe(client)}");
                pipe.Disconnect();
                continue;
            }

            BrokerLog.Write("PC Core connected.");

            try
            {
                await PumpAsync(pipe, elevated, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
            {
                BrokerLog.Write($"Connection ended: {ex.GetType().Name}.");
            }
            finally
            {
                var released = Injector.ReleaseAll();
                BrokerLog.Write($"PC Core disconnected; released {released} held input(s).");
            }
        }
    }

    private static async Task PumpAsync(
        NamedPipeServerStream pipe,
        bool elevated,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            var request = await BrokerFraming
                .ReadAsync<BrokerRequest>(pipe, cancellationToken)
                .ConfigureAwait(false);

            if (request is null)
            {
                return;
            }

            var response = Handle(request, elevated);
            await BrokerFraming.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The complete request handler.
    /// <para>
    /// The version and the operation name are checked before anything is read out of the request, so
    /// a message from a newer PC Core, or one naming an operation this broker does not have, is
    /// refused rather than partially understood.
    /// </para>
    /// </summary>
    private static BrokerResponse Handle(BrokerRequest request, bool elevated)
    {
        if (request.Version != BrokerProtocol.Version)
        {
            return Refuse(request, $"this broker speaks protocol version {BrokerProtocol.Version}");
        }

        if (!BrokerProtocol.Ops.All.Contains(request.Op))
        {
            return Refuse(request, "unknown operation");
        }

        var result = request.Op switch
        {
            BrokerProtocol.Ops.Hello or BrokerProtocol.Ops.Desktop => NativeInjectionResult.Ok,
            BrokerProtocol.Ops.MoveAbsolute => Injector.MoveAbsolute(request.X, request.Y),
            BrokerProtocol.Ops.MoveRelative => Injector.MoveRelative(request.Dx, request.Dy),
            BrokerProtocol.Ops.Button => Injector.Button(request.Button, request.Down),
            BrokerProtocol.Ops.Scroll => Injector.Scroll(request.Horizontal, request.Vertical),
            BrokerProtocol.Ops.Key => Key(request),
            BrokerProtocol.Ops.Text => Text(request),
            BrokerProtocol.Ops.Touch => Touch(request),
            _ => NativeInjectionResult.Ok,
        };

        var released = request.Op == BrokerProtocol.Ops.ReleaseAll ? Injector.ReleaseAll() : 0;

        return new BrokerResponse
        {
            Sequence = request.Sequence,
            Delivery = BrokerProtocol.Deliveries.From(result.Delivery),
            Diagnostic = result.Diagnostic,
            Released = released,
            Info = request.Op is BrokerProtocol.Ops.Hello or BrokerProtocol.Ops.Desktop
                ? Describe(elevated)
                : null,
        };
    }

    private static NativeInjectionResult Key(BrokerRequest request)
    {
        // Bounds first: the wire carries an int, and only a ushort can be a virtual-key code. The
        // allow-list inside the injector is what decides whether this particular key is one Nexus
        // may press at all.
        if (request.VirtualKey is < 0 or > ushort.MaxValue)
        {
            return NativeInjectionResult.Failed("virtual-key code out of range");
        }

        return Injector.Key((ushort)request.VirtualKey, request.Extended, request.Down);
    }

    private static NativeInjectionResult Text(BrokerRequest request)
    {
        var text = request.Text;

        if (string.IsNullOrEmpty(text))
        {
            return NativeInjectionResult.Ok;
        }

        // The capability layer already caps this. Capped again here because "the other side checks
        // it" stops being true the moment there is another side.
        return text.Length > MaxTextLength
            ? NativeInjectionResult.Failed("text is longer than the broker accepts")
            : Injector.Text(text);
    }

    private static NativeInjectionResult Touch(BrokerRequest request)
    {
        var phase = BrokerProtocol.Phases.Parse(request.Phase);

        if (phase is null)
        {
            return NativeInjectionResult.Failed("unknown touch phase");
        }

        if (request.Contact is < 0 or >= NativeInjector.MaxTouchContacts)
        {
            return NativeInjectionResult.Failed("touch contact id out of range");
        }

        return Injector.Touch((uint)request.Contact, phase.Value, request.X, request.Y);
    }

    private static BrokerResponse Refuse(BrokerRequest request, string diagnostic) => new()
    {
        Sequence = request.Sequence,
        Delivery = BrokerProtocol.Deliveries.Failed,
        Diagnostic = diagnostic,
    };

    private static BrokerInfo Describe(bool elevated) => new()
    {
        BrokerVersion = Version,
        ProcessId = Environment.ProcessId,
        Elevated = elevated,
        TouchAvailable = Injector.TouchAvailable,
        PhysicalDigitizer = NativeInjector.HasPhysicalDigitizer,
        Desktop = InputDesktop.Current().Kind switch
        {
            InputDesktopKind.Default => "DEFAULT",
            InputDesktopKind.Secure => "SECURE",
            InputDesktopKind.Other => "OTHER",
            _ => "UNKNOWN",
        },
    };

    private static int ReportStatus()
    {
        var identity = WindowsIdentity.GetCurrent();
        var info = Describe(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));

        BrokerLog.Write($"version   : {info.BrokerVersion}");
        BrokerLog.Write($"elevated  : {info.Elevated}");
        BrokerLog.Write($"touch     : {info.TouchAvailable}{(Injector.TouchDiagnostic is { } d ? $" ({d})" : string.Empty)}");
        BrokerLog.Write($"digitizer : {info.PhysicalDigitizer}");
        BrokerLog.Write($"desktop   : {info.Desktop}");
        BrokerLog.Write($"pipe      : \\\\.\\pipe\\{BrokerProtocol.PipeName(identity.User?.Value ?? "?")}");

        return 0;
    }

    /// <summary>Only the file name, never the whole path: a log line is not the place for either.</summary>
    private static string Describe(string? imagePath) =>
        string.IsNullOrEmpty(imagePath) ? "(unknown)" : Path.GetFileName(imagePath);

    private const int MaxTextLength = 4096;

    private static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).FileVersion
        ?? "0.0.0";
}
