using PcCore.Capabilities.Indicator;
using PcCore.Tests.TestSupport;
using Xunit;

namespace PcCore.Tests.Indicator;

/// <summary>
/// What the PC shows while a phone is watching or driving it.
/// <para>
/// The decision is a pure function of three facts, so it is tested as one. The property that
/// matters most is the one asserted last: there is no combination of mode and activity that leaves
/// a live remote session with no sign of it at all.
/// </para>
/// </summary>
public sealed class RemoteIndicatorTests
{
    [Fact]
    public void NothingIsShownWhenNobodyIsConnected()
    {
        foreach (var mode in Enum.GetValues<IndicatorMode>())
        {
            Assert.Equal(IndicatorState.Idle, RemoteIndicatorService.Decide(mode, viewing: false, inputActive: false));
        }
    }

    [Fact]
    public void StandardShowsTheBadgeOnlyWhileInputIsLive()
    {
        // The default, and the reasoning behind it: an overlay that is up whenever the phone is
        // merely watching becomes wallpaper, and stops being noticed at the moment it matters.
        var watching = RemoteIndicatorService.Decide(IndicatorMode.Standard, viewing: true, inputActive: false);
        Assert.False(watching.Overlay);
        Assert.True(watching.Tray);

        var controlling = RemoteIndicatorService.Decide(IndicatorMode.Standard, viewing: true, inputActive: true);
        Assert.True(controlling.Overlay);
        Assert.True(controlling.Tray);
    }

    [Fact]
    public void MinimalNeverShowsTheBadgeButAlwaysKeepsTheTray()
    {
        // The one thing MINIMAL is not allowed to do is disappear. Quieter, never hidden.
        foreach (var (viewing, input) in new[] { (true, false), (false, true), (true, true) })
        {
            var state = RemoteIndicatorService.Decide(IndicatorMode.Minimal, viewing, input);

            Assert.False(state.Overlay);
            Assert.True(state.Tray);
        }
    }

    [Fact]
    public void AlwaysVisibleShowsTheBadgeForPassiveViewingToo()
    {
        var state = RemoteIndicatorService.Decide(IndicatorMode.AlwaysVisible, viewing: true, inputActive: false);

        Assert.True(state.Overlay);
        Assert.True(state.Tray);
    }

    [Fact]
    public void TheTextSaysWhichIsHappening()
    {
        Assert.Contains(
            "viewing",
            RemoteIndicatorService.Decide(IndicatorMode.AlwaysVisible, true, false).Text,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "controlling",
            RemoteIndicatorService.Decide(IndicatorMode.Standard, true, true).Text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoModeCanHideALiveSessionCompletely()
    {
        // The invariant the whole feature rests on. If this ever fails, Nexus has become something
        // that can watch a machine without the person at it being able to tell.
        foreach (var mode in Enum.GetValues<IndicatorMode>())
        {
            foreach (var (viewing, input) in new[] { (true, false), (false, true), (true, true) })
            {
                var state = RemoteIndicatorService.Decide(mode, viewing, input);

                Assert.True(
                    state.Overlay || state.Tray,
                    $"{mode} with viewing={viewing} input={input} showed nothing at all");

                Assert.NotEmpty(state.Text);
            }
        }
    }

    [Fact]
    public void InputAloneIsEnoughToShowSomething()
    {
        // Input without a stream is unusual but possible - the two are deliberately independent -
        // and it is the more sensitive of the two, so it must never be the quiet case.
        var state = RemoteIndicatorService.Decide(IndicatorMode.Standard, viewing: false, inputActive: true);

        Assert.True(state.Overlay);
        Assert.True(state.Tray);
    }

    [Fact]
    public void TheChosenModeSurvivesARestart()
    {
        using var state = new TempStateDirectory("indicator-mode");

        using (var first = new RemoteIndicatorService(
            new NullRemoteIndicator(), NoInput.Instance, state.Directory, TestLog.For<RemoteIndicatorService>()))
        {
            Assert.Equal(IndicatorMode.Standard, first.Mode);
            first.SetMode(IndicatorMode.AlwaysVisible);
        }

        using var second = new RemoteIndicatorService(
            new NullRemoteIndicator(), NoInput.Instance, state.Directory, TestLog.For<RemoteIndicatorService>());

        Assert.Equal(IndicatorMode.AlwaysVisible, second.Mode);
    }

    [Fact]
    public void AMissingPreferenceFallsBackToTheDefault()
    {
        using var state = new TempStateDirectory("indicator-missing");

        using var service = new RemoteIndicatorService(
            new NullRemoteIndicator(), NoInput.Instance, state.Directory, TestLog.For<RemoteIndicatorService>());

        Assert.Equal(IndicatorMode.Standard, service.Mode);
    }

    [Fact]
    public void ActivityChangesReachTheDisplay()
    {
        using var state = new TempStateDirectory("indicator-activity");
        var display = new NullRemoteIndicator();

        using var service = new RemoteIndicatorService(display, NoInput.Instance, state.Directory, TestLog.For<RemoteIndicatorService>());

        service.SetViewers(1);
        Assert.True(display.Last.Tray);
        Assert.False(display.Last.Overlay);

        service.SetInputSessions(1);
        Assert.True(display.Last.Overlay);

        service.SetInputSessions(0);
        service.SetViewers(0);
        Assert.Equal(IndicatorState.Idle, display.Last);
    }

    [Fact]
    public void DisposingClearsTheIndicator()
    {
        // A crash or a shutdown must not leave a badge on screen claiming a session that has ended.
        using var state = new TempStateDirectory("indicator-dispose");
        var display = new NullRemoteIndicator();

        var service = new RemoteIndicatorService(display, NoInput.Instance, state.Directory, TestLog.For<RemoteIndicatorService>());
        service.SetViewers(1);
        service.SetInputSessions(1);

        service.Dispose();

        Assert.Equal(IndicatorState.Idle, display.Last);
    }
}
