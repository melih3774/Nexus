using System.ComponentModel;
using System.Diagnostics;

using PcCore.Update;

namespace PcCore.Updater;

/// <summary>
/// Finds, asks to close, and starts PC Assistant.
/// <para>
/// <b>Asked, never ended.</b> The stop event is named after the installed executable, and only that
/// copy waits on it. PC Assistant closes through its normal exit - the Nexus bridge first, then the
/// approval cards, then its database - or refuses because a task is running or an approval is waiting.
/// Either way this only waits: an update is worth less than the owner's task, so a PC Assistant that
/// stays open ends the update with nothing changed rather than being killed.
/// </para>
/// </summary>
internal sealed class WindowsAssistantControl(Action<string> log) : IAssistantProcessControl
{
    public IReadOnlyList<RunningPcCore> FindRunning()
    {
        var found = new List<RunningPcCore>();

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AssistantInstall.ExecutableName)))
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

    public async Task<bool> AskToCloseAsync(RunningPcCore target, string executable, TimeSpan grace, CancellationToken cancellationToken)
    {
        Process process;

        try
        {
            process = Process.GetProcessById(target.ProcessId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            if (!EventWaitHandle.TryOpenExisting(AssistantInstall.StopEventName(executable), out var stop))
            {
                // A copy that does not listen cannot be asked. Closing its window would work, but it
                // would also end a running task without asking, which is the one thing this avoids.
                log("PC Assistant is not listening for update requests.");
                return false;
            }

            using (stop)
            {
                stop.Set();
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(grace);

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
    }

    public int Start(string executable)
    {
        // Shell-executed like the Start Menu, so it inherits neither this process's console nor its
        // handles, and runs unelevated as the owner, the only way PC Assistant is meant to run.
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        }) ?? throw new InvalidOperationException("Windows did not start PC Assistant.");

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
}
