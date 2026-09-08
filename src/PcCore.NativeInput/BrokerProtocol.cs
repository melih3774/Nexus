using System.Text.Json.Serialization;

namespace PcCore.NativeInput;

/// <summary>
/// The contract between PC Core and the Nexus Input Broker.
/// <para>
/// <b>The whole protocol is here, and it is deliberately tiny.</b> The broker runs at a higher
/// integrity level than anything else Nexus ships, so the only defensible design is one where the
/// complete list of things it can be asked to do fits on a screen: move a pointer, press a button,
/// turn a wheel, press an allow-listed key, type text, drive a touch contact, let go of everything,
/// and describe itself. There is no "run", no "open", no file access, no window handle, no
/// pass-through of arbitrary Win32 arguments, and no way to add one without editing this file.
/// </para>
/// <para>
/// It is local only. The pipe is bound to the owner's own SID and never leaves the machine; the
/// broker knows nothing about Tailscale, pairing or the phone. Deciding that a request came from
/// the authenticated Primary Phone stays entirely with PC Core, which is the only thing that can
/// know it.
/// </para>
/// </summary>
public static class BrokerProtocol
{
    /// <summary>
    /// The protocol version. A broker and a PC Core that disagree refuse each other rather than
    /// guessing, and PC Core then falls back to injecting input itself.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Longest frame either side will read. Comfortably above the largest legal request — one
    /// <c>input.text</c> message of 4096 characters — and far below anything that could be used to
    /// make the broker allocate.
    /// </summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>
    /// The pipe name for one user.
    /// <para>
    /// Per-SID because the broker is per-logon-session and per-owner. Two accounts signed in at once
    /// get two brokers and two pipes, and neither can see the other's.
    /// </para>
    /// </summary>
    public static string PipeName(string userSid) => $"Nexus.InputBroker.v{Version}.{userSid}";

    public static class Ops
    {
        /// <summary>Identify the broker and report what it can do. Always the first message.</summary>
        public const string Hello = "hello";

        public const string MoveAbsolute = "move_abs";
        public const string MoveRelative = "move_rel";
        public const string Button = "button";
        public const string Scroll = "scroll";
        public const string Key = "key";
        public const string Text = "text";
        public const string Touch = "touch";
        public const string ReleaseAll = "release_all";

        /// <summary>Which desktop is receiving input right now. Used to surface the Secure Desktop.</summary>
        public const string Desktop = "desktop";

        /// <summary>The complete set. Anything else is refused without being parsed further.</summary>
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            Hello, MoveAbsolute, MoveRelative, Button, Scroll, Key, Text, Touch, ReleaseAll, Desktop,
        };
    }

    public static class Phases
    {
        public const string Down = "DOWN";
        public const string Move = "MOVE";
        public const string Up = "UP";
        public const string Cancel = "CANCEL";

        public static TouchPhase? Parse(string? value) => value switch
        {
            Down => TouchPhase.Down,
            Move => TouchPhase.Move,
            Up => TouchPhase.Up,
            Cancel => TouchPhase.Cancel,
            _ => null,
        };
    }

    public static class Deliveries
    {
        public const string Delivered = "DELIVERED";
        public const string BlockedByIntegrity = "BLOCKED_BY_INTEGRITY";
        public const string BlockedBySecureDesktop = "BLOCKED_BY_SECURE_DESKTOP";
        public const string Unsupported = "UNSUPPORTED";
        public const string Failed = "FAILED";

        public static string From(NativeDelivery delivery) => delivery switch
        {
            NativeDelivery.Delivered => Delivered,
            NativeDelivery.BlockedByIntegrity => BlockedByIntegrity,
            NativeDelivery.BlockedBySecureDesktop => BlockedBySecureDesktop,
            NativeDelivery.Unsupported => Unsupported,
            _ => Failed,
        };

        public static NativeDelivery To(string? delivery) => delivery switch
        {
            Delivered => NativeDelivery.Delivered,
            BlockedByIntegrity => NativeDelivery.BlockedByIntegrity,
            BlockedBySecureDesktop => NativeDelivery.BlockedBySecureDesktop,
            Unsupported => NativeDelivery.Unsupported,
            _ => NativeDelivery.Failed,
        };
    }
}

