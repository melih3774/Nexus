using System.Runtime.Versioning;

namespace PcCore.Update.Windows;

/// <summary>PC Core's end of the named stop event the updater sets.</summary>
[SupportedOSPlatform("windows")]
public static class UpdateStopSignal
{
    /// <summary>
    /// Runs <paramref name="onStop"/> once, on a thread-pool thread, when the event is set. Disposing
    /// the result stops listening.
    /// </summary>
    /// <exception cref="WaitHandleCannotBeOpenedException">The event could not be created or opened.</exception>
    public static IDisposable Listen(Action onStop, string name = UpdatePaths.StopEventName)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);

        var registration = ThreadPool.RegisterWaitForSingleObject(
            signal,
            (_, _) => onStop(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        return new Listener(signal, registration);
    }

    private sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            // Not Unregister(signal): that argument is a handle to set once unregistering completes, and
            // this one is shared by name. Every PC Core that shut down normally set it, stopping any
            // other PC Core listening - such as one the owner started while this one was still closing.
            registration.Unregister(null);
            signal.Dispose();
        }
    }
}
