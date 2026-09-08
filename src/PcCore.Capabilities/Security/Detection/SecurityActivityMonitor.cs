using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Security.Detection;

public sealed class DetectionOptions
{
    public const string SectionName = "PcCore:Security:Detection";

    /// <summary>Turning this off stops incidents being raised at all. Arming then does nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long after arming or starting up to ignore activity.
    /// <para>
    /// The owner arms the PC by clicking something, and their hand is still on the mouse when they
    /// do. Without a short grace period every arm would immediately raise an incident about the
    /// person who just armed it.
    /// </para>
    /// </summary>
    public int GraceSeconds { get; set; } = 5;

    /// <summary>
    /// Pointer movement, in pixels, that has to accumulate before it counts.
    /// <para>
    /// A mouse on a desk drifts, a trackpad reports a pixel when the table is knocked, and an optical
    /// sensor on a patterned surface produces occasional single-pixel noise. None of that is a
    /// person.
    /// </para>
    /// </summary>
    public int PointerMovementThreshold { get; set; } = 40;

    /// <summary>How long accumulated pointer movement stays relevant before it decays.</summary>
    public int PointerWindowSeconds { get; set; } = 3;

    /// <summary>Identical activity kinds closer together than this collapse into one.</summary>
    public int CoalesceMilliseconds { get; set; } = 500;
}

/// <summary>
/// Why a security session started. Recorded on the session and shown to the owner.
/// <para>
/// The wire values are pinned by attribute, exactly as <see cref="SecurityState"/>,
/// <see cref="Sessions.SessionResolution"/> and <see cref="Evidence.EvidenceKind"/> are. This enum
/// travels to the phone inside every session summary — in <c>security.sessions.list</c>,
/// <c>security.sessions.get</c> and the <c>security.session_started</c> event — so its spelling is
/// a cross-language contract, not an implementation detail.
/// </para>
/// <para>
/// It is pinned rather than left to the codec's naming policy because leaving it produced the one
/// bug that made three features look broken at once: the policy spelled it <c>pointer_activity</c>,
/// the phone expected <c>POINTER_ACTIVITY</c> like every other security enum, and the resulting
/// decode failure emptied the incident history, broke the Security screen, and silently dropped
/// the alert notification. <c>SecuritySessionWireFormatTests</c> now holds the spelling still.
/// </para>
/// </summary>
public enum IncidentTrigger
{
    [JsonStringEnumMemberName("POINTER_ACTIVITY")]
    PointerActivity,

    [JsonStringEnumMemberName("KEYBOARD_ACTIVITY")]
    KeyboardActivity,

    [JsonStringEnumMemberName("SESSION_UNLOCKED")]
    SessionUnlocked,

    [JsonStringEnumMemberName("INTERACTIVE_LOGON")]
    InteractiveLogon,
}

/// <summary>
/// Turns raw observations into "a person is using this machine", and nothing more.
/// <para>
/// Everything in here exists to answer one question as conservatively as possible: is somebody
/// actually there? A false negative means an incident is missed. A false positive means the owner
/// gets an alarm about their own cat, learns to ignore alarms, and the feature stops working. The
/// second failure is the one that kills the feature, so the thresholds lean towards silence.
/// </para>
/// <para>
/// It gates on state as its first act. In <c>OWNER_PRESENT</c> nothing here does anything at all —
/// no counting, no timers, and, crucially, nothing downstream that could record.
/// </para>
/// </summary>
public sealed class SecurityActivityMonitor : IDisposable
{
    private readonly ISecurityActivityDetector _detector;
    private readonly INexusInputAttribution _attribution;
    private readonly ISecurityStateService _state;
    private readonly IEventStream _events;
    private readonly DetectionOptions _options;
    private readonly ILogger<SecurityActivityMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly IDisposable _stateSubscription;
    private readonly Lock _gate = new();

    private DateTimeOffset _graceUntil;
    private double _pointerTravel;
    private DateTimeOffset _pointerWindowStart;
    private DateTimeOffset _lastCoalescedAt;
    private ActivityKind? _lastCoalescedKind;

