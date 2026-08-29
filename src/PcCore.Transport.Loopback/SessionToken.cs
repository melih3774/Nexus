using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PcCore.Transport.Loopback;

public sealed class LoopbackTransportOptions
{
    public const string SectionName = "PcCore:Transport";

    /// <summary>
    /// TCP port on the loopback interface. The bind <em>address</em> is deliberately not
    /// configurable — see <see cref="LoopbackTransport"/>.
    /// </summary>
    public int Port { get; set; } = 47811;

    /// <summary>
    /// Require the per-run session token on <c>/rpc</c> and <c>/ws</c>. Leave enabled unless you
    /// are debugging the transport itself.
    /// </summary>
    public bool RequireToken { get; set; } = true;

    /// <summary>Maximum accepted request body, in bytes.</summary>
    public int MaxRequestBytes { get; set; } = 64 * 1024;
}

/// <summary>
/// A per-run bearer token for the loopback transport.
/// <para>
/// Loopback is not an authorisation boundary. Every process on the machine — and every web page
/// the user's browser loads — can send requests to <c>127.0.0.1</c>. The token, together with the
/// <c>Host</c>/<c>Origin</c> checks in <see cref="LoopbackGuard"/>, is what stops a hostile web
/// page from driving PC Core through the user's browser.
/// </para>
/// <para>
/// It does <em>not</em> defend against another process running as the same user: such a process
/// can read the token file, and could do anything the user can do regardless. That is a
/// deliberate, documented limit of this milestone (SPEC.md 7.3).
/// </para>
/// </summary>
public sealed class SessionToken
{
    private readonly byte[] _tokenBytes;

    private SessionToken(string value)
    {
        Value = value;
        _tokenBytes = Encoding.UTF8.GetBytes(value);
    }

    public string Value { get; }

    /// <summary>Where the token was written, if it was.</summary>
    public string? FilePath { get; private set; }

    public static SessionToken Generate() =>
        new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    /// <summary>
    /// Compares in constant time. A naive <c>==</c> here leaks the token one byte at a time to a
    /// local attacker who can measure response timing.
    /// </summary>
    public bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(_tokenBytes, candidateBytes);
    }

    /// <summary>
    /// Persists the token so a separate CLI or test client can read it. Written under
    /// <c>%LOCALAPPDATA%</c>, which is already per-user protected.
    /// </summary>
    public void WriteToDisk(ILogger logger)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PcCore");

            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "session.token");
            File.WriteAllText(path, Value);
            FilePath = path;

            // The path is logged; the token itself never is.
            logger.LogInformation("Session token written to {TokenPath}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the session token file. Use the console value instead.");
        }
    }
}
