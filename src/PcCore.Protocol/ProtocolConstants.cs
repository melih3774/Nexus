namespace PcCore.Protocol;

/// <summary>
/// Wire-level constants shared by every transport and every caller.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>
    /// The only protocol version v0.1 understands. A request that omits the field is
    /// treated as this version; a request that states a different one is rejected.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>Header carrying the per-run session token on loopback transports.</summary>
    public const string TokenHeader = "X-PcCore-Token";
}

/// <summary>Discriminator values for the <c>type</c> field of every envelope.</summary>
public static class MessageTypes
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
}
