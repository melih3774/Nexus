using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Storage;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Capabilities;

/// <summary>
/// PC Core's log on disk.
/// <para>
/// It exists because PC Core normally has no console — Start Menu, tray, sign-in — so everything it
/// diagnosed about itself went nowhere, and the owner's question after a failure ("why did my phone
/// stop connecting?") could only be answered by someone who had thought to launch it from a
/// terminal first.
/// </para>
/// </summary>
public sealed class RollingFileLoggerTests
{
    [Fact]
    public void WhatIsLoggedEndsUpInTheFile()
    {
        using var state = new TempStateDirectory("host-log");
        using var provider = new RollingFileLoggerProvider(state.Directory);

        var logger = provider.CreateLogger("PcCore.Tests");
        logger.LogInformation("Device {DeviceId} authenticated.", "GxpX9buUY");
        logger.LogWarning("Device authentication failed: {Reason}.", "wrong secret");

        var written = File.ReadAllText(state.Directory.File(RollingFileLoggerProvider.FileName));

        // The message, its level, and its category — the three things that make a line searchable.
        Assert.Contains("Device GxpX9buUY authenticated.", written, StringComparison.Ordinal);
        Assert.Contains("wrong secret", written, StringComparison.Ordinal);
        Assert.Contains("info", written, StringComparison.Ordinal);
        Assert.Contains("warn", written, StringComparison.Ordinal);
        Assert.Contains("PcCore.Tests", written, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionIsWrittenWithItsMessage()
    {
        using var state = new TempStateDirectory("host-log-exception");
        using var provider = new RollingFileLoggerProvider(state.Directory);

        provider.CreateLogger("PcCore.Tests")
            .LogError(new InvalidOperationException("the camera was already open"), "Live capture failed");

        var written = File.ReadAllText(state.Directory.File(RollingFileLoggerProvider.FileName));

        Assert.Contains("Live capture failed", written, StringComparison.Ordinal);
        Assert.Contains("the camera was already open", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// The log rolls rather than growing without bound, and keeps one generation — because the run
    /// that explains a problem is usually the one *before* the one the owner is looking at.
    /// </summary>
    [Fact]
    public void TheLogRollsAndKeepsOnePreviousGeneration()
    {
        using var state = new TempStateDirectory("host-log-roll");
        using var provider = new RollingFileLoggerProvider(state.Directory);

        var logger = provider.CreateLogger("PcCore.Tests");
        var padding = new string('x', 4096);

        // Comfortably past the 2 MB cap.
        for (var i = 0; i < 700; i++)
        {
            logger.LogInformation("line {Index} {Padding}", i, padding);
        }

        var current = state.Directory.File(RollingFileLoggerProvider.FileName);
        var previous = state.Directory.File(RollingFileLoggerProvider.PreviousFileName);

        Assert.True(File.Exists(current), "the current log is missing after rolling");
        Assert.True(File.Exists(previous), "the previous generation was discarded rather than kept");

        // Rolling must not be a way to lose the newest lines: the last thing written is in the
        // current file, which is where anyone looks first.
        Assert.Contains("line 699", File.ReadAllText(current), StringComparison.Ordinal);

        // And the cap is real. Each file is bounded; without this the roll could fire once and then
        // let the new file grow forever.
        Assert.True(
            new FileInfo(current).Length < 8 * 1024 * 1024,
            "the current log grew past anything the cap would allow");
    }

    /// <summary>
    /// A log that cannot be opened must never stop PC Core from running. The console provider is
    /// registered beside it and still works.
    /// </summary>
    [Fact]
    public void AnUnusableDirectoryIsSurvived()
    {
        // A path that cannot be a directory, because a file of that name is in the way.
        using var state = new TempStateDirectory("host-log-blocked");
        var blocked = Path.Combine(state.Path, "not-a-directory");
        File.WriteAllText(blocked, "in the way");

        var provider = new RollingFileLoggerProvider(new StateDirectory(blocked));

        // Constructing it did not throw, and neither does using it.
        provider.CreateLogger("PcCore.Tests").LogInformation("this has nowhere to go");
        provider.Dispose();
    }
}
