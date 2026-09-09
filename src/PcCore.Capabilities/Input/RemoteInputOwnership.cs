namespace PcCore.Capabilities.Input;

/// <summary>
/// Which backend, and which <em>instance</em> of it, produced some input.
/// <para>
/// <b>The generation is the load-bearing half.</b> "The HID backend" is not a sufficient identity
/// once a driver can disappear and come back: a <c>Ctrl</c> held through one driver instance is not
/// released by a neutral report sent to its successor, because the successor's device is a different
/// device that never had anything held. Every reconnect takes a new generation, so state belonging
/// to a dead instance can never be mistaken for state the live one owns.
/// </para>
/// </summary>
public readonly record struct BackendInstance(InputBackend Backend, int Generation)
{
    /// <summary>The instance used before anything has been routed. Owns nothing, ever.</summary>
    public static readonly BackendInstance None = new(InputBackend.LocalInjection, 0);

    public override string ToString() => $"{Backend}#{Generation}";
}

/// <summary>What is currently held, and by whom.</summary>
public sealed record HeldInputState
{
    public required int Buttons { get; init; }

    public required int Keys { get; init; }

    public required int Modifiers { get; init; }

    public required bool TouchContact { get; init; }

    public bool IsNeutral => Buttons == 0 && Keys == 0 && Modifiers == 0 && !TouchContact;

    public static readonly HeldInputState Neutral = new()
    {
        Buttons = 0,
        Keys = 0,
        Modifiers = 0,
        TouchContact = false,
    };
}

/// <summary>Why a route switch was refused, when it was.</summary>
public enum RouteSwitchResult
{
    /// <summary>Nothing was held; the switch may proceed.</summary>
    Clean,

    /// <summary>State was held and has been neutralised; the switch may proceed.</summary>
    Neutralized,

    /// <summary>
    /// State was held and neutralisation could not be delivered, because the backend that owned it
    /// is gone. The switch may proceed, but the old device may still believe something is held, and
    /// nothing may be routed to that backend again until it accepts a neutral report.
    /// </summary>
    Uncertain,
}

/// <summary>
/// Tracks what remote input is currently held down, and which backend instance owns it.
/// <para>
/// <b>The invariant this type exists to enforce:</b> input held through one backend is released
/// through that same backend, or not at all.
/// </para>
/// <para>
/// The failure it prevents is specific and silent. A <c>Ctrl</c> pressed through a virtual HID
/// keyboard lives in that HID device's report state. If the driver then dies and Nexus "releases"
/// Ctrl with a <c>SendInput</c> key-up, the key-up goes to a different input path entirely — the HID
/// device is gone, so nothing ever tells Windows that its keyboard let go, and the system can be
/// left believing a modifier is down with no way for the owner to clear it except unplugging
/// something that was never plugged in. Releasing through the wrong backend is worse than not
/// releasing at all, because it looks like it worked.
/// </para>
/// <para>
/// <b>Uncertainty is represented rather than assumed away.</b> When the owning backend disappears
/// before a neutral report can be delivered, this does not record the state as released. It records
/// that nobody knows — and refuses to route to that backend again until a fresh instance has
/// accepted an explicit neutral report. Pretending the release succeeded is the one outcome that
/// makes the problem invisible.
/// </para>
/// </summary>
public sealed class RemoteInputOwnership
{
    private readonly Lock _gate = new();

    private BackendInstance _owner = BackendInstance.None;
    private int _buttons;
    private int _keys;
    private int _modifiers;
    private bool _touch;
    private bool _uncertain;
    private BackendInstance _uncertainOwner = BackendInstance.None;

    /// <summary>The instance currently holding state, or <see cref="BackendInstance.None"/>.</summary>
    public BackendInstance Owner
    {
        get
        {
            lock (_gate)
            {
                return _owner;
            }
        }
    }

    public HeldInputState Held
    {
        get
        {
            lock (_gate)
            {
                return new HeldInputState
                {
                    Buttons = _buttons,
                    Keys = _keys,
                    Modifiers = _modifiers,
                    TouchContact = _touch,
                };
            }
        }
    }

    /// <summary>
    /// True when a backend disappeared while holding state, so nothing knows whether it was
    /// released. Cleared only by <see cref="ConfirmNeutral"/>.
    /// </summary>
    public bool IsUncertain
    {
        get
        {
            lock (_gate)
            {
                return _uncertain;
            }
        }
    }

    /// <summary>Records that something went down through this instance.</summary>
    public void RecordDown(BackendInstance instance, HeldInputKind kind)
    {
        lock (_gate)
        {
            // Ownership follows the first thing held. A second backend cannot take ownership while
            // the first still holds something; the router is responsible for not letting that happen,
            // and TryBeginRoute is what enforces it.
            if (IsNeutralLocked())
            {
                _owner = instance;
            }

            switch (kind)
            {
                case HeldInputKind.Button: _buttons++; break;
                case HeldInputKind.Key: _keys++; break;
                case HeldInputKind.Modifier: _modifiers++; break;
                case HeldInputKind.TouchContact: _touch = true; break;
            }
        }
    }

