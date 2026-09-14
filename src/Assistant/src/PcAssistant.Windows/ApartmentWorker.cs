using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PcAssistant.Windows;

// A single bounded worker prevents hung providers from spawning unbounded threads.
internal sealed class ApartmentWorker : IDisposable
{
    private readonly BlockingCollection<Action> queue = new(16);
    private int disposed;
    public ApartmentWorker(ApartmentState apartment)
    {
        var thread = new Thread(() => { foreach (var action in queue.GetConsumingEnumerable()) action(); })
        { IsBackground = true, Name = "PC Assistant native " + apartment };
        thread.SetApartmentState(apartment);
        thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken) => RunAsync(_ => action(), cancellationToken);

    public async Task<T> RunAsync<T>(Func<CancellationToken, T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryAdd(() =>
        {
            try { token.ThrowIfCancellationRequested(); completion.TrySetResult(action(token)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        })) throw new InvalidOperationException("The native worker is busy; retry after the current operation finishes.");
        return await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) queue.CompleteAdding();
    }
}
