using System;
using System.IO;
using System.Threading;
using PcAssistant.Desktop;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// The stop request Nexus's updater sends before replacing an installed PC Assistant: that it reaches
/// the right copy, and that PC Assistant only agrees when nothing is in flight.
/// </summary>
public sealed class UpdateStopListenerTests
{
    /// <summary>
    /// Nexus derives the same name (PcCore.Update.AssistantInstall.StopEventName) and pins it to this
    /// same literal. A drift on either side fails here or there, never silently.
    /// </summary>
    [Fact]
    public void TheEventNameIsDerivedFromTheExecutablePathTheWayNexusDerivesIt()
    {
        Assert.Equal(
            @"Local\PcAssistant.UpdateStop.BA6C187C6D18F441",
            UpdateStopListener.EventNameFor(@"C:\Users\Example\AppData\Local\Programs\Nexus\Assistant\PcAssistant.Desktop.exe"));

        // Case does not make it a different copy; a different folder does.
        Assert.Equal(
            UpdateStopListener.EventNameFor(@"C:\USERS\EXAMPLE\APPDATA\LOCAL\PROGRAMS\NEXUS\ASSISTANT\PCASSISTANT.DESKTOP.EXE"),
            UpdateStopListener.EventNameFor(@"C:\Users\Example\AppData\Local\Programs\Nexus\Assistant\PcAssistant.Desktop.exe"));
        Assert.NotEqual(
            UpdateStopListener.EventNameFor(@"C:\dev\PcAssistant.Desktop\bin\Release\net10.0-windows\PcAssistant.Desktop.exe"),
            UpdateStopListener.EventNameFor(@"C:\Users\Example\AppData\Local\Programs\Nexus\Assistant\PcAssistant.Desktop.exe"));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(2, 3, false)]
    public void ItClosesOnlyWhenNoTaskIsRunningAndNoApprovalIsWaiting(int running, int waiting, bool closes)
    {
        Assert.Equal(closes, UpdateStopListener.RefusalReason(running, waiting) is null);
    }

    [Fact]
    public void ADevelopmentHostIsNeverOfferedToTheUpdater()
    {
        Assert.Null(UpdateStopListener.TryStart(@"C:\Program Files\dotnet\dotnet.exe", () => { }, _ => { }));
        Assert.Null(UpdateStopListener.TryStart(null, () => { }, _ => { }));
    }

    /// <summary>The real named event, set the way the updater sets it, reaches the listener — more than once.</summary>
    [Fact]
    public void TheUpdatersSignalReachesTheListenerEveryTimeItIsSent()
    {
        var executable = Path.Combine(Path.GetTempPath(), "nexus-stop-test-" + Guid.NewGuid().ToString("N"), "PcAssistant.Desktop.exe");
        using var asked = new SemaphoreSlim(0);

        using var listener = UpdateStopListener.TryStart(executable, () => asked.Release(), _ => { });
        Assert.NotNull(listener);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True(EventWaitHandle.TryOpenExisting(UpdateStopListener.EventNameFor(executable), out var signal));
            using (signal) signal!.Set();

            Assert.True(asked.Wait(TimeSpan.FromSeconds(5)), $"stop request {attempt + 1} was not delivered");
        }
    }
}