    private long _ignoredNexusInput;
    private long _ignoredSynthetic;
    private long _suppressedNoise;

    /// <summary>
    /// Raised when a person has been detected while the PC is armed. Exactly once per incident:
    /// the state machine refuses a second transition, and the monitor does not retry.
    /// </summary>
    public event Action<IncidentTrigger, LocalActivity>? IncidentDetected;

    /// <summary>Raised for activity during an incident, so the timeline can grow. Never during OWNER_PRESENT.</summary>
    public event Action<LocalActivity>? ActivityDuringIncident;

    public SecurityActivityMonitor(
        ISecurityActivityDetector detector,
        INexusInputAttribution attribution,
        ISecurityStateService state,
        IEventStream events,
        DetectionOptions options,
        ILogger<SecurityActivityMonitor> logger,
        TimeProvider? time = null)
    {
        _detector = detector;
        _attribution = attribution;
        _state = state;
        _events = events;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        _graceUntil = _time.GetUtcNow().AddSeconds(_options.GraceSeconds);

        _detector.Observed += OnObserved;

        // Subscribed through the existing event bus rather than a new hook on the state service:
        // the transition is already published there, and one notification mechanism is enough.
        _stateSubscription = _events.Subscribe(OnEvent);
    }

    public long IgnoredNexusInput => Interlocked.Read(ref _ignoredNexusInput);

    public long IgnoredSyntheticInput => Interlocked.Read(ref _ignoredSynthetic);

    public long SuppressedNoise => Interlocked.Read(ref _suppressedNoise);

