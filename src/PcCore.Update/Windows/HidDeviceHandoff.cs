using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace PcCore.Update.Windows;

/// <summary>
/// The moment, inside one update, when the elevated half is about to replace the HID driver and needs
/// nothing of Nexus's to be holding the device open.
/// <para>
/// <b>Why a handshake rather than an order of steps.</b> PC Core keeps the Nexus control device open
/// while it runs, and an open handle vetoes the device restart that loads a new driver: Windows binds
/// the new package and keeps the old driver running until the PC restarts. The driver is replaced by the
/// elevated half, behind the one UAC prompt, but PC Core may only be stopped and started by the
/// unelevated half - started from an elevated process it would run elevated. Stopping it before the
/// prompt would cost the owner their PC for as long as the prompt waits, and for nothing if it is
/// declined. So the elevated half first does everything that can fail without consequence (copying and
/// hashing the payloads, choosing the rollback), then asks; the unelevated half stops PC Core and
/// answers; only then is the driver touched.
/// </para>
/// <para>
/// <b>Two named events</b>, session-local, under a name chosen fresh for each update and passed on the
/// elevated command line. Whatever could set them already runs as the owner and could stop PC Core
/// itself. A forged "released" only means the driver is installed while PC Core still holds it, which
/// the driver lifecycle detects and reports as restart-required; a forged "requested" stops PC Core,
/// which the updater then starts again.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class HidDeviceHandoff : IDisposable
{
    private readonly EventWaitHandle _requested;
    private readonly EventWaitHandle _released;

    private HidDeviceHandoff(string id, EventWaitHandle requested, EventWaitHandle released)
    {
        Id = id;
        _requested = requested;
        _released = released;
    }

    /// <summary>What the elevated half is given to find this handoff: 32 hex digits.</summary>
    public string Id { get; }

    /// <summary>The unelevated half's end. Fails rather than share events something else created first.</summary>
    public static HidDeviceHandoff Create()
    {
        var id = Guid.NewGuid().ToString("N");
        var requested = new EventWaitHandle(false, EventResetMode.ManualReset, Name(id, "Requested"), out var newRequested);
        var released = new EventWaitHandle(false, EventResetMode.ManualReset, Name(id, "Released"), out var newReleased);

        if (!newRequested || !newReleased)
        {
            requested.Dispose();
            released.Dispose();
            throw new InvalidOperationException("The HID device handoff events already existed.");
        }

        return new HidDeviceHandoff(id, requested, released);
    }

    /// <summary>The elevated half's end; null when <paramref name="id"/> is malformed or its events are gone.</summary>
    public static HidDeviceHandoff? Open(string id)
    {
        if (!HandoffId().IsMatch(id)
            || !EventWaitHandle.TryOpenExisting(Name(id, "Requested"), out var requested))
        {
            return null;
        }

        if (!EventWaitHandle.TryOpenExisting(Name(id, "Released"), out var released))
        {
            requested.Dispose();
            return null;
        }

        return new HidDeviceHandoff(id, requested, released);
    }

    /// <summary>Elevated half: asks for the device and waits until Nexus has let go of it, or until <paramref name="timeout"/>.</summary>
    public bool Request(TimeSpan timeout)
    {
        _requested.Set();
        return _released.WaitOne(timeout);
    }

    /// <summary>Unelevated half: completes when the elevated half asks for the device.</summary>
    public Task WhenRequestedAsync(CancellationToken cancellationToken)
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = ThreadPool.RegisterWaitForSingleObject(_requested, (_, _) => requested.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        var cancel = cancellationToken.Register(() => requested.TrySetCanceled(cancellationToken));

        _ = requested.Task.ContinueWith(
            _ =>
            {
                wait.Unregister(null);
                cancel.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return requested.Task;
    }

    /// <summary>Unelevated half: Nexus no longer holds the device.</summary>
    public void Release() => _released.Set();

    public void Dispose()
    {
        _requested.Dispose();
        _released.Dispose();
    }

    private static string Name(string id, string what) => $@"Local\Nexus.Updater.HidHandoff.{id}.{what}";

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HandoffId();
}
