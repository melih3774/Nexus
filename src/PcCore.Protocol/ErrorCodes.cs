namespace PcCore.Protocol;

/// <summary>
/// The closed set of machine-readable error codes. Callers switch on these strings;
/// <see cref="ProtocolError.Message"/> is for humans and must never be parsed.
/// </summary>
public static class ErrorCodes
{
    /// <summary>Envelope malformed, unparseable, or missing a required field.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>The stated <c>protocol_version</c> is not understood by this build.</summary>
    public const string UnsupportedProtocolVersion = "unsupported_protocol_version";

    /// <summary>No capability is registered for the requested action.</summary>
    public const string UnknownAction = "unknown_action";

    /// <summary>Parameters were missing, of the wrong shape, or failed validation.</summary>
    public const string InvalidParameters = "invalid_parameters";

    /// <summary>The path resolved outside every configured allowed root.</summary>
    public const string PathNotAllowed = "path_not_allowed";

    /// <summary>The target does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>The operating system refused access (ACL, lock, or file in use).</summary>
    public const string AccessDenied = "access_denied";

    /// <summary>The action is valid but unavailable in this environment.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>
    /// A state change was requested that the state machine does not permit from the current state.
    /// The request is refused outright: an invalid transition is never quietly rewritten into some
    /// other valid one.
    /// </summary>
    public const string InvalidStateTransition = "invalid_state_transition";

    /// <summary>A primary phone is already paired and replacement was not requested explicitly.</summary>
    public const string AlreadyPaired = "already_paired";

    /// <summary>
    /// Pairing could not be completed. Deliberately non-specific: distinguishing "wrong secret"
    /// from "expired" from "no session" would let an unpaired party map the pairing machine.
    /// </summary>
    public const string PairingFailed = "pairing_failed";

    /// <summary>
    /// A device failed to prove possession of the shared secret. Also deliberately non-specific.
    /// </summary>
    public const string AuthenticationFailed = "authentication_failed";

    /// <summary>
    /// The caller is authenticated but not permitted to invoke this action. Distinct from
    /// <see cref="UnknownAction"/> on purpose: a remote device being told "you may not" rather than
    /// "no such thing" makes the denial diagnosable, and the action names are not secret.
    /// </summary>
    public const string NotAuthorized = "not_authorized";

    /// <summary>
    /// The caller is sending faster than the action permits. Distinct from
    /// <see cref="NotAuthorized"/> because it is a "slow down", not a "you may not": the same caller
    /// making the same request a moment later succeeds. Used by remote input, where a client stuck
    /// in a loop would otherwise saturate both the link and the desktop.
    /// </summary>
    public const string RateLimited = "rate_limited";

    /// <summary>
    /// The phone is authenticated, but its app is older than this PC Core serves. Only
    /// <c>update.check</c> and <c>update.apk.get</c> remain available to it, so it can fetch the
    /// build that is not. Distinct from <see cref="NotAuthorized"/> because the remedy is different:
    /// updating the app, not authenticating the owner.
    /// </summary>
    public const string UpdateRequired = "update_required";

    /// <summary>Something is already at the path a write would create, and replacing it was not asked for.</summary>
    public const string AlreadyExists = "already_exists";

    /// <summary>
    /// Another program holds the file open in a way that excludes this one. Distinct from
    /// <see cref="AccessDenied"/> because the remedy is different: close the program, not change permissions.
    /// </summary>
    public const string FileInUse = "file_in_use";

    /// <summary>The file changed on the PC between two ranges of one transfer, so the ranges do not belong together.</summary>
    public const string FileChanged = "file_changed";

    /// <summary>The volume has no room for what is being written.</summary>
    public const string DiskFull = "disk_full";

    /// <summary>What arrived did not hash to what the sender said it sent. Nothing was kept.</summary>
    public const string ChecksumMismatch = "checksum_mismatch";

    /// <summary>The Windows clipboard could not be opened, usually because another program is holding it.</summary>
    public const string ClipboardUnavailable = "clipboard_unavailable";

    /// <summary>An unexpected fault. Details are deliberately suppressed.</summary>
    public const string InternalError = "internal_error";
}
