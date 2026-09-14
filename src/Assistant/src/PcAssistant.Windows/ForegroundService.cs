using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PcAssistant.Windows;

/// <summary>Outcome of a foreground acquisition. <see cref="Verified"/> is read back from the desktop, never inferred.</summary>
public sealed record ForegroundResult(bool Verified, long Hwnd, long ActualForeground, int Attempts, string Message);

/// <summary>
/// A window could not be brought to the front. Distinct from every other failure so callers can stop
/// instead of spending another owner approval on an attempt that will fail the same way.
/// </summary>
public sealed class ForegroundUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// The one place foreground is taken and proven.
///
/// Windows refuses <c>SetForegroundWindow</c> from a process that does not already own the foreground, and
/// the owner's click on an approval card makes PC Assistant that owner. So the approval reply hands this
/// process the foreground privilege, and every operation needing a window in front acquires it here —
/// inside the same approved operation, immediately before acting, never as a separate step the model has
/// to remember. Nothing trusts what <c>SetForegroundWindow</c> returns; the result is polled back from
/// <c>GetForegroundWindow</c>.
/// </summary>
public static class ForegroundService
{
    /// <summary>Consecutive failures for one window before it stops asking the owner at all.</summary>
    public const int MaxConsecutiveFailures = 2;

    private static readonly int[] BackoffMilliseconds = [30, 60, 120, 240];
    private static readonly ConcurrentDictionary<long, int> Failures = new();

    /// <summary>Brings <paramref name="hwnd"/> to the front and proves it, with bounded retry and backoff.</summary>
    public static async Task<ForegroundResult> EnsureAsync(long hwnd, CancellationToken cancellationToken = default)
    {
        var handle = Native.Window(hwnd);
        using var dpi = new Native.DpiScope();

        if (Native.IsIconic(handle)) Native.ShowWindowAsync(handle, 9); // SW_RESTORE

        // A hidden window can be made the foreground window without ever becoming usable, which would pull
        // the owner's focus into nothing. Nothing to retry here, so it fails immediately.
        if (!Native.IsWindowVisible(handle)) return Fail(hwnd, 0, "it is hidden");

        for (var attempt = 0; attempt <= BackoffMilliseconds.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSettled(handle))
            {
                Failures.TryRemove(hwnd, out _);
                return new ForegroundResult(true, hwnd, hwnd, attempt, $"Window {hwnd} is verified foreground.");
            }
            if (attempt == BackoffMilliseconds.Length) break;
            Activate(handle);
            await Task.Delay(BackoffMilliseconds[attempt], cancellationToken).ConfigureAwait(false);
        }

        return Fail(hwnd, BackoffMilliseconds.Length, "Windows kept the foreground on another application");
    }

    private static ForegroundResult Fail(long hwnd, int attempts, string reason)
    {
        var actual = (long)Native.GetForegroundWindow();
        var failures = Failures.AddOrUpdate(hwnd, 1, static (_, count) => count + 1);
        var message = $"Window {hwnd} could not be brought to the front because {reason} (foreground is {actual}).";
        return new ForegroundResult(false, hwnd, actual, attempts,
            failures >= MaxConsecutiveFailures ? message + " " + StopAdvice : message);
    }

    /// <summary>
    /// True once a window has failed twice in a row. Checked before the owner is asked, so a window Windows
    /// will not activate cannot burn one approval per attempt.
    /// </summary>
    public static bool IsBlocked(long hwnd)
    {
        if (!Failures.TryGetValue(hwnd, out var count) || count < MaxConsecutiveFailures) return false;
        // The owner may have brought it forward themselves in the meantime; that clears the block. It has to
        // be genuinely usable, not merely foreground: a hidden window can hold the foreground and would
        // otherwise clear its own block on every check.
        if (Native.IsWindow((nint)hwnd) && IsSettled((nint)hwnd))
        {
            Failures.TryRemove(hwnd, out _);
            return false;
        }
        return true;
    }

    public static string BlockedMessage(long hwnd) =>
        $"Window {hwnd} already failed to come to the front {MaxConsecutiveFailures} times in a row, so this was not attempted and the owner was not asked again. {StopAdvice}";

    public static int FailureCount(long hwnd) => Failures.TryGetValue(hwnd, out var count) ? count : 0;

    public static void Reset(long hwnd) => Failures.TryRemove(hwnd, out _);

    public static void ResetAll() => Failures.Clear();

    private const string StopAdvice =
        "Stop retrying and stop requesting input approvals: tell the owner which window needs to be in front and wait for them to say it is.";

    private static bool IsSettled(nint handle) =>
        Native.IsWindowVisible(handle) && !Native.IsIconic(handle) && Native.GetForegroundWindow() == handle;

    /// <summary>
    /// Two ways in, kept deliberately: the privilege handover granted at approval time, and attaching to the
    /// current foreground thread's input queue (always detached again). They fail under different Windows
    /// foreground-lock configurations, so both stay.
    ///
    /// What the regression suite actually proves is the atomic acquisition itself — reverting that makes
    /// ApprovedInputReacquiresTheForegroundTheApprovalStoleAndSucceedsOnOneApproval fail. Disabling either
    /// fallback on its own left the test green on the development machine, so neither is demonstrated to be
    /// individually necessary here. Do not read their presence as tested-necessary; read it as defence in
    /// depth against configurations the suite does not reproduce.
    ///
    /// An approval answered from the phone through Nexus has no desktop click behind it, so the handover
    /// always fails and only the input-queue route is left.
    /// RemotelyApprovedInputReachesItsTargetWithoutAnyDesktopClick shows that route alone is enough with an
    /// ordinary application in front. It was not enough while the Quick Settings flyout held the foreground:
    /// the click was refused as a foreground failure, which stops and tells the owner rather than misfiring.
    /// A zero-length injected mouse move ahead of the attempt did not change that and was not kept.
    /// </summary>
    private static void Activate(nint handle)
    {
        var foreground = Native.GetForegroundWindow();
        var ours = Native.GetCurrentThreadId();
        var owner = foreground == 0 ? 0 : Native.GetWindowThreadProcessId(foreground, out _);
        var attached = owner != 0 && owner != ours && Native.AttachThreadInput(ours, owner, true);
        try
        {
            Native.BringWindowToTop(handle);
            Native.SetForegroundWindow(handle);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(ours, owner, false);
        }
    }
}
