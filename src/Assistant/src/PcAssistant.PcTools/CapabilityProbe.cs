using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using PcAssistant.Windows;
using PcAssistant.Windows.Capture;

namespace PcAssistant.PcTools;

/// <summary>One capability, what was actually tried, and what happened.</summary>
public sealed record CapabilityResult(string Name, string State, string Detail);

/// <summary>
/// What this machine can really do, measured rather than assumed.
///
/// The panel this feeds used to say "Ready when PC tools are ready" for screen capture, UI
/// Automation, clipboard and the rest — which is a statement about the code shipping, not
/// about the PC. It would have said Ready on a machine where Graphics Capture is unavailable,
/// where the clipboard is held by another process, or where there is no audio endpoint at all.
///
/// So each check here does the smallest real version of the thing: capture a window, read a
/// control tree, read the clipboard, read the audio endpoint. States are deliberately three-
/// valued — Ready, Limited, Unavailable — because "works with a caveat" is the common case and
/// collapsing it into Ready is how a diagnostic starts lying.
///
/// Every check is read-only. Nothing is clicked, typed, opened or changed, so running the
/// panel can never disturb the owner's desktop.
/// </summary>
public static class CapabilityProbe
{
    /// <summary>Argument that switches the tool server into a one-shot self-check.</summary>
    public const string Argument = "--probe";

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<CapabilityResult>();
        var desktop = new DesktopWindows();

        // A window that is not PC Assistant's own, so the probe never photographs an approval card.
        WindowInfo? sample = null;
        try
        {
            sample = desktop.ListWindows(40).FirstOrDefault(w =>
                !w.IsMinimized && w.Bounds.Width > 40 && w.Bounds.Height > 40 &&
                !ProtectedWindows.IsProtectedWindow(w.Hwnd));
        }
        catch (Exception ex) { results.Add(new("Window list", "Unavailable", ex.Message)); }

        results.Add(ProbeCapture(sample, cancellationToken));
        results.Add(await ProbeAutomationAsync(sample, cancellationToken).ConfigureAwait(false));
        results.Add(await ProbeClipboardAsync(cancellationToken).ConfigureAwait(false));
        results.Add(await ProbeAudioAsync(cancellationToken).ConfigureAwait(false));
        results.Add(ProbePower());
        results.Add(ProbeNetwork());
        results.Add(ProbeApplications());
        results.Add(ProbeInput());

        Console.Out.Write(JsonSerializer.Serialize(results));
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static CapabilityResult ProbeCapture(WindowInfo? sample, CancellationToken cancellationToken)
    {
        if (sample is null)
            return new("Window capture", "Limited", "No ordinary window was open to test capture against.");

        try
        {
            var registry = new CaptureRegistry();
            var shot = new WindowCapture(new DesktopWindows(), registry)
                .CaptureWindow(sample.Hwnd, maxEdge: 320, cancellationToken: cancellationToken);

            return shot.Method switch
            {
                CaptureMethod.GraphicsCapture => new("Window capture", "Ready",
                    $"Windows Graphics Capture works: captured '{Short(sample.Title)}' at {shot.ImageWidth}x{shot.ImageHeight}. " +
                    "A window can be observed while other windows cover it."),
                CaptureMethod.PrintWindow => new("Window capture", "Limited",
                    "Graphics Capture is unavailable; windows render themselves through PrintWindow instead. " +
                    "Occlusion is still handled, but hardware-composited content can come back blank."),
                _ => new("Window capture", "Limited",
                    "Only visible desktop pixels can be read on this machine, so a covered window includes whatever covers it."),
            };
        }
        catch (Exception ex) { return new("Window capture", "Unavailable", ex.Message); }
    }

    private static async Task<CapabilityResult> ProbeAutomationAsync(WindowInfo? sample, CancellationToken cancellationToken)
    {
        if (sample is null)
            return new("Windows UI Automation", "Limited", "No ordinary window was open to read a control tree from.");

        try
        {
            using var automation = new UiAutomationService();
            var snapshot = await automation.SnapshotAsync(sample.Hwnd, maxDepth: 1, maxNodes: 5, cancellationToken)
                .ConfigureAwait(false);
            return snapshot.Nodes.Count > 0
                ? new("Windows UI Automation", "Ready",
                    $"Read {snapshot.Nodes.Count} control(s) from '{Short(sample.Title)}'. Individual applications may still " +
                    "expose little or nothing; that is a property of each app, not of this machine.")
                : new("Windows UI Automation", "Limited",
                    "The automation provider answered but exposed no controls for the sampled window.");
        }
        catch (Exception ex) { return new("Windows UI Automation", "Unavailable", ex.Message); }
    }