    /// <summary>
    /// Records a release through this instance. A release from an instance that does not own the
    /// state is refused and returns false — that is the cross-backend release this type forbids.
    /// </summary>
    public bool TryRecordUp(BackendInstance instance, HeldInputKind kind)
    {
        lock (_gate)
        {
            if (_owner != instance)
            {
                return false;
            }

            switch (kind)
            {
                case HeldInputKind.Button when _buttons > 0: _buttons--; break;
                case HeldInputKind.Key when _keys > 0: _keys--; break;
                case HeldInputKind.Modifier when _modifiers > 0: _modifiers--; break;
                case HeldInputKind.TouchContact: _touch = false; break;
                default: return false;
            }

            if (IsNeutralLocked())
            {
                _owner = BackendInstance.None;
            }

            return true;
        }
    }

    /// <summary>
    /// Asks whether input may be routed to <paramref name="instance"/> now.
    /// <para>
    /// Refuses while a different instance holds state, and refuses while neutralisation of a
    /// vanished backend is unconfirmed. Both refusals are the same rule seen from two sides: nothing
    /// new is routed until what is already held is accounted for.
    /// </para>
    /// </summary>
    public bool CanRouteTo(BackendInstance instance)
    {
        lock (_gate)
        {
            if (_uncertain && _uncertainOwner.Backend == instance.Backend)
            {
                // The same backend type came back. Until a fresh instance accepts a neutral report,
                // its device may still believe something is held.
                return false;
            }

            return IsNeutralLocked() || _owner == instance;
        }
    }

    /// <summary>
    /// Neutralises whatever the current owner holds, using a caller-supplied delivery that returns
    /// whether the backend accepted it.
    /// <para>
    /// The delivery is passed in rather than called on a backend reference so this type stays free of
    /// any knowledge of how a backend works — and so a test can make delivery fail without needing a
    /// driver to disappear.
    /// </para>
    /// </summary>
    public RouteSwitchResult NeutralizeForSwitch(Func<BackendInstance, bool> deliverNeutral)
    {
        ArgumentNullException.ThrowIfNull(deliverNeutral);

        BackendInstance owner;

        lock (_gate)
        {
            if (IsNeutralLocked() && !_uncertain)
            {
                return RouteSwitchResult.Clean;
            }

            owner = _owner;
        }

        // Delivered outside the lock: it crosses a process boundary and must not hold a lock the
        // input path also takes.
        var delivered = owner != BackendInstance.None && deliverNeutral(owner);

        lock (_gate)
        {
            if (delivered)
            {
                ClearLocked();
                _uncertain = false;
                _uncertainOwner = BackendInstance.None;
                return RouteSwitchResult.Neutralized;
            }

            // Not released, and not pretended to be. The old device may still hold something.
            _uncertain = true;
            _uncertainOwner = owner;
            ClearLocked();
            return RouteSwitchResult.Uncertain;
        }
    }

    /// <summary>
    /// Records that a fresh backend instance has accepted an explicit neutral report, which is the
    /// only thing that clears uncertainty.
    /// <para>
    /// Refuses a generation that is not newer than the one that vanished: the same instance coming
    /// back is the same device, and accepting it would clear uncertainty without resolving it.
    /// </para>
    /// </summary>
    public bool ConfirmNeutral(BackendInstance instance)
    {
        lock (_gate)
        {
            if (_uncertain
                && _uncertainOwner.Backend == instance.Backend
                && instance.Generation <= _uncertainOwner.Generation)
            {
                return false;
            }

            ClearLocked();
            _uncertain = false;
            _uncertainOwner = BackendInstance.None;
            return true;
        }
    }

    /// <summary>
    /// Forgets everything without claiming anything was released. For shutdown, where the question
    /// stops mattering because the process is going away.
    /// </summary>
    public void Forget()
    {
        lock (_gate)
        {
            ClearLocked();
            _uncertain = false;
            _uncertainOwner = BackendInstance.None;
        }
    }

    private bool IsNeutralLocked() => _buttons == 0 && _keys == 0 && _modifiers == 0 && !_touch;

    private void ClearLocked()
    {
        _buttons = 0;
        _keys = 0;
        _modifiers = 0;
        _touch = false;
        _owner = BackendInstance.None;
    }
}

/// <summary>What kind of thing is being held. Deliberately not which key or which button.</summary>
public enum HeldInputKind
{
    Button,
    Key,
    Modifier,
    TouchContact,
}
