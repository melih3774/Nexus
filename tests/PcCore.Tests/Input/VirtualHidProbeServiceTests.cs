using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using PcCore.NativeInput.Hid;
using PcCore.Windows.Input;

using Xunit;

namespace PcCore.Tests.Input;

/// <summary>
/// A backend that counts what it was asked, and answers whatever the test scripted.
/// <para>
/// It never opens a device. The point of these tests is <em>when</em> the backend is asked and
/// <em>whether</em> it is released, both of which are decisions this service owns; what a probe
/// actually does on a machine with a driver belongs to <see cref="NexusHidBackend"/> and to the
/// real hardware.
/// </para>
/// </summary>
internal sealed class CountingHidBackend : IVirtualHidBackend
{
    // Two fields, because the real backend has two. Availability is what the last probe observed;
    // it does not become Ready because a driver appeared, only because something looked. Collapsing
    // them into one would let a test "connect" without probing, which is the exact mistake this
    // whole service exists to prevent.
    private VirtualHidAvailability _observed =
        VirtualHidAvailability.None("no backend in this test");

    private VirtualHidAvailability _pending =
        VirtualHidAvailability.None("no backend in this test");

    public int Probes { get; private set; }

    public int Neutralizations { get; private set; }

    public int Shutdowns { get; private set; }

    public int Disposals { get; private set; }

    /// <summary>Set to make the next probe throw, as a driver vanishing mid-call would.</summary>
    public bool ThrowOnProbe { get; set; }

    public VirtualHidAvailability Availability => _observed;

    /// <summary>Scripts what the <em>next probe</em> will find. Nothing changes until one runs.</summary>
    public void WillReport(VirtualHidState state, int devices = 3)
    {
        _pending = new VirtualHidAvailability
        {
            State = state,
            Devices = Enumerable.Range(0, devices)
                .Select(i => new VirtualHidDeviceIdentity
                {
                    Kind = (VirtualHidDeviceKind)i,
                    DevicePath = $@"\\?\HID#VID_F055&PID_000{i + 1}#test",
                })
                .ToArray(),
            Diagnostic = state == VirtualHidState.Ready ? null : state.ToString(),
        };
    }

    /// <summary>
    /// What a driver loss looks like from outside: the submit path spent the failure budget and
    /// dropped the connection, so availability is no longer Ready and nothing was probed to
    /// discover that.
    /// </summary>
    public void Lose()
    {
        var gone = new VirtualHidAvailability
        {
            State = VirtualHidState.Unavailable,
            Devices = [],
            Diagnostic = "the Nexus HID driver stopped answering",
        };

        // Both: the submit path dropped the connection, and a probe would now find it gone too.
        _observed = gone;
        _pending = gone;
    }

    public VirtualHidAvailability Probe()
    {
        Probes++;

        if (ThrowOnProbe)
        {
            throw new InvalidOperationException("the driver went away mid-probe");
        }

        _observed = _pending;
        return _observed;
    }

    public bool SubmitMouse(in HidMouseReport report) => true;

    public bool SubmitKeyboard(in HidKeyboardReport report) => true;

    public bool SubmitDigitizer(in HidDigitizerReport report) => true;

    public void Neutralize() => Neutralizations++;

    public void Shutdown()
    {
        Shutdowns++;
        Neutralize();
        _observed = VirtualHidAvailability.None("released");
    }

    public void Dispose() => Disposals++;
}

/// <summary>
/// The probe lifecycle: who asks the driver whether it is there, how often, and what happens on the
/// way out.
/// <para>
/// <b>These exist because the answer to "who asks" was nobody.</b> The backend, the lifecycle state
/// machine, the eligibility rules, the router and the registry were all written, wired and passing
/// their own tests, and <c>Probe()</c> had six call sites — all of them in this test project. On a
/// machine with the driver installed and started, PC Core reported "the Nexus HID driver is not
/// connected" and was correct: it had never once looked. Every test below is a property that would
/// have failed then.
/// </para>
/// </summary>
public sealed class VirtualHidProbeServiceTests
{
    /// <summary>A tick long enough that no timer fires during a test that drives ticks by hand.</summary>
    private static readonly TimeSpan Never = TimeSpan.FromMinutes(10);

    private static VirtualHidProbeService Service(
        CountingHidBackend backend,
        TimeSpan? tick = null) =>
        new(backend, NullLogger<VirtualHidProbeService>.Instance, tickInterval: tick ?? Never);

    // --- startup ---------------------------------------------------------------------------------

