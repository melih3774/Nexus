using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcAssistant.Elevation;

/// <summary>
/// The wire contract between unelevated PC Assistant and the privileged helper service.
///
/// One JSON line in each direction over a local named pipe. The pipe name is deliberately
/// stable across versions so a mismatch is reported as a mismatch instead of appearing as
/// "helper not installed": both sides compare <see cref="Version"/> explicitly and an
/// unequal value refuses every privileged operation.
/// </summary>
public static class PrivilegedProtocol
{
    /// <summary>
    /// Incremented on any breaking change to request or response shape. The helper accepts
    /// exactly this value: an older helper therefore rejects a newer client's protocol
    /// rather than guessing at fields it does not know.
    /// </summary>
    public const int Version = 1;

    /// <summary>Local pipe only. Never a network endpoint.</summary>
    public const string PipeName = "PcAssistant.Elevation";

    /// <summary>Windows service name created by the owner-run installer.</summary>
    public const string ServiceName = "PcAssistantElevation";

    public const string ServiceDisplayName = "PC Assistant Privileged Operations";

    /// <summary>A single request or response line may not exceed this. Enforced on both sides.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>
    /// Captured stdout/stderr are each truncated to this before they cross the pipe. Sized so
    /// that both streams plus JSON escaping still fit inside one <see cref="MaxMessageBytes"/>
    /// message; command output beyond this is rarely what a decision turns on anyway.
    /// </summary>
    public const int MaxCapturedCharacters = 12 * 1024;

    /// <summary>A request whose clock differs from the helper's by more than this is stale.</summary>
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(2);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
