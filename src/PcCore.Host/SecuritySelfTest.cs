using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Security.Evidence;
using PcCore.Capabilities.Security.Sessions;
using PcCore.Capabilities.Storage;
using PcCore.Protocol;
using PcCore.Windows;
using PcCore.Windows.Input;

namespace PcCore.Host;

/// <summary>
/// Exercises the security workflow against this actual machine.
/// <para>
/// Three things here cannot be tested with fakes, and they are the three that matter most. Whether
/// the real low-level hooks actually install; whether input Nexus injects is really attributed to
/// Nexus rather than to a person; and whether this PC's camera, microphone and Windows Hello
/// genuinely work. Everything else — the state gate, the noise filter, retention — is ordinary logic
/// with unit tests.
/// </para>
/// <para>
/// It uses a throwaway state directory, so it never touches the owner's real incident history, and
/// it restores the security state it found.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public static class SecuritySelfTest
{
    public static async Task<int> RunAsync(string[] args, ILoggerFactory loggerFactory)
    {
        var failures = 0;

        void Step(string what, bool ok, string? detail = null)
        {
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {what}{(detail is null ? "" : $"  [{detail}]")}");
        }

        Console.WriteLine();
        Console.WriteLine("  Nexus security workflow self-test");
        Console.WriteLine();

        var sandbox = Path.Combine(Path.GetTempPath(), "nexus-security-selftest", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(sandbox);
        Console.WriteLine($"  Sandbox: {sandbox}");
        Console.WriteLine();

        var directory = new StateDirectory(sandbox);
        directory.Ensure();

        var events = new EventBus(loggerFactory.CreateLogger<EventBus>());
        var stateService = new SecurityStateService(
            events, directory, loggerFactory.CreateLogger<SecurityStateService>());

        var configuration = new SecurityConfigurationStore(
            directory, loggerFactory.CreateLogger<SecurityConfigurationStore>());

        var store = new SecuritySessionStore(
            directory, new SecurityStorageOptions(), loggerFactory.CreateLogger<SecuritySessionStore>());

        var detector = new WindowsActivityDetector(loggerFactory.CreateLogger<WindowsActivityDetector>());

        // The real ledger, so the self-test exercises the same attribution the running host uses.
        var attribution = new NexusInputAttribution(
            new AttributionOptions(), loggerFactory.CreateLogger<NexusInputAttribution>());

        var monitor = new SecurityActivityMonitor(
            detector,
            attribution,
            stateService,
            events,
            new DetectionOptions { GraceSeconds = 0 },
            loggerFactory.CreateLogger<SecurityActivityMonitor>());

        var screen = new WindowsScreenEvidence(
            new GdiScreenCapture(new SystemDisplayGeometry(), loggerFactory.CreateLogger<GdiScreenCapture>()),
            loggerFactory.CreateLogger<WindowsScreenEvidence>());

        var camera = new WindowsCameraEvidence(configuration, loggerFactory.CreateLogger<WindowsCameraEvidence>());
        var audio = new WindowsAudioEvidence(configuration, loggerFactory.CreateLogger<WindowsAudioEvidence>());
        var hello = new WindowsHelloVerifier(loggerFactory.CreateLogger<WindowsHelloVerifier>());

        await using var incidents = new SecurityIncidentService(
            store,
            stateService,
            monitor,
            configuration,
            screen,
            camera,
            audio,
            events,
            new EvidenceOptions { CameraClipSeconds = 3, AudioClipSeconds = 3 },
            loggerFactory.CreateLogger<SecurityIncidentService>());

        try
        {
            // --- Detection ---------------------------------------------------------------------
            DpiAwareness.TryEnablePerMonitorV2();
            monitor.Start();

            await Task.Delay(500).ConfigureAwait(false);
            Step("install real low-level input hooks", detector.IsRunning, detector.Diagnostic);

            // --- OWNER_PRESENT records nothing --------------------------------------------------
            Step(
                "state starts at OWNER_PRESENT",
                stateService.Current.State == SecurityState.OwnerPresent,
                stateService.Current.State.ToString());

            var controller = new WindowsInputController(
                new SystemDisplayGeometry(), loggerFactory.CreateLogger<WindowsInputController>());

            await InjectAsync(controller).ConfigureAwait(false);

            Step(
                "no session is created while OWNER_PRESENT",
                store.All().Count == 0 && stateService.Current.State == SecurityState.OwnerPresent);

            var evidenceRoot = Path.Combine(sandbox, "Security", "evidence");

            Step(
                "no evidence is written while OWNER_PRESENT",
                CountFiles(evidenceRoot) == 0,
                $"{CountFiles(evidenceRoot)} evidence files");

            // --- Injected input must not look like a person -------------------------------------
            stateService.RequestTransition(SecurityState.Armed, "selftest").EnsureAccepted();
            Step("armed", stateService.Current.State == SecurityState.Armed);

            var seen = new List<LocalActivity>();
            void Tally(LocalActivity a) => seen.Add(a);
            detector.Observed += Tally;

            var before = monitor.IgnoredNexusInput;
            await InjectAsync(controller).ConfigureAwait(false);
            await Task.Delay(400).ConfigureAwait(false);

            detector.Observed -= Tally;

            var pointer = seen.Count(a => a.Kind == ActivityKind.PointerActivity);
            var keyboard = seen.Count(a => a.Kind == ActivityKind.KeyboardActivity);
            var mistaken = seen.Count(a => a.Source == ActivitySource.Local);

            Console.WriteLine(
                $"          hooks observed {pointer} pointer + {keyboard} keyboard event(s) from injection");
            Console.WriteLine(
                $"          attribution: {seen.Count(a => a.Source == ActivitySource.NexusRemote)} nexus, "
                + $"{seen.Count(a => a.Source == ActivitySource.Synthetic)} synthetic, "
                + $"{seen.Count(a => a.Source == ActivitySource.Local)} local");

            Step(
                "the mouse hook actually sees injected movement",
                pointer > 0,
                $"{pointer} pointer events");

            Step(
                "not one injected event was mistaken for a person",
                mistaken == 0,
                $"{mistaken} misattributed");

            var attributed = monitor.IgnoredNexusInput - before;

            // The decisive property of this milestone. Nexus injected real input into the real
            // desktop, the real hooks saw it, and every event carried Nexus's own signature.
            Step(
                "Nexus remote input is attributed to Nexus, not to a person",
                attributed > 0,
                $"{attributed} events tagged");

            Step(
                "Nexus remote input while ARMED raises no incident",
                store.All().Count == 0 && stateService.Current.State == SecurityState.Armed,
                stateService.Current.State.ToString());

            // --- A real incident, with real evidence --------------------------------------------
            // Raised directly rather than by moving the mouse, because the operator is not here to
            // move it. The detector path above is what proves the hooks and the attribution work.
            var session = OpenIncidentDirectly(stateService, store, incidents);

            Step("ARMED -> SECURITY_SESSION opens exactly one session",
                store.All().Count == 1 && stateService.Current.State == SecurityState.SecuritySession);

            var folder = store.EvidenceFolder(session.SessionId);

            var shot = await screen.CaptureStillAsync(folder, default).ConfigureAwait(false);
            Step("real screen evidence", shot is not null, Describe(folder, shot));

            var cameras = await camera.ListDevicesAsync(default).ConfigureAwait(false);
            Console.WriteLine($"          cameras: {(cameras.Count == 0 ? "none" : string.Join(", ", cameras.Select(c => c.Name)))}");

            if (cameras.Count > 0)
            {
                var still = await camera.CaptureStillAsync(folder, default).ConfigureAwait(false);
                Step("real camera image", still is not null, Describe(folder, still));

                var clip = await camera.CaptureClipAsync(folder, TimeSpan.FromSeconds(3), default)
                    .ConfigureAwait(false);

                Step("real camera clip", clip is not null, Describe(folder, clip));
            }
            else
            {
                Console.WriteLine("  SKIP  camera evidence: no camera on this machine");
            }

            var microphones = await audio.ListDevicesAsync(default).ConfigureAwait(false);
            Console.WriteLine($"          microphones: {(microphones.Count == 0 ? "none" : string.Join(", ", microphones.Select(m => m.Name)))}");

            if (microphones.Count > 0)
            {
                var clip = await audio.CaptureClipAsync(folder, TimeSpan.FromSeconds(3), default)
                    .ConfigureAwait(false);

                Step("real microphone clip", clip is not null, Describe(folder, clip));
            }
            else
            {
                Console.WriteLine("  SKIP  microphone evidence: none on this machine");
            }

            // --- Integrity ------------------------------------------------------------------------
            var files = Directory.GetFiles(folder);
            var digestsMatch = files.All(f =>
                SecuritySessionStore.ComputeDigest(f).Length == 64
                && SecuritySessionStore.ComputeDigest(f) == SecuritySessionStore.ComputeDigest(f));

            Step("every evidence file hashes to a stable SHA-256", files.Length > 0 && digestsMatch,
                $"{files.Length} files");

            // A modified file must hash differently, which is what the digest is for.
            if (files.Length > 0)
            {
                var target = files[0];
                var original = SecuritySessionStore.ComputeDigest(target);
                await File.AppendAllTextAsync(target, "tampered").ConfigureAwait(false);

                Step(
                    "a modified evidence file no longer matches its digest",
                    SecuritySessionStore.ComputeDigest(target) != original);
            }

            // --- Resolution -------------------------------------------------------------------------
            var notMe = incidents.ConfirmNotOwner("LOCAL");
            Step(
                "NOT ME sets high alert and keeps the state at SECURITY_SESSION",
                notMe is { HighAlert: true, Resolution: SessionResolution.ConfirmedNotOwner }
                    && stateService.Current.State == SecurityState.SecuritySession);

            var acknowledged = incidents.AcknowledgeAsOwner("LOCAL");
            Step(
                "THIS WAS ME returns the state to OWNER_PRESENT",
                acknowledged?.Resolution == SessionResolution.AcknowledgedOwner
                    && stateService.Current.State == SecurityState.OwnerPresent);

            Step(
                "evidence survives acknowledgement",
                acknowledged is not null && Directory.GetFiles(folder).Length > 0,
                $"{Directory.GetFiles(folder).Length} files kept");

            // --- Windows Hello --------------------------------------------------------------------
            var available = await hello.IsAvailableAsync(default).ConfigureAwait(false);
            Step("Windows Hello availability reported", true, available ? "available" : "not configured");

            if (args.Contains("--prompt-hello") && available)
            {
                Console.WriteLine();
                Console.WriteLine("  A Windows Hello prompt should now appear. Approve or cancel it.");

                var result = await hello.VerifyAsync("Nexus security self-test", default).ConfigureAwait(false);
                Console.WriteLine($"          Windows Hello returned: {result}");
            }
            else if (available)
            {
                Console.WriteLine("          (run with --prompt-hello to exercise the real prompt)");
            }
        }
        finally
        {
            monitor.Dispose();

            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"  (sandbox left at {sandbox})");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "  All security self-test steps passed." : $"  {failures} step(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Injects a burst of real input, exactly as the phone would.
    /// <para>
    /// Small movements that cancel out, so the pointer ends where it started. Enough of them to be
    /// well past the noise threshold, so "no incident" cannot be explained by the movement being too
    /// small to count.
    /// </para>
    /// </summary>
    private static async Task InjectAsync(WindowsInputController controller)
    {
        for (var i = 0; i < 40; i++)
        {
            controller.MovePointerRelative(i % 2 == 0 ? 3 : -3, i % 2 == 0 ? 3 : -3);
        }

        controller.Key(PcCore.Capabilities.Input.NexusKey.Shift, down: true);
        controller.Key(PcCore.Capabilities.Input.NexusKey.Shift, down: false);

        await Task.Delay(300).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens an incident without waiting for a person to arrive.
    /// <para>
    /// The detector cannot be made to report <em>local</em> activity synthetically — anything this
    /// process injects is correctly attributed to Nexus, which is the property being tested a few
    /// lines above. So the incident is opened through the same service path a real detection uses.
    /// </para>
    /// </summary>
    private static SecuritySession OpenIncidentDirectly(
        SecurityStateService state,
        SecuritySessionStore store,
        SecurityIncidentService incidents)
    {
        state.RequestTransition(SecurityState.SecuritySession, "selftest.simulated-detection").EnsureAccepted();

        // Recover() creates the record for a state that says an incident is running, which is
        // exactly the situation here.
        incidents.Recover();

        return store.Active()!;
    }

    private static string Describe(string folder, CapturedEvidence? evidence)
    {
        if (evidence is null)
        {
            return "unavailable";
        }

        var info = new FileInfo(Path.Combine(folder, evidence.FileName));
        return info.Exists ? $"{evidence.ContentType}, {info.Length / 1024} KB" : "file missing";
    }

    private static int CountFiles(string root) =>
        Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length : 0;
}