    /// <summary>The defect, pinned: starting the host must actually contact the driver.</summary>
    [Fact]
    public async Task StartingTheHostProbesTheBackend()
    {
        var backend = new CountingHidBackend();
        using var service = Service(backend);

        Assert.Equal(0, backend.Probes);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, backend.Probes);
        Assert.Equal(1, service.ProbeCount);
    }

    /// <summary>
    /// Before anything starts, the reported phase says nobody is asking — rather than reporting the
    /// backend's opinion, which would be "not connected" and would read as "there is no driver".
    /// </summary>
    [Fact]
    public void BeforeStartupTheDiagnosticSaysNothingIsDrivingTheBackend()
    {
        using var service = Service(new CountingHidBackend());

        Assert.Equal(VirtualHidProbePhase.NotRunning, service.Phase);
        Assert.Contains("no probe pump is running", service.Describe(), StringComparison.Ordinal);
    }

    /// <summary>The first probe happens inside startup, so the first status request is truthful.</summary>
    [Fact]
    public async Task TheFirstStatusAfterStartupReflectsARealProbe()
    {
        var backend = new CountingHidBackend();
        backend.WillReport(VirtualHidState.Ready);

        using var service = Service(backend);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VirtualHidProbePhase.Connected, service.Phase);
        Assert.Equal(VirtualHidState.Ready, service.LastResult);
        Assert.NotNull(service.LastProbeUtc);
    }

    // --- retrying an absent or unavailable driver -------------------------------------------------

    [Fact]
    public async Task AnAbsentDriverIsAskedAgainOnEveryTick()
    {
        var backend = new CountingHidBackend();
        using var service = Service(backend);

        await service.StartAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(service.Tick(), "a disconnected backend must be probed");
        }

        Assert.Equal(4, backend.Probes);
        Assert.Equal(VirtualHidProbePhase.Searching, service.Phase);
    }

    /// <summary>
    /// A driver that answers and then refuses to publish is retried too, and is reported differently
    /// from one that was never found — the owner has something to do about the first and not the
    /// second.
    /// </summary>
    [Fact]
    public async Task ARefusingDriverIsRetriedAndReportedAsSuch()
    {
        var backend = new CountingHidBackend();
        backend.WillReport(VirtualHidState.Refused, devices: 0);

        using var service = Service(backend);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VirtualHidProbePhase.Retrying, service.Phase);
        Assert.True(service.Tick());
        Assert.Equal(2, backend.Probes);
    }

    /// <summary>
    /// A probe that throws must not kill the pump. The next tick still asks — the whole failure mode
    /// this service exists to prevent is one where nothing asks again.
    /// </summary>
    [Fact]
    public async Task AThrowingProbeDoesNotStopThePump()
    {
        var backend = new CountingHidBackend { ThrowOnProbe = true };
        using var service = Service(backend);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(service.Tick());
        Assert.True(service.Tick());
        Assert.Equal(3, backend.Probes);
    }

    // --- a connected backend is left alone --------------------------------------------------------

    /// <summary>
    /// Re-probing a working backend would tear its devices down and republish them, because
    /// connecting disconnects first. So a connected backend is not probed at all.
    /// </summary>
    [Fact]
    public async Task AConnectedBackendIsNeverProbedAgain()
    {
        var backend = new CountingHidBackend();
        backend.WillReport(VirtualHidState.Ready);

        using var service = Service(backend);
        await service.StartAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 20; i++)
        {
            Assert.False(service.Tick(), "a connected backend must not be re-probed");
        }

        Assert.Equal(1, backend.Probes);
    }

    /// <summary>
    /// Partial counts as connected. A backend publishing a mouse and a keyboard but no digitizer is
    /// a real install, and probing to chase the third would delete the two that work.
    /// </summary>
    [Fact]
    public async Task APartialBackendIsLeftAloneRatherThanChased()
    {
        var backend = new CountingHidBackend();
        backend.WillReport(VirtualHidState.Partial, devices: 2);

        using var service = Service(backend);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(service.Tick());
        Assert.Equal(1, backend.Probes);
        Assert.Equal(VirtualHidProbePhase.Connected, service.Phase);
    }

    // --- loss and recovery ------------------------------------------------------------------------

    /// <summary>
    /// The sequence that matters: connected, lost, retried, back. Loss is discovered because the
    /// submit path drops the connection, not because this polls for it.
    /// </summary>
    [Fact]
    public async Task LossResumesProbingAndRecoveryStopsItAgain()
    {
        var backend = new CountingHidBackend();
        backend.WillReport(VirtualHidState.Ready);

        using var service = Service(backend);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(service.Tick());
        Assert.Equal(1, backend.Probes);

        // The driver goes away. Nothing probed to find that out; reports started failing.
        backend.Lose();

        Assert.True(service.Tick(), "a lost backend must be probed again");
        Assert.Equal(2, backend.Probes);
        Assert.Equal(VirtualHidProbePhase.Retrying, service.Phase);

        // Still gone: keep asking, on the backend's own bounded schedule.
        Assert.True(service.Tick());
        Assert.Equal(3, backend.Probes);

        // It comes back.
        backend.WillReport(VirtualHidState.Ready);

        Assert.True(service.Tick());
        Assert.Equal(VirtualHidProbePhase.Connected, service.Phase);

        // And is left alone again.
        Assert.False(service.Tick());
        Assert.Equal(4, backend.Probes);
    }

    /// <summary>
    /// The retry policy is the backend's, not this service's. Every tick against a disconnected
    /// backend is one call to <c>Probe()</c> and no more — the cooldown, the doubling and the
    /// generation counter all live behind that call, so there is exactly one place they can be
    /// wrong.
    /// </summary>
    [Fact]
    public async Task EachTickCostsExactlyOneProbeAndNoRetryLogicOfItsOwn()
    {
        var backend = new CountingHidBackend();
        using var service = Service(backend);

        await service.StartAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 10; i++)
        {
            service.Tick();
        }

        Assert.Equal(11, backend.Probes);
    }

    // --- the pump is real -------------------------------------------------------------------------

    /// <summary>
    /// The timer actually ticks. Everything above drives <c>Tick</c> by hand, which would pass
    /// perfectly well if the loop were never started — and that is precisely the shape of the bug
    /// being fixed, so it gets its own test.
    /// </summary>
    [Fact]
    public async Task ThePumpKeepsProbingOnItsOwn()
    {
        var backend = new CountingHidBackend();
        using var service = Service(backend, TimeSpan.FromMilliseconds(15));

        await service.StartAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (backend.Probes < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(backend.Probes >= 4, $"the pump probed {backend.Probes} time(s); it is not running");
    }

    // --- shutdown ---------------------------------------------------------------------------------

    /// <summary>
    /// Shutdown releases the devices explicitly, over a connection that is still open, rather than
    /// leaving it to the driver noticing the control handle close.
    /// </summary>
    [Fact]
    public async Task StoppingReleasesAndDisconnectsTheBackend()
    {
        var backend = new CountingHidBackend();
        backend.WillReport(VirtualHidState.Ready);

        using var service = Service(backend);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, backend.Shutdowns);

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, backend.Shutdowns);
        Assert.True(backend.Neutralizations >= 1, "shutdown must neutralise before it disconnects");
        Assert.Equal(VirtualHidProbePhase.Stopped, service.Phase);
    }

    /// <summary>
    /// And it happens before disposal, not as a side effect of it. A container disposes singletons
    /// last; held input must not wait for that.
    /// </summary>
    [Fact]
    public async Task TheBackendIsReleasedWithoutBeingDisposedByThisService()
    {
        var backend = new CountingHidBackend();
        backend.WillReport(VirtualHidState.Ready);

        var service = Service(backend);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();

        Assert.Equal(1, backend.Shutdowns);
        Assert.Equal(0, backend.Disposals);
    }

    [Fact]
    public async Task StoppingWithoutHavingStartedStillReleases()
    {
        var backend = new CountingHidBackend();
        using var service = Service(backend);

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, backend.Shutdowns);
    }

    // --- one pump, whatever the composition did ---------------------------------------------------

    /// <summary>
    /// Two pumps would double every probe and race each other into the connect path, where the
    /// second would disconnect the devices the first had just published.
    /// </summary>
    [Fact]
    public async Task ASecondStartDoesNotCreateASecondPump()
    {
        var backend = new CountingHidBackend();
        using var service = Service(backend);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, backend.Probes);
        Assert.Equal(2, service.StartCount);
    }

    /// <summary>
    /// A stop and a fresh start — the in-process restart — leaves one pump, not two, and probes
    /// again because the previous stop released everything.
    /// </summary>
    [Fact]
    public async Task RestartingProbesAgainWithoutLeavingTheOldPumpBehind()
    {
        var backend = new CountingHidBackend();
        using var service = Service(backend);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, backend.Probes);

        service.Tick();
        Assert.Equal(3, backend.Probes);
    }

    /// <summary>
    /// The registration itself: one object, resolved both as the hosted service the host starts and
    /// as the singleton diagnostics read. Registering the type twice would give two, one of which
    /// probes and one of which reports.
    /// </summary>
    [Fact]
    public void CompositionProducesExactlyOneProbeService()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IVirtualHidBackend>(new CountingHidBackend());
        services.AddVirtualHidProbe();

        using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().OfType<VirtualHidProbeService>().ToArray();

        Assert.Single(hosted);
        Assert.Same(provider.GetRequiredService<VirtualHidProbeService>(), hosted[0]);
    }

    /// <summary>Registering twice — a merge accident, a copied line — still yields one pump.</summary>
    [Fact]
    public async Task RegisteringTwiceStillProbesOnce()
    {
        var backend = new CountingHidBackend();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<IVirtualHidBackend>(backend);
        services.AddVirtualHidProbe();
        services.AddVirtualHidProbe();

        using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().OfType<VirtualHidProbeService>().ToArray();
        Assert.Single(hosted);

        foreach (var service in hosted)
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, backend.Probes);
    }
}