    private static async Task<CapabilityResult> ProbeClipboardAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var clipboard = new ClipboardService();
            await clipboard.ReadAsync(16, cancellationToken).ConfigureAwait(false);
            return new("Clipboard", "Ready", "The clipboard can be read. Writing happens only when the owner asks.");
        }
        catch (Exception ex)
        {
            return new("Clipboard", "Limited",
                "The clipboard could not be read just now, which usually means another application is holding it: " + ex.Message);
        }
    }

    private static async Task<CapabilityResult> ProbeAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var audio = new AudioService();
            var state = await audio.GetAsync(cancellationToken).ConfigureAwait(false);
            return new("Audio", "Ready",
                $"Default playback device '{state.DeviceName}' at {state.VolumePercent}%{(state.Muted ? ", muted" : "")}.");
        }
        catch (Exception ex) { return new("Audio", "Unavailable", ex.Message); }
    }

    private static CapabilityResult ProbePower()
    {
        try
        {
            var power = SystemControls.Power();
            return new("Power and battery", "Ready",
                power.BatteryPercent is { } percent
                    ? $"{power.PowerSource}, battery {percent}% ({power.BatteryState})."
                    : $"{power.PowerSource}. {power.BatteryState}.");
        }
        catch (Exception ex) { return new("Power and battery", "Unavailable", ex.Message); }
    }

    private static CapabilityResult ProbeNetwork()
    {
        try
        {
            var network = SystemControls.Network();
            var wifi = network.Adapters.FirstOrDefault(a => a.Ssid is not null);
            return new("Network", "Ready",
                $"{network.Adapters.Count} adapter(s); " +
                (network.HasUsableConnection ? "a usable connection is present" : "no adapter has a default gateway") +
                (wifi is null ? "." : $"; Wi-Fi '{wifi.Ssid}'.") +
                (network.VirtualNetworks.Count > 0 ? $" Virtual/tunnel: {string.Join(", ", network.VirtualNetworks.Take(3))}." : ""));
        }
        catch (Exception ex) { return new("Network", "Unavailable", ex.Message); }
    }

    private static CapabilityResult ProbeApplications()
    {
        try
        {
            using var launcher = new AppLauncher();
            var all = launcher.Discover(null, 200);
            var settings = launcher.Discover("bluetooth", 5).Any(a => a.Target == "ms-settings:bluetooth");
            return all.Count > 0
                ? new("Application discovery", "Ready",
                    $"{all.Count}+ launch targets found across Start Menu, App Paths, packaged apps and Windows pages" +
                    (settings ? "; Settings deep links resolve." : "; Settings deep links did not resolve."))
                : new("Application discovery", "Limited", "No launch targets were discovered.");
        }
        catch (Exception ex) { return new("Application discovery", "Unavailable", ex.Message); }
    }

    /// <summary>
    /// Input is the one capability that cannot be probed without using it. Injecting a
    /// keystroke to prove keystrokes work would be exactly the unrequested desktop interference
    /// the rest of this design avoids, so this reports what can be checked and says the rest
    /// plainly rather than claiming Ready.
    /// </summary>
    private static CapabilityResult ProbeInput()
    {
        try
        {
            var blocked = InputService.HeldInputs();

            return blocked.Count == 0
                ? new("Input", "Ready", "No modifier or button is being held. Injection itself is proven only when an " +
                                        "approved action runs; it cannot be tested without actually moving the owner's desktop.")
                : new("Input", "Limited", $"Currently held and would block automated input: {string.Join(", ", blocked)}.");
        }
        catch (Exception ex) { return new("Input", "Unavailable", ex.Message); }
    }

    private static string Short(string title) =>
        string.IsNullOrWhiteSpace(title) ? "untitled" : title.Length <= 40 ? title : title[..39] + "…";
}