    public void Start()
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning(
                "Security activity detection is disabled by configuration; arming the PC will not raise incidents.");
            return;
        }

        _detector.Start();

        if (_detector.IsRunning)
        {
            _logger.LogInformation("Security activity detection is running.");
        }
        else
        {
            _logger.LogWarning("Security activity detection is unavailable: {Reason}", _detector.Diagnostic);
        }
    }

    /// <summary>Restarts the grace period when the owner arms, so their own hand does not trip it.</summary>
    private void OnEvent(EventEnvelope envelope)
    {
        if (envelope.EventName != SecurityEvents.StateChanged
            || envelope.Payload is not SecurityStateChangedPayload change)
        {
            return;
        }

        if (change.CurrentState == SecurityState.Armed)
        {
            lock (_gate)
            {
                _graceUntil = _time.GetUtcNow().AddSeconds(_options.GraceSeconds);
                _pointerTravel = 0;
                _lastCoalescedKind = null;
            }
        }
    }

    private void OnObserved(LocalActivity activity)
    {
        // 1. Attribution. Nexus's own injected input is not a person, and neither is anything else
        //    synthetic. Counted rather than silently dropped so the exclusion is observable.
        switch (activity.Source)
        {
            case ActivitySource.NexusRemote:
                Interlocked.Increment(ref _ignoredNexusInput);
                return;

            case ActivitySource.Synthetic:
                Interlocked.Increment(ref _ignoredSynthetic);
                return;
        }

        if (!_options.Enabled)
        {
            return;
        }

        // 2. State. This is the line that makes "no recording while the owner is present" true rather
        //    than merely intended: in OWNER_PRESENT nothing downstream is ever reached — including
        //    the attribution ledger below, which has no work to do in a state where no incident can
        //    begin and would only spend claims that could never have mattered.
        var current = _state.Current.State;

        if (current == SecurityState.OwnerPresent)
        {
            return;
        }

        // 3. The second attribution rule, for the input Nexus causes that carries no signature of
        //    its own.
        //
        //    The signature check above is exact and remains the primary rule: a SendInput event
        //    either carries Nexus's tag in dwExtraInfo or it does not. But Nexus no longer reaches
        //    the desktop only through SendInput. Touch injection has no field for a tag, and the
        //    mouse messages Windows promotes a touch contact into carry Windows' own pen/touch
        //    signature instead. On this machine injected touch produced no low-level hook events at
        //    all, which would be enough on its own — but "measured once, on one build of Windows" is
        //    not a boundary, and the cost of being wrong is Nexus raising an alarm about the owner
        //    using their own PC.
        //
        //    So every event Nexus successfully injects is declared, and anything that looks local
        //    while a matching declaration is outstanding is Nexus's own work. Claims expire in
        //    fractions of a second and are counted exactly, so this absorbs precisely what Nexus
        //    caused and nothing more.
        //
        //    It sits above the SECURITY_SESSION branch deliberately. Using the phone to look at what
        //    is happening during an incident must not write the owner's own remote input into that
        //    incident's timeline — which is the same reason the signature check has always been
        //    first.
        if (_attribution.TryClaim(activity.Kind, _time.GetUtcNow()))
        {
            Interlocked.Increment(ref _ignoredNexusInput);
            return;
        }

        if (current == SecurityState.SecuritySession)
        {
            // An incident is already open. Activity still feeds the timeline, but nothing new starts.
            if (PassesNoiseFilter(activity))
            {
                ActivityDuringIncident?.Invoke(activity);
            }

            return;
        }

        // 4. Armed. Only now can an incident begin.
        if (!PassesNoiseFilter(activity))
        {
            return;
        }

        // A foreground change with nobody touching anything is an application doing something, not a
        // person arriving. It enriches a timeline; it does not start one.
        var trigger = activity.Kind switch
        {
            ActivityKind.PointerActivity => IncidentTrigger.PointerActivity,
            ActivityKind.KeyboardActivity => IncidentTrigger.KeyboardActivity,
            ActivityKind.SessionUnlocked => IncidentTrigger.SessionUnlocked,
            ActivityKind.InteractiveLogon => IncidentTrigger.InteractiveLogon,
            _ => (IncidentTrigger?)null,
        };

        if (trigger is null)
        {
            return;
        }

        _logger.LogWarning(
            "Local {Kind} detected while ARMED; raising a security session.", activity.Kind);

        IncidentDetected?.Invoke(trigger.Value, activity);
    }

    /// <summary>
    /// The whole noise heuristic. Deliberately four rules a person can hold in their head, not a
    /// model: a rule nobody understands is a rule nobody can tell is misbehaving.
    /// </summary>
    private bool PassesNoiseFilter(LocalActivity activity)
    {
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            // Rule 1: nothing counts during the grace period after arming or starting.
            if (now < _graceUntil)
            {
                Interlocked.Increment(ref _suppressedNoise);
                return false;
            }

            // Rule 2: pointer movement has to accumulate. A button press is unambiguous and skips it.
            if (activity is { Kind: ActivityKind.PointerActivity, Detail: null })
            {
                if (now - _pointerWindowStart > TimeSpan.FromSeconds(_options.PointerWindowSeconds))
                {
                    _pointerTravel = 0;
                    _pointerWindowStart = now;
                }

                _pointerTravel += PointerStep;

                if (_pointerTravel < _options.PointerMovementThreshold)
                {
                    Interlocked.Increment(ref _suppressedNoise);
                    return false;
                }

                _pointerTravel = 0;
            }

            // Rule 3: identical kinds in quick succession collapse. Holding a key produces a stream
            // of events that all mean the same thing.
            if (_lastCoalescedKind == activity.Kind
                && now - _lastCoalescedAt < TimeSpan.FromMilliseconds(_options.CoalesceMilliseconds))
            {
                Interlocked.Increment(ref _suppressedNoise);
                return false;
            }

            _lastCoalescedKind = activity.Kind;
            _lastCoalescedAt = now;

            return true;
        }
    }

    /// <summary>
    /// How much travel one pointer observation represents.
    /// <para>
    /// The detector reports movement without saying how far, because a security subsystem has no
    /// business knowing where the pointer is. Counting observations instead is coarser and entirely
    /// sufficient: jitter produces a handful, a person crossing the screen produces dozens.
    /// </para>
    /// </summary>
    private const double PointerStep = 8;

    public void Dispose()
    {
        _detector.Observed -= OnObserved;
        _stateSubscription.Dispose();
        _detector.Dispose();
    }
}
