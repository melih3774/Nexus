using PcCore.Update.Windows;
using Xunit;

namespace PcCore.Tests.Update;

/// <summary>
/// The stop event is shared by name, so it must mean "the updater asked" and nothing else.
/// <para>
/// It used to be set by every PC Core that shut down normally: a PC Core the owner started while the
/// previous one was still closing stopped itself a few seconds later, logging that the updater had
/// asked it to. Seen on a real restart: the old process finished closing at 08:34:06.118 and the new
/// one stopped at 08:34:06.129.
/// </para>
/// </summary>
public sealed class UpdateStopSignalTests
{
    private static string UniqueName() => $@"Local\Nexus.Tests.Stop.{Guid.NewGuid():N}";

    [Fact]
    public void SettingTheEventStopsPcCore()
    {
        var name = UniqueName();
        using var stopped = new ManualResetEventSlim();
        using var listener = UpdateStopSignal.Listen(stopped.Set, name);

        using (var updater = EventWaitHandle.OpenExisting(name))
        {
            updater.Set();
        }

        Assert.True(stopped.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void APcCoreThatShutsDownDoesNotStopTheOneStartedAfterIt()
    {
        var name = UniqueName();
        using var closing = new ManualResetEventSlim();
        using var starting = new ManualResetEventSlim();

        // The previous PC Core is still closing when the owner starts Nexus again.
        var previous = UpdateStopSignal.Listen(closing.Set, name);
        using var next = UpdateStopSignal.Listen(starting.Set, name);

        previous.Dispose();

        Assert.False(starting.Wait(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken), "The new PC Core was told to stop by the old one shutting down.");
        Assert.False(closing.IsSet);
    }
}
