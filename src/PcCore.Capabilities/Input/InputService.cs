using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Screen;
using PcCore.Protocol;

namespace PcCore.Capabilities.Input;

public sealed class InputOptions
{
    public const string SectionName = "PcCore:Input";

    /// <summary>
    /// Sustained events per second. A drag at 60 Hz plus buttons and keys sits far below this;
    /// anything above it is a runaway client or a stuck finger, not a person using a computer.
    /// </summary>
    public int MaxEventsPerSecond { get; set; } = 250;

    /// <summary>Allowance for bursts — a fast flick, a held arrow key — without penalising them.</summary>
    public int BurstEvents { get; set; } = 500;

    /// <summary>One typed message. Long enough to paste a paragraph, short enough not to be a weapon.</summary>
    public int MaxTextLength { get; set; } = 4096;

    /// <summary>Bounds a single scroll message, so a decoding bug cannot scroll a document to its end.</summary>
    public int MaxScrollNotches { get; set; } = 16;

    /// <summary>Bounds one relative move. Larger deltas are a mistake, not a gesture.</summary>
    public int MaxRelativeDelta { get; set; } = 4000;
}

/// <summary>An open remote-input session. Distinct from being connected, and from watching a stream.</summary>
public sealed record InputSession(string OwnerId, DateTimeOffset StartedAtUtc);

/// <summary>
/// The gate every remote input event passes through.
/// <para>
/// <b>Why a session at all.</b> Being the authenticated Primary Phone means you may ask; it does not
/// mean the phone in your pocket should be able to type. Input becomes possible only when the owner
/// opens the control surface, and stops the moment they leave it, the connection drops, or anything
/// else goes wrong. The connection and the ability to type are two facts, and conflating them would
/// mean a phone left connected on a desk is a keyboard.
/// </para>
/// <para>
/// <b>Why held state is tracked here.</b> A dropped connection during a drag, or with Ctrl held for
/// a shortcut, would otherwise leave Windows believing a button or a modifier is still down — with
/// nothing on the PC to release it, and every subsequent click behaving strangely. Every down event
/// is recorded, and ending a session for any reason releases exactly what that session pressed.
/// </para>
/// </summary>
public interface IInputService
{
    /// <summary>
    /// Raised whenever the number of open input sessions changes.
    /// <para>
    /// On the interface rather than the implementation because the indicator depends on it, and a
    /// subscription that only the composition root knows about is one a test cannot see and a
    /// refactor can quietly drop.
    /// </para>
    /// </summary>
    event Action<InputActivityChanged>? ActivityChanged;

    InputSession Begin(string ownerId);

    /// <summary>Ends a session and releases anything it left held. False when there was none.</summary>
    bool End(string ownerId);

    bool IsActive(string ownerId);

    /// <summary>Every session currently open. Used by the indicator, which shows when input is live.</summary>
    IReadOnlyList<InputSession> Active { get; }

    /// <param name="x">Position across the display, 0 to 1.</param>
    /// <param name="y">Position down the display, 0 to 1.</param>
    InputResult MoveAbsolute(string ownerId, int displayIndex, double x, double y);

    InputResult MoveRelative(string ownerId, double dx, double dy);

    InputResult Button(string ownerId, PointerButton button, ButtonAction action);

    InputResult Scroll(string ownerId, double horizontal, double vertical);

    InputResult Key(string ownerId, NexusKey key, KeyAction action);

    InputResult Text(string ownerId, string text);

    /// <summary>Releases everything this session holds. Returns how many events that took.</summary>
    int ReleaseAll(string ownerId);
}

/// <summary>Raised when an input session is opened or closed, so the indicator can follow it.</summary>
public sealed record InputActivityChanged(int ActiveSessions);

public sealed class InputService : IInputService
{
    private readonly IInputController _controller;
    private readonly IDisplayCatalog _displays;
    private readonly InputOptions _options;
    private readonly ILogger<InputService> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public event Action<InputActivityChanged>? ActivityChanged;

    public InputService(
        IInputController controller,
        IDisplayCatalog displays,
        InputOptions options,
        ILogger<InputService> logger)
    {
        _controller = controller;
        _displays = displays;
        _options = options;
        _logger = logger;
    }

