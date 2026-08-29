using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Storage;

namespace PcCore.Capabilities.Identity;

/// <summary>
/// Protects a small secret at rest.
/// <para>
/// Implemented on Windows by DPAPI (<c>CurrentUser</c> scope), so the ciphertext is bound to this
/// Windows account and is useless if the file is copied elsewhere. It is an interface so the
/// capability layer stays platform-neutral and tests can run without DPAPI.
/// </para>
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    /// <summary>Returns null if the blob cannot be unprotected — wrong user, tampering, or corruption.</summary>
    byte[]? Unprotect(byte[] ciphertext);
}

/// <summary>On-disk shape of the PC installation identity.</summary>
public sealed record PcIdentityRecord
{
    public int SchemaVersion { get; init; } = 1;
    public required string PcInstanceId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>
/// One stable, random identifier for this PC Core installation.
/// <para>
/// Deliberately <b>not</b> derived from any hardware or account identifier — no MAC address, no
/// motherboard serial, no Windows product id, no user name, no hostname. It is a random GUID whose
/// only job is to let a paired phone recognise which installation it is talking to. Deriving it
/// from hardware would make it a fingerprint that follows the owner across reinstalls and leaks
/// machine details into every pairing QR code.
/// </para>
/// <para>Generated once, then read from disk for the life of the installation.</para>
/// </summary>
public interface IPcIdentityProvider
{
    string PcInstanceId { get; }
}

public sealed class PcIdentityProvider : IPcIdentityProvider
{
    public const string FileName = "instance.json";

    private readonly Lazy<string> _instanceId;

    public PcIdentityProvider(StateDirectory directory, ILogger<PcIdentityProvider> logger)
    {
        var store = new AtomicJsonFileStore<PcIdentityRecord>(directory, FileName, logger);

        _instanceId = new Lazy<string>(() =>
        {
            var loaded = store.Load();

            if (loaded.IsLoaded && Guid.TryParse(loaded.Value!.PcInstanceId, out var existing) && existing != Guid.Empty)
            {
                logger.LogInformation("PC instance id {PcInstanceId}", existing);
                return existing.ToString();
            }

            if (loaded.Status == StateLoadStatus.Corrupt)
            {
                // A new id is the only option; the old file has already been quarantined. Say so
                // loudly, because a paired phone still holding the previous id will not match.
                logger.LogError(
                    "PC instance identity was unreadable and a new one is being generated. "
                    + "A previously paired phone will no longer recognise this installation.");
            }

            // Guid.NewGuid is a random (version 4) GUID from the OS CSPRNG. No machine
            // characteristic contributes to it.
            var created = Guid.NewGuid();
            store.Save(new PcIdentityRecord { PcInstanceId = created.ToString(), CreatedUtc = DateTimeOffset.UtcNow });

            logger.LogInformation("Generated PC instance id {PcInstanceId}", created);
            return created.ToString();
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string PcInstanceId => _instanceId.Value;
}
