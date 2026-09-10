namespace PcCore.SecureCapture;

/// <summary>
/// Everything the privileged helper and PC Core agree on. Compiled into both.
/// <para>
/// <b>This file is the entire attack surface of the SYSTEM component</b>, which is why it is short
/// and why it is one file rather than a shape spread over a serializer. There is one request on the
/// control pipe — "start a capture worker for the console session" — and two message kinds on the
/// frame pipe. The helper exposes no way to run anything, read anything, or write anything.
/// </para>
/// </summary>
internal static class SecureCaptureProtocol
{
    /// <summary>The service's control pipe. One well-known name; there is one console session.</summary>
    public const string ControlPipeName = "Nexus.SecureCapture.Control";

    /// <summary>Prefix every frame pipe must use, so the helper cannot be pointed at an arbitrary name.</summary>
    public const string FramePipePrefix = "Nexus.SecureCapture.Frames.";

    public const int Version = 1;

    /// <summary>Magic on the frame pipe, so a wrong-protocol peer is rejected before anything is parsed.</summary>
    public const uint FrameMagic = 0x4353584E; // "NXSC"

    /// <summary>Client to worker.</summary>
    public static class Commands
    {
        /// <summary>Send one frame of whatever desktop is currently receiving input.</summary>
        public const byte Capture = 1;

        /// <summary>Finish and exit.</summary>
        public const byte Stop = 2;
    }

    /// <summary>Worker to client.</summary>
    public static class Replies
    {
        /// <summary>A BGRA frame follows.</summary>
        public const byte Frame = 1;

        /// <summary>No frame; a reason string follows.</summary>
        public const byte Unavailable = 2;
    }

    /// <summary>The largest frame the client will accept, so a bad length cannot allocate the machine away.</summary>
    public const int MaxFrameBytes = 8192 * 8192 * 4;

    public const int NonceBytes = 32;

    /// <summary>
    /// True for a frame-pipe name the helper is willing to connect to.
    /// <para>
    /// The name comes from a request on the control pipe, so it is attacker-influenced input in the
    /// same sense any protocol field is. Constraining it to one prefix and a hex suffix means the
    /// helper cannot be talked into connecting to a pipe somebody else is serving under a name
    /// chosen to look like something else.
    /// </para>
    /// </summary>
    public static bool IsValidFramePipeName(string? name) =>
        name is not null
        && name.Length > FramePipePrefix.Length
        && name.Length <= FramePipePrefix.Length + 64
        && name.StartsWith(FramePipePrefix, StringComparison.Ordinal)
        && name[FramePipePrefix.Length..].All(static c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
}