    public IReadOnlyList<InputSession> Active
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Values.Select(s => s.Session)];
            }
        }
    }

    public InputSession Begin(string ownerId)
    {
        InputSession session;
        int count;

        lock (_gate)
        {
            if (_sessions.TryGetValue(ownerId, out var existing))
            {
                return existing.Session;
            }

            session = new InputSession(ownerId, DateTimeOffset.UtcNow);
            _sessions[ownerId] = new SessionState(session, new TokenBucket(_options));
            count = _sessions.Count;
        }

        _logger.LogInformation("Remote input enabled for {Owner} ({Count} active).", ownerId, count);
        ActivityChanged?.Invoke(new InputActivityChanged(count));

        return session;
    }

    public bool End(string ownerId)
    {
        var released = ReleaseAll(ownerId);
        int count;

        lock (_gate)
        {
            if (!_sessions.Remove(ownerId))
            {
                return false;
            }

            count = _sessions.Count;
        }

        _logger.LogInformation(
            "Remote input disabled for {Owner}; released {Released} held input(s) ({Count} active).",
            ownerId,
            released,
            count);

        ActivityChanged?.Invoke(new InputActivityChanged(count));
        return true;
    }

    public bool IsActive(string ownerId)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(ownerId);
        }
    }

    public InputResult MoveAbsolute(string ownerId, int displayIndex, double x, double y)
    {
        var state = Authorize(ownerId);

        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1)
        {
            // Deliberately a refusal rather than a clamp. A coordinate outside the display means the
            // client's mapping is wrong, and silently moving the pointer to the nearest edge would
            // hide that while still moving the pointer somewhere the owner did not touch.
            throw new CapabilityException(
                ErrorCodes.InvalidParameters, "Pointer position must be between 0 and 1 on both axes.");
        }

        var display = _displays.GetDisplays().FirstOrDefault(d => d.Index == displayIndex)
            ?? throw new CapabilityException(ErrorCodes.InvalidParameters, "No such display.");

        // The point lands inside the display, never one pixel past its right or bottom edge.
        var pixelX = display.Left + (int)Math.Round(x * (display.Width - 1));
        var pixelY = display.Top + (int)Math.Round(y * (display.Height - 1));

        return Record(state, _controller.MovePointerAbsolute(pixelX, pixelY));
    }

    public InputResult MoveRelative(string ownerId, double dx, double dy)
    {
        var state = Authorize(ownerId);

        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "Pointer delta must be a finite number.");
        }

        var limit = _options.MaxRelativeDelta;
        var moveX = (int)Math.Round(Math.Clamp(dx, -limit, limit));
        var moveY = (int)Math.Round(Math.Clamp(dy, -limit, limit));

        return Record(state, _controller.MovePointerRelative(moveX, moveY));
    }

    public InputResult Button(string ownerId, PointerButton button, ButtonAction action)
    {
        var state = Authorize(ownerId);

        if (!Enum.IsDefined(button) || !Enum.IsDefined(action))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "Unknown pointer button or action.");
        }

        InputResult result;

        switch (action)
        {
            case ButtonAction.Down:
                result = _controller.Button(button, down: true);
                if (result.Succeeded)
                {
                    lock (_gate)
                    {
                        state.HeldButtons.Add(button);
                    }
                }

                break;

            case ButtonAction.Up:
                result = _controller.Button(button, down: false);
                lock (_gate)
                {
                    state.HeldButtons.Remove(button);
                }

                break;

            case ButtonAction.Click:
                result = Chord(button, 1);
                break;

            default:
                result = Chord(button, 2);
                break;
        }

        return Record(state, result);
    }

    public InputResult Scroll(string ownerId, double horizontal, double vertical)
    {
        var state = Authorize(ownerId);

        if (!double.IsFinite(horizontal) || !double.IsFinite(vertical))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "Scroll amount must be a finite number.");
        }

        var limit = _options.MaxScrollNotches;

        return Record(
            state,
            _controller.Scroll(
                (int)Math.Round(Math.Clamp(horizontal, -limit, limit)),
                (int)Math.Round(Math.Clamp(vertical, -limit, limit))));
    }

    public InputResult Key(string ownerId, NexusKey key, KeyAction action)
    {
        var state = Authorize(ownerId);

        if (!Enum.IsDefined(key) || !Enum.IsDefined(action))
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "Unknown key or key action.");
        }

        InputResult result;

        switch (action)
        {
            case KeyAction.Down:
                result = _controller.Key(key, down: true);
                if (result.Succeeded)
                {
                    lock (_gate)
                    {
                        state.HeldKeys.Add(key);
                    }
                }

                break;

            case KeyAction.Up:
                result = _controller.Key(key, down: false);
                lock (_gate)
                {
                    state.HeldKeys.Remove(key);
                }

                break;

            default:
                result = _controller.Key(key, down: true);
                if (result.Succeeded)
                {
                    result = _controller.Key(key, down: false);
                }

                break;
        }

        return Record(state, result);
    }

    public InputResult Text(string ownerId, string text)
    {
        var state = Authorize(ownerId);

        if (string.IsNullOrEmpty(text))
        {
            return InputResult.Ok;
        }

        if (text.Length > _options.MaxTextLength)
        {
            throw new CapabilityException(
                ErrorCodes.InvalidParameters, $"Text may be at most {_options.MaxTextLength} characters.");
        }

        // Control characters other than tab and newline have no business arriving as text; the ones
        // that mean something have their own keys.
        foreach (var character in text)
        {
            if (char.IsControl(character) && character is not ('\t' or '\n' or '\r'))
            {
                throw new CapabilityException(
                    ErrorCodes.InvalidParameters, "Text may not contain control characters; use input.key instead.");
            }
        }

        return Record(state, _controller.Text(text));
    }

    public int ReleaseAll(string ownerId)
    {
        PointerButton[] buttons;
        NexusKey[] keys;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(ownerId, out var state))
            {
                return 0;
            }

            buttons = [.. state.HeldButtons];
            keys = [.. state.HeldKeys];

            state.HeldButtons.Clear();
            state.HeldKeys.Clear();
        }

        var released = 0;

        // Buttons first: releasing a modifier while a drag is still down can turn a drag into a
        // shortcut on the way out.
        foreach (var button in buttons)
        {
            Safely(() => _controller.Button(button, down: false));
            released++;
        }

        foreach (var key in keys)
        {
            Safely(() => _controller.Key(key, down: false));
            released++;
        }

        if (released > 0)
        {
            _logger.LogInformation(
                "Released {Buttons} held button(s) and {Keys} held key(s) for {Owner}.",
                buttons.Length,
                keys.Length,
                ownerId);
        }

        return released;
    }

    private InputResult Chord(PointerButton button, int times)
    {
        var result = InputResult.Ok;

        for (var i = 0; i < times && result.Succeeded; i++)
        {
            result = _controller.Button(button, down: true);

            if (result.Succeeded)
            {
                result = _controller.Button(button, down: false);
            }
        }

        return result;
    }

    /// <summary>Checks that a session exists and has budget, throwing the right protocol error otherwise.</summary>
    private SessionState Authorize(string ownerId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(ownerId, out var state))
            {
                throw new CapabilityException(
                    ErrorCodes.NotAuthorized,
                    "Remote input is not enabled. Open the control surface first.");
            }

            if (!state.Budget.TryTake(DateTimeOffset.UtcNow))
            {
                throw new CapabilityException(
                    ErrorCodes.RateLimited,
                    $"Too many input events. The limit is {_options.MaxEventsPerSecond} per second.");
            }

            return state;
        }
    }

    /// <summary>Logs a refusal once per session rather than once per event, and passes it through.</summary>
    private InputResult Record(SessionState state, InputResult result)
    {
        if (result.Delivery == InputDelivery.BlockedByIntegrity && !state.WarnedAboutIntegrity)
        {
            state.WarnedAboutIntegrity = true;
            _logger.LogWarning(
                "Input was refused by Windows for {Owner}: {Reason}. PC Core runs as a standard user "
                + "and cannot send input to an elevated window; this is a Windows boundary, not a bug.",
                state.Session.OwnerId,
                result.Diagnostic);
        }

        return result;
    }

    private void Safely(Func<InputResult> action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Cleanup runs on the failure path. It must never throw a second failure over the first.
            _logger.LogDebug(ex, "Releasing held input failed.");
        }
    }

    private sealed class SessionState(InputSession session, TokenBucket budget)
    {
        public InputSession Session { get; } = session;

        public TokenBucket Budget { get; } = budget;

        public HashSet<PointerButton> HeldButtons { get; } = [];

        public HashSet<NexusKey> HeldKeys { get; } = [];

        public bool WarnedAboutIntegrity { get; set; }
    }

    /// <summary>
    /// A plain token bucket: a sustained rate with room for bursts.
    /// <para>
    /// The point is not to stop an attacker — a caller that reaches here is the authenticated
    /// Primary Phone — but to stop a bug. A client stuck in a move loop would otherwise saturate the
    /// link and the desktop at once, and the first symptom would be a PC nobody can use.
    /// </para>
    /// </summary>
    private sealed class TokenBucket(InputOptions options)
    {
        private readonly double _perSecond = Math.Max(1, options.MaxEventsPerSecond);
        private readonly double _capacity = Math.Max(1, options.BurstEvents);

        private double _tokens = Math.Max(1, options.BurstEvents);
        private DateTimeOffset _last = DateTimeOffset.UtcNow;

        public bool TryTake(DateTimeOffset now)
        {
            var elapsed = (now - _last).TotalSeconds;

            if (elapsed > 0)
            {
                _tokens = Math.Min(_capacity, _tokens + (elapsed * _perSecond));
                _last = now;
            }

            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }
}
