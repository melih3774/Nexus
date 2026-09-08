using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Input;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Storage;
using PcCore.NativeInput;
using PcCore.Protocol;
using PcCore.Windows;
using PcCore.Windows.Input;

namespace PcCore.Host;

/// <summary>
/// Exercises the real Windows input stack, on this machine, through the same path a phone uses.
/// <para>
/// <b>Why this exists rather than more unit tests.</b> Everything interesting about native input is
/// a property of Windows, not of Nexus: whether a virtual key reaches a focused control, whether a
/// Turkish character survives, whether an injected touch arrives as touch, whether an elevated
/// window is reachable, and — most importantly — whether the security detector can still tell
/// Nexus's own input from a person's. None of that can be proved with a fake. This runs the real
/// thing and reads the result back out of a real window.
/// </para>
/// <para>
/// It is safe to run: it creates its own window, types into its own text box, restores the pointer,
/// and never touches the owner's security state on disk.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeInputSelfTest
{
    private const string Owner = "self-test";

    /// <summary>Text chosen to break a naive keyboard path: Turkish, an emoji, and plain ASCII.</summary>
    private const string UnicodeSample = "Nexus çğışüö ÇĞİŞÜÖ 42 ✓";

    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggerFactory)
    {
        Console.WriteLine();
        Console.WriteLine("Nexus native input self-test");
        Console.WriteLine("============================");
        Console.WriteLine();

        var installer = new NativeInputInstaller(loggerFactory.CreateLogger<NativeInputInstaller>());
        var local = new WindowsInputController(
            new SystemDisplayGeometry(), loggerFactory.CreateLogger<WindowsInputController>());

        using var broker = new BrokerInputChannel(loggerFactory.CreateLogger<BrokerInputChannel>());
        using var router = new NativeInputRouter(
            broker, local, installer, loggerFactory.CreateLogger<NativeInputRouter>());

        var results = new List<(string Name, bool Passed, string Detail)>();

        // --- 1. What is installed, and what is carrying input -------------------------------------
        var installation = installer.GetStatus();
        var status = router.Status;

        Console.WriteLine($"  Task registered : {installation.Registered} (elevated: {installation.Elevated})");
        Console.WriteLine($"  Input path      : {status.Path}  state {status.State}");
        Console.WriteLine($"  Elevated reach  : {status.ReachesElevatedWindows}");
        Console.WriteLine($"  Touch injection : {status.TouchAvailable}");
        Console.WriteLine($"  Real digitizer  : {status.PhysicalDigitizer}");
        Console.WriteLine($"  Input desktop   : {status.Desktop}");
        Console.WriteLine($"  Broker version  : {status.BrokerVersion ?? "(not connected)"}");

        if (status.Diagnostic is { } diagnostic)
        {
            Console.WriteLine($"  Note            : {diagnostic}");
        }

        Console.WriteLine();

        if (status.Desktop != "DEFAULT")
        {
            // Refusing to run is the correct behaviour, not a limitation of the test: the entire
            // point of the Secure Desktop gate is that nothing injects while it is up.
            Console.WriteLine("  The Secure Desktop is active. Nothing will be injected. Dismiss the prompt and retry.");
            return 2;
        }

        // --- 2. The elevated-application test, when asked for -------------------------------------
        //
        // Separate, and driven by the owner, because it is the one thing here that cannot be faked:
        // proving that input reaches an elevated window needs an elevated window, and creating one
        // is a decision taken at the owner's own UAC prompt, not by Nexus.
        if (args.Contains("--foreground"))
        {
            return await TestForegroundWindowAsync(loggerFactory, router, status).ConfigureAwait(false);
        }

        // --- 2b. The abrupt-disconnect test ---------------------------------------------------------
        //
        // Deliberately ends the process with a button, a modifier and a touch contact still down,
        // and without calling anything that cleans up. Nothing in Nexus gets a chance to be polite:
        // whatever releases them has to be the disconnect itself. That is the case a crash, a lost
        // network and a force-quit all reduce to, and the only way to test it is to actually do it.
        if (args.Contains("--abandon-held-input"))
        {
            return AbandonHeldInput(loggerFactory, router, displaysFor(loggerFactory));
        }

        // --- 3. The service, wired exactly as the host wires it ------------------------------------
        var displays = new DxgiDesktopStream(loggerFactory);
        var attribution = new NexusInputAttribution(
            new AttributionOptions(), loggerFactory.CreateLogger<NexusInputAttribution>());

        var input = new InputService(
            router, displays, attribution, new InputOptions(), loggerFactory.CreateLogger<InputService>());

        input.Begin(Owner);

        using var surface = new NativeInputSurface();

        if (!surface.Open("Nexus native input self-test — this window closes itself"))
        {
            Console.WriteLine("  FAIL  the test window could not be created");
            return 1;
        }

        await Task.Delay(600).ConfigureAwait(false);

        var display = displays.GetDisplays().FirstOrDefault(d => d.IsPrimary)
            ?? displays.GetDisplays().FirstOrDefault();

        if (display is null)
        {
            Console.WriteLine("  FAIL  no display could be enumerated");
            return 1;
        }

        results.Add(await TestPointerAsync(input, display).ConfigureAwait(false));
        results.Add(await TestButtonsAndScrollAsync(input, surface, display).ConfigureAwait(false));
        results.Add(await TestKeyboardAsync(input, surface, display).ConfigureAwait(false));
        results.Add(await TestUnicodeAsync(input, surface, display).ConfigureAwait(false));
        results.Add(await TestTouchAsync(input, surface, display, status).ConfigureAwait(false));
        results.Add(await TestCleanupAsync(input, router).ConfigureAwait(false));
        results.Add(await TestAttributionAsync(loggerFactory, router, displays, args).ConfigureAwait(false));
        results.Add(await MeasureLatencyAsync(input, router).ConfigureAwait(false));

        input.End(Owner);
        surface.Dispose();

        Console.WriteLine();
        Console.WriteLine("  Summary");
        Console.WriteLine("  -------");

        foreach (var (name, passed, detail) in results)
        {
            Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  {name,-28} {detail}");
        }

        var failed = results.Count(r => !r.Passed);
        Console.WriteLine();
        Console.WriteLine($"  {results.Count - failed}/{results.Count} checks passed.");

        return failed == 0 ? 0 : 1;
    }

    /// <summary>Opens a display catalog. Separated only so the abandon path can have one early.</summary>
    private static IDisplayCatalog displaysFor(ILoggerFactory loggerFactory) => new DxgiDesktopStream(loggerFactory);

    /// <summary>
    /// Presses things and then dies, so that the cleanup path is the only thing left to fix it.
    /// </summary>
    private static int AbandonHeldInput(
        ILoggerFactory loggerFactory,
        NativeInputRouter router,
        IDisplayCatalog displays)
    {
        var attribution = new NexusInputAttribution(
            new AttributionOptions(), loggerFactory.CreateLogger<NexusInputAttribution>());

        var input = new InputService(
            router, displays, attribution, new InputOptions(), loggerFactory.CreateLogger<InputService>());

        input.Begin(Owner);

        var display = displays.GetDisplays().FirstOrDefault();

        if (display is null)
        {
            Console.WriteLine("  no display; nothing to abandon");
            return 1;
        }

        input.Button(Owner, PointerButton.Left, ButtonAction.Down);
        input.Key(Owner, NexusKey.Shift, KeyAction.Down);
        input.Touch(Owner, display.Index, 0, TouchAction.Down, 0.5, 0.5);

        Console.WriteLine("  Left button, Shift and one touch contact are now held.");
        Console.WriteLine("  Ending the process without releasing any of them.");

        // Not Environment.Exit, which would run finalizers and dispose the channel politely.
        // Environment.FailFast is the closest thing to being killed that a process can do to itself.
        Console.Out.Flush();
        Environment.FailFast(null);

        return 0;
    }

    /// <summary>
    /// Drives whatever the owner puts in front, and says what happened.
    /// <para>
    /// The elevated-application test, in one command. The owner opens an Administrator window —
    /// an elevated PowerShell, an Administrator Command Prompt — focuses it, and this reports the
    /// window's integrity level and whether pointer and keyboard input actually reached it. With
    /// the Nexus Input Broker installed and elevated, a HIGH-integrity window should accept input;
    /// without it, Windows refuses and this says so rather than pretending.
    /// </para>
    /// </summary>
    private static async Task<int> TestForegroundWindowAsync(
        ILoggerFactory loggerFactory,
        NativeInputRouter router,
        NativeInputStatus status)
    {
        Console.WriteLine("  Focus the window you want to test. Reading it in 8 seconds...");
        Console.WriteLine();

        for (var second = 8; second > 0; second--)
        {
            Console.Write($"\r  {second}... ");
            await Task.Delay(1000).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine();

        var window = ForegroundWindowProbe.Describe();
        Console.WriteLine($"  Foreground : {ForegroundWindowProbe.Summarise(window)}");

        var displays = new DxgiDesktopStream(loggerFactory);
        var attribution = new NexusInputAttribution(
            new AttributionOptions(), loggerFactory.CreateLogger<NexusInputAttribution>());

        var input = new InputService(
            router, displays, attribution, new InputOptions(), loggerFactory.CreateLogger<InputService>());

        input.Begin(Owner);

        try
        {
            // A pointer move and a harmless key. Deliberately not a click: clicking something in an
            // Administrator window that the owner did not choose is not a test, it is an accident.
            // Shift is chosen because pressing and releasing it changes nothing anywhere.
            var moved = input.MoveRelative(Owner, 6, 4);
            await Task.Delay(60).ConfigureAwait(false);
            input.MoveRelative(Owner, -6, -4);

            var keyed = input.Key(Owner, NexusKey.Shift, KeyAction.Press);
            await Task.Delay(60).ConfigureAwait(false);

            Console.WriteLine($"  Pointer    : {Describe(moved)}");
            Console.WriteLine($"  Keyboard   : {Describe(keyed)}");
            Console.WriteLine();

            var reached = moved.Succeeded && keyed.Succeeded;

            if (window.Elevated && reached)
            {
                Console.WriteLine("  PASS  input reached an elevated window.");
                return 0;
            }

            if (window.Elevated)
            {
                Console.WriteLine(
                    "  FAIL  the window is elevated and input did not reach it. "
                    + $"Native input is {status.State}; run --install-native-input from an elevated terminal.");

                return 1;
            }

            Console.WriteLine(
                reached
                    ? "  The focused window is not elevated, so this proves nothing about elevated windows. "
                      + "Focus an Administrator window and run this again."
                    : "  Input did not reach even an unelevated window, which is a different problem.");

            return reached ? 0 : 1;
        }
        finally
        {
            input.End(Owner);
        }
    }

    private static string Describe(InputResult result) =>
        result.Diagnostic is null ? result.Delivery.ToString() : $"{result.Delivery} — {result.Diagnostic}";

    /// <summary>The pointer actually moved, and went back where it started.</summary>
    private static async Task<(string, bool, string)> TestPointerAsync(IInputService input, DisplayInfo display)
    {
        if (!WindowsInputController.TryGetPointerPosition(out var startX, out var startY))
        {
            return ("pointer move", false, "the cursor position could not be read");
        }

        input.MoveRelative(Owner, 40, 24);
        await Task.Delay(80).ConfigureAwait(false);
        WindowsInputController.TryGetPointerPosition(out var movedX, out var movedY);

        // Direction only. Windows applies pointer ballistics to relative movement, so forty pixels
        // asked for is not forty pixels delivered — measured here as roughly 1.8x on this machine's
        // mouse settings. That is the correct behaviour for a trackpad and the reason Touchpad mode
        // feels like the PC's own: the acceleration curve the owner already tuned still applies.
        var movedRight = movedX > startX && movedY > startY;

        // Absolute placement, by contrast, must be exact — Direct mode depends on it landing where
        // the finger was, and absolute moves bypass the acceleration curve entirely.
        var target = (
            X: display.Left + (display.Width / 2),
            Y: display.Top + (display.Height / 2));

        var (u, v) = Normalise(display, target.X, target.Y);
        input.MoveAbsolute(Owner, display.Index, u, v);
        await Task.Delay(80).ConfigureAwait(false);
        WindowsInputController.TryGetPointerPosition(out var absoluteX, out var absoluteY);

        var landed = Math.Abs(absoluteX - target.X) <= 2 && Math.Abs(absoluteY - target.Y) <= 2;

        // Put the pointer back where the owner left it.
        var (backU, backV) = Normalise(display, startX, startY);
        input.MoveAbsolute(Owner, display.Index, backU, backV);
        await Task.Delay(80).ConfigureAwait(false);
        WindowsInputController.TryGetPointerPosition(out var backX, out var backY);

        var restored = Math.Abs(backX - startX) <= 2 && Math.Abs(backY - startY) <= 2;

        return (
            "pointer move",
            movedRight && landed && restored,
            $"relative ({startX},{startY})->({movedX},{movedY}); "
            + $"absolute wanted ({target.X},{target.Y}) got ({absoluteX},{absoluteY}); "
            + $"restored ({backX},{backY})");
    }

    /// <summary>A click and a scroll reached a real window, carrying Nexus's own tag.</summary>
    private static async Task<(string, bool, string)> TestButtonsAndScrollAsync(
        IInputService input,
        NativeInputSurface surface,
        DisplayInfo display)
    {
        surface.FocusEdit();
        surface.Clear();

        var (x, y) = surface.TargetSpot();
        var (u, v) = Normalise(display, x, y);

        input.MoveAbsolute(Owner, display.Index, u, v);
        await Task.Delay(60).ConfigureAwait(false);

        input.Button(Owner, PointerButton.Left, ButtonAction.Click);
        await Task.Delay(120).ConfigureAwait(false);

        input.Scroll(Owner, 0, -2);
        await Task.Delay(120).ConfigureAwait(false);

        var messages = surface.Messages;
        var clicked = messages.Any(m => m.StartsWith("WM_LBUTTONDOWN", StringComparison.Ordinal));
        var tagged = messages.Any(m => m.Contains("nexus-tag", StringComparison.Ordinal));
        var scrolled = messages.Any(m => m.StartsWith("WM_MOUSEWHEEL", StringComparison.Ordinal));

        return (
            "click and scroll",
            clicked && scrolled && tagged,
            $"{messages.Count} message(s), click {clicked}, wheel {scrolled}, nexus tag {tagged}");
    }

    /// <summary>Named keys reach a focused control: typed A, then erased it with Backspace.</summary>
    private static async Task<(string, bool, string)> TestKeyboardAsync(
        IInputService input,
        NativeInputSurface surface,
        DisplayInfo display)
    {
        await FocusTextBoxAsync(input, surface, display).ConfigureAwait(false);
        surface.ClearEdit();

        input.Key(Owner, NexusKey.A, KeyAction.Press);
        input.Key(Owner, NexusKey.B, KeyAction.Press);
        await Task.Delay(150).ConfigureAwait(false);

        var typed = surface.ReadEdit();

        input.Key(Owner, NexusKey.Backspace, KeyAction.Press);
        await Task.Delay(150).ConfigureAwait(false);

        var afterBackspace = surface.ReadEdit();

        // Shift is held around a key rather than pressed alone, which is how the phone sends a
        // latched modifier — and the case where a stuck modifier would be most obvious.
        surface.ClearEdit();
        input.Key(Owner, NexusKey.Shift, KeyAction.Down);
        input.Key(Owner, NexusKey.C, KeyAction.Press);
        input.Key(Owner, NexusKey.Shift, KeyAction.Up);
        await Task.Delay(150).ConfigureAwait(false);

        var shifted = surface.ReadEdit();

        var ok = typed.Equals("ab", StringComparison.Ordinal)
            && afterBackspace.Equals("a", StringComparison.Ordinal)
            && shifted.Equals("C", StringComparison.Ordinal);

        return ("keyboard and modifiers", ok, $"typed '{typed}', backspace '{afterBackspace}', shift '{shifted}'");
    }

    /// <summary>The one the owner would notice first: Turkish text arriving as Turkish text.</summary>
    private static async Task<(string, bool, string)> TestUnicodeAsync(
        IInputService input,
        NativeInputSurface surface,
        DisplayInfo display)
    {
        await FocusTextBoxAsync(input, surface, display).ConfigureAwait(false);
        surface.ClearEdit();

        input.Text(Owner, UnicodeSample);
        await Task.Delay(400).ConfigureAwait(false);

        var read = surface.ReadEdit();
        var ok = read.Equals(UnicodeSample, StringComparison.Ordinal);

        return ("unicode text", ok, ok ? $"'{read}'" : $"expected '{UnicodeSample}', got '{read}'");
    }

    /// <summary>
    /// Direct mode: a touch tap and a touch drag, verified as touch messages and as the mouse
    /// messages Windows promotes them into for applications that do not handle touch.
    /// </summary>
    private static async Task<(string, bool, string)> TestTouchAsync(
        IInputService input,
        NativeInputSurface surface,
        DisplayInfo display,
        NativeInputStatus status)
    {
        surface.FocusEdit();
        surface.Clear();

        var (x, y) = surface.TargetSpot();

        var (u, v) = Normalise(display, x, y);
        var down = input.Touch(Owner, display.Index, 0, TouchAction.Down, u, v);
        await Task.Delay(50).ConfigureAwait(false);
        var move = input.Touch(Owner, display.Index, 0, TouchAction.Move, u, v);
        await Task.Delay(50).ConfigureAwait(false);
        var up = input.Touch(Owner, display.Index, 0, TouchAction.Up, u, v);
        await Task.Delay(200).ConfigureAwait(false);

        var delivery = $"down {down.Delivery}"
            + (down.Diagnostic is null ? string.Empty : $" ({down.Diagnostic})")
            + $", move {move.Delivery}, up {up.Delivery}";

        var tapMessages = surface.Messages;
        surface.Clear();

        input.Touch(Owner, display.Index, 0, TouchAction.Down, u, v);

        for (var step = 1; step <= 6; step++)
        {
            var (du, dv) = Normalise(display, x + (step * 8), y + (step * 4));
            input.Touch(Owner, display.Index, 0, TouchAction.Move, du, dv);
            await Task.Delay(20).ConfigureAwait(false);
        }

        var (endU, endV) = Normalise(display, x + 48, y + 24);
        input.Touch(Owner, display.Index, 0, TouchAction.Up, endU, endV);
        await Task.Delay(250).ConfigureAwait(false);

        var dragMessages = surface.Messages;

        var sawPointer = tapMessages.Any(m => m.StartsWith("WM_POINTER", StringComparison.Ordinal));
        var sawPromoted = tapMessages.Any(m => m.Contains("pen-or-touch", StringComparison.Ordinal));
        var sawDrag = dragMessages.Count(m => m.StartsWith("WM_MOUSEMOVE", StringComparison.Ordinal)) > 1
            || dragMessages.Count(m => m.StartsWith("WM_POINTERUPDATE", StringComparison.Ordinal)) > 1;

        if (!status.TouchAvailable)
        {
            // The fallback is a supported outcome, not a failure: the contact becomes a pointer and
            // a button, and Direct mode still works.
            var fellBack = tapMessages.Any(m => m.Contains("nexus-tag", StringComparison.Ordinal));
            return ("touch (pointer fallback)", fellBack, $"{tapMessages.Count} message(s) from the tap");
        }

        return (
            "touch tap and drag",
            sawPointer && sawPromoted && sawDrag,
            $"tap: pointer {sawPointer}, promoted {sawPromoted}; drag moved {sawDrag}; {delivery}");
    }

    /// <summary>
    /// Everything held gets released, whatever the caller forgets.
    /// <para>
    /// A button, a modifier and a touch contact are deliberately left down, then the session is
    /// released as a dropped connection would release it. Nothing may be left pressed.
    /// </para>
    /// </summary>
    private static async Task<(string, bool, string)> TestCleanupAsync(IInputService input, NativeInputRouter router)
    {
        input.Button(Owner, PointerButton.Left, ButtonAction.Down);
        input.Key(Owner, NexusKey.Shift, KeyAction.Down);
        await Task.Delay(60).ConfigureAwait(false);

        var released = input.ReleaseAll(Owner);
        await Task.Delay(80).ConfigureAwait(false);

        // Asked a second time: a correct release leaves nothing behind, so the second call must find
        // nothing to do. This is what a stuck button would show up as.
        var second = router.ReleaseAllHeld();

        return ("release on disconnect", released >= 2 && second == 0, $"released {released}, residue {second}");
    }

    /// <summary>
    /// The mandatory regression: Nexus's own input must not raise a security session while ARMED,
    /// and a person's must.
    /// <para>
    /// Run against the real hooks, in a sandboxed state directory, so the owner's own security state
    /// and incident history are untouched. The second half needs a hand on the mouse, so it only
    /// runs when asked for with <c>--with-local-input</c>.
    /// </para>
    /// </summary>
    private static async Task<(string, bool, string)> TestAttributionAsync(
        ILoggerFactory loggerFactory,
        NativeInputRouter router,
        IDisplayCatalog displays,
        string[] args)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"nexus-attribution-{Guid.NewGuid():N}");
        var directory = new StateDirectory(sandbox);
        directory.Ensure();

        try
        {
            var events = new EventBus(loggerFactory.CreateLogger<EventBus>());
            var state = new SecurityStateService(
                events, directory, loggerFactory.CreateLogger<SecurityStateService>());

            var attribution = new NexusInputAttribution(
                new AttributionOptions(), loggerFactory.CreateLogger<NexusInputAttribution>());

            using var detector = new WindowsActivityDetector(
                loggerFactory.CreateLogger<WindowsActivityDetector>());

            using var monitor = new SecurityActivityMonitor(
                detector,
                attribution,
                state,
                events,
                new DetectionOptions { GraceSeconds = 0, CoalesceMilliseconds = 0 },
                loggerFactory.CreateLogger<SecurityActivityMonitor>());

            var incidents = 0;
            monitor.IncidentDetected += (_, _) => Interlocked.Increment(ref incidents);
            monitor.Start();

            if (!detector.IsRunning)
            {
                return ("security attribution", false, $"the detector did not start: {detector.Diagnostic}");
            }

            state.RequestTransition(SecurityState.Armed, "selftest.native-input");
            await Task.Delay(250).ConfigureAwait(false);

            var input = new InputService(
                router, displays, attribution, new InputOptions(), loggerFactory.CreateLogger<InputService>());

            input.Begin(Owner);

            // A generous amount of Nexus input: far more than the movement threshold, plus keys.
            for (var i = 0; i < 24; i++)
            {
                input.MoveRelative(Owner, i % 2 == 0 ? 12 : -12, i % 2 == 0 ? 8 : -8);
                await Task.Delay(12).ConfigureAwait(false);
            }

            input.Key(Owner, NexusKey.Shift, KeyAction.Press);
            input.Key(Owner, NexusKey.Control, KeyAction.Press);
            await Task.Delay(400).ConfigureAwait(false);

            input.End(Owner);

            var raisedByNexus = Volatile.Read(ref incidents);
            var stillArmed = state.Current.State == SecurityState.Armed;

            var detail =
                $"nexus input ignored {monitor.IgnoredNexusInput}, claimed by ledger {attribution.Claimed}, "
                + $"incidents {raisedByNexus}, state {state.Current.State}";

            if (!args.Contains("--with-local-input"))
            {
                return ("security attribution (nexus only)", raisedByNexus == 0 && stillArmed, detail);
            }

            Console.WriteLine();
            Console.WriteLine("  Now move the mouse or press a key yourself. Waiting 15 seconds...");

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);

            while (DateTimeOffset.UtcNow < deadline && Volatile.Read(ref incidents) == 0)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }

            var raisedByPerson = Volatile.Read(ref incidents) > 0;

            return (
                "security attribution (both)",
                raisedByNexus == 0 && stillArmed && raisedByPerson,
                detail + $", local input raised an incident: {raisedByPerson}");
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (IOException)
            {
                // A leftover sandbox in the temp directory is not worth failing the run over.
            }
        }
    }

    /// <summary>
    /// How long one event costs from the capability layer to Windows accepting it.
    /// <para>
    /// The number that matters is the difference between the two paths: whatever the broker adds is
    /// what the owner would feel as extra lag on every pointer movement.
    /// </para>
    /// </summary>
    private static async Task<(string, bool, string)> MeasureLatencyAsync(IInputService input, NativeInputRouter router)
    {
        const int Iterations = 300;

        // Warm the path so the first connection and the first JIT do not land in the measurement.
        for (var i = 0; i < 20; i++)
        {
            input.MoveRelative(Owner, 1, 0);
            input.MoveRelative(Owner, -1, 0);
        }

        await Task.Delay(50).ConfigureAwait(false);

        var samples = new double[Iterations];
        var stopwatch = new Stopwatch();

        for (var i = 0; i < Iterations; i++)
        {
            stopwatch.Restart();
            input.MoveRelative(Owner, i % 2 == 0 ? 1 : -1, 0);
            stopwatch.Stop();

            samples[i] = stopwatch.Elapsed.TotalMicroseconds;
        }

        Array.Sort(samples);

        var median = samples[Iterations / 2];
        var p95 = samples[(int)(Iterations * 0.95)];
        var path = router.Status.Path;

        // Anything under a millisecond is inaudible next to the network hop the event already made.
        return (
            "latency",
            median < 2000,
            $"{path}: median {median:F0} us, p95 {p95:F0} us over {Iterations} moves");
    }

    private static async Task FocusTextBoxAsync(
        IInputService input,
        NativeInputSurface surface,
        DisplayInfo display)
    {
        surface.FocusEdit();

        var (x, y) = surface.EditCentre();
        var (u, v) = Normalise(display, x, y);

        input.MoveAbsolute(Owner, display.Index, u, v);
        await Task.Delay(50).ConfigureAwait(false);
        input.Button(Owner, PointerButton.Left, ButtonAction.Click);
        await Task.Delay(150).ConfigureAwait(false);
    }

    /// <summary>Screen pixels to the 0-1 position on a display that the protocol speaks in.</summary>
    private static (double X, double Y) Normalise(DisplayInfo display, int x, int y) => (
        Math.Clamp((x - display.Left) / (double)Math.Max(1, display.Width - 1), 0, 1),
        Math.Clamp((y - display.Top) / (double)Math.Max(1, display.Height - 1), 0, 1));
}
