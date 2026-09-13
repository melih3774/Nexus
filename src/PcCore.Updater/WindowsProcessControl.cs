using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using PcCore.Update;

namespace PcCore.Updater;

/// <summary>
/// Finds, stops and starts PC Core.
/// <para>
/// <b>Stopping, in order of preference.</b> The stop event PC Core waits on, which runs its normal
/// shutdown: HID devices neutralised, sensors released, the tray icon removed. Then Ctrl+C on its
/// console, which a PC Core too old to know the event still handles the same way. Only if neither
/// ends it within the grace period is the process killed.
/// </para>
/// </summary>
internal sealed class WindowsProcessControl(Action<string> log) : INexusProcessControl
{
    private const uint CtrlCEvent = 0;

    public IReadOnlyList<RunningPcCore> FindRunning()
    {
        var found = new List<RunningPcCore>();

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UpdatePaths.HostExecutableName)))
        {
            using (process)
            {
                string? path = null;

                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Exited meanwhile, or not ours to inspect.
                }

                found.Add(new RunningPcCore(process.Id, path));
            }
        }

        return found;
    }

    public async Task StopAsync(RunningPcCore target, TimeSpan grace, CancellationToken cancellationToken)
    {
        Process process;

        try
        {
            process = Process.GetProcessById(target.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            if (EventWaitHandle.TryOpenExisting(UpdatePaths.StopEventName, out var stop))
            {
                using (stop)
                {
                    log("Asked PC Core to stop.");
                    stop.Set();
                }
            }
            else if (SendCtrlC(target.ProcessId))
            {
                log("PC Core predates the stop event; sent it Ctrl+C.");
            }

            if (await WaitForExitAsync(process, grace, cancellationToken).ConfigureAwait(false))
            {
                log("PC Core stopped.");
                return;
            }

            log("PC Core did not stop in time; ending it.");

            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
            }

            await WaitForExitAsync(process, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    public int Start(string executable)
    {
        // Shell-executed, so it inherits neither this process's console nor its handles. It does inherit
        // a packaged app's AppData redirection, when there is one - which is why Program refuses to run
        // where that would give PC Core a different copy of its state than the Start Menu gives it.
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        }) ?? throw new InvalidOperationException("Windows did not start PC Core.");

        return process.Id;
    }

    public bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            process.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    /// <summary>
    /// Ctrl+C delivered to another process's console: attach to it, ignore the event ourselves, raise
    /// it, detach. The updater has no console of its own when started from the tray, so attaching
    /// cannot fail for that reason.
    /// </summary>
    private static bool SendCtrlC(int processId)
    {
        FreeConsole();

        if (!AttachConsole((uint)processId))
        {
            return false;
        }

        try
        {
            SetConsoleCtrlHandler(nint.Zero, true);
            return GenerateConsoleCtrlEvent(CtrlCEvent, 0);
        }
        finally
        {
            // Let the event reach the other process before this one stops ignoring it; otherwise the
            // same Ctrl+C ends the updater half way through its job.
            Thread.Sleep(500);
            FreeConsole();
            SetConsoleCtrlHandler(nint.Zero, false);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
}