/// <summary>
/// One request. Every field is a bounded scalar; there is nowhere to put a pointer, a handle or a
/// path, which is the point.
/// </summary>
public sealed record BrokerRequest
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = BrokerProtocol.Version;

    /// <summary>Matched on the reply, so a desynchronised pipe is detected rather than misread.</summary>
    [JsonPropertyName("seq")]
    public long Sequence { get; init; }

    [JsonPropertyName("op")]
    public string Op { get; init; } = string.Empty;

    /// <summary>Virtual-desktop pixels for a move or a touch. Negative is legal on multi-monitor.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    [JsonPropertyName("dx")]
    public int Dx { get; init; }

    [JsonPropertyName("dy")]
    public int Dy { get; init; }

    /// <summary>Wheel notches.</summary>
    [JsonPropertyName("h")]
    public int Horizontal { get; init; }

    [JsonPropertyName("vt")]
    public int Vertical { get; init; }

    /// <summary>0 left, 1 right, 2 middle.</summary>
    [JsonPropertyName("b")]
    public int Button { get; init; }

    /// <summary>Touch contact id.</summary>
    [JsonPropertyName("c")]
    public int Contact { get; init; }

    [JsonPropertyName("down")]
    public bool Down { get; init; }

    /// <summary>The extended-key flag, which the arrow and navigation keys need.</summary>
    [JsonPropertyName("ext")]
    public bool Extended { get; init; }

    /// <summary>Virtual-key code. Checked against the allow-list on arrival, never trusted.</summary>
    [JsonPropertyName("vk")]
    public int VirtualKey { get; init; }

    /// <summary>Touch phase: DOWN, MOVE, UP or CANCEL.</summary>
    [JsonPropertyName("p")]
    public string? Phase { get; init; }

    [JsonPropertyName("t")]
    public string? Text { get; init; }
}

public sealed record BrokerResponse
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = BrokerProtocol.Version;

    [JsonPropertyName("seq")]
    public long Sequence { get; init; }

    [JsonPropertyName("d")]
    public string Delivery { get; init; } = BrokerProtocol.Deliveries.Delivered;

    /// <summary>Short and safe to show. Never a stack trace, never a path.</summary>
    [JsonPropertyName("m")]
    public string? Diagnostic { get; init; }

    /// <summary>Populated only by <c>hello</c> and <c>desktop</c>.</summary>
    [JsonPropertyName("info")]
    public BrokerInfo? Info { get; init; }

    /// <summary>Populated only by <c>release_all</c>.</summary>
    [JsonPropertyName("rel")]
    public int Released { get; init; }
}

/// <summary>What the broker says about itself, so PC Core can report an honest status.</summary>
public sealed record BrokerInfo
{
    [JsonPropertyName("ver")]
    public string BrokerVersion { get; init; } = string.Empty;

    [JsonPropertyName("pid")]
    public int ProcessId { get; init; }

    /// <summary>
    /// True when the broker is running at high integrity, which is what lets it reach the windows of
    /// ordinary elevated applications. False means it started, but has no more reach than PC Core.
    /// </summary>
    [JsonPropertyName("elev")]
    public bool Elevated { get; init; }

    [JsonPropertyName("touch")]
    public bool TouchAvailable { get; init; }

    /// <summary>True when the machine has a real pen or touch digitizer of its own.</summary>
    [JsonPropertyName("digi")]
    public bool PhysicalDigitizer { get; init; }

    /// <summary>DEFAULT, SECURE, OTHER or UNKNOWN.</summary>
    [JsonPropertyName("desk")]
    public string Desktop { get; init; } = string.Empty;
}
