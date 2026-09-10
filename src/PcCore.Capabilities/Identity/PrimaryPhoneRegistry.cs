using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Storage;

namespace PcCore.Capabilities.Identity;

/// <summary>
/// The paired phone, as anyone outside the registry may see it. There is deliberately no secret
/// on this type: the credential never leaves <see cref="PrimaryPhoneRegistry"/> after pairing.
/// </summary>
public sealed record PrimaryPhone(
    string DeviceId,
    string DisplayName,
    DateTimeOffset PairedAtUtc,
    string? OwnerPublicKey = null,
    DateTimeOffset? OwnerKeyRegisteredAtUtc = null)
{
    /// <summary>True once the phone has registered the biometric-gated key it authorises sessions with.</summary>
    public bool HasOwnerKey => !string.IsNullOrWhiteSpace(OwnerPublicKey);
}

/// <summary>Returned exactly once, at pairing time, so the phone can store its copy.</summary>
public sealed record PairedCredential(PrimaryPhone Phone, string SharedSecret);

/// <summary>On-disk shape. The secret is a DPAPI blob, never plaintext.</summary>
public sealed record PrimaryPhoneRecord
{
    public int SchemaVersion { get; init; } = 1;
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required DateTimeOffset PairedAtUtc { get; init; }

    /// <summary>Base64 of the DPAPI-protected shared secret. Unreadable outside this Windows account.</summary>
    public required string ProtectedSecret { get; init; }

    /// <summary>
    /// Base64 of the phone's owner-authentication public key, in X.509 SubjectPublicKeyInfo form.
    /// <para>
    /// A public key, so it is stored in the clear: it verifies signatures and cannot make them. The
    /// private half is a non-exportable Android Keystore key that the platform refuses to use until
    /// the owner has satisfied a system biometric or device-credential prompt, which is the entire
    /// reason a signature from it means anything.
    /// </para>
    /// </summary>
    public string? OwnerPublicKey { get; init; }

    public DateTimeOffset? OwnerKeyRegisteredAtUtc { get; init; }
}

/// <summary>
/// Exactly one Primary Phone, or none.
/// <para>
/// This is a personal-use system: one owner, one PC, one phone. There is no device table, no
/// fleet, and no per-device policy — replacing the phone is an explicit owner action, not a
/// second row. That is a deliberate simplification, not an oversight.
/// </para>
/// <para>
/// <b>The shared secret never leaves this class</b> except as the return value of
/// <see cref="Pair"/>, which is the one moment the phone must receive it. Verification happens
/// here via <see cref="VerifyProof"/> so no caller ever holds the credential.
/// </para>
/// </summary>
public interface IPrimaryPhoneRegistry
{
    /// <summary>The paired phone, or null.</summary>
    PrimaryPhone? Current { get; }

    /// <summary>
    /// Mints a device id and a fresh 256-bit shared secret, persists them, and returns the secret
    /// for delivery to the phone. Any previously paired phone is replaced and its secret destroyed.
    /// </summary>
    PairedCredential Pair(string displayName);

    /// <summary>Removes the paired phone and its secret. Returns false if nothing was paired.</summary>
    bool Unpair();

    /// <summary>
    /// Constant-time check that <paramref name="proof"/> is an HMAC-SHA-256 of
    /// <paramref name="message"/> under the stored secret for <paramref name="deviceId"/>.
    /// </summary>
    bool VerifyProof(string deviceId, ReadOnlySpan<byte> message, ReadOnlySpan<byte> proof);

    /// <summary>
    /// Records the phone's owner-authentication public key.
    /// <para>
    /// Returns false when the device id is not the paired one, or when a <em>different</em> key is
    /// already registered — replacing one is not something an authenticated connection may do on its
    /// own, because a phone in the wrong hands is authenticated too. Use
    /// <see cref="ClearOwnerKey"/> at the PC to allow a fresh registration.
    /// </para>
    /// </summary>
    bool RegisterOwnerKey(string deviceId, string publicKeySpki);

    /// <summary>Forgets the registered owner key, so the phone may register a new one. Local-only.</summary>
    bool ClearOwnerKey();
}

public sealed class PrimaryPhoneRegistry : IPrimaryPhoneRegistry
{
    public const string FileName = "primary-phone.json";

    /// <summary>256 bits, per the milestone requirement. Not a PIN; never human-generated.</summary>
    private const int SharedSecretBytes = 32;

    private const int DeviceIdBytes = 16;

    private readonly AtomicJsonFileStore<PrimaryPhoneRecord> _store;
    private readonly ISecretProtector _protector;
    private readonly ILogger<PrimaryPhoneRegistry> _logger;
    private readonly Lock _gate = new();

    private PrimaryPhone? _phone;
    private byte[]? _secret;
    private bool _loaded;

    /// <summary>
    /// The record exactly as it sits on disk, kept so a later edit can rewrite it without needing to
    /// re-protect the secret. Registering the owner key changes one field of a file whose other
    /// field is a DPAPI blob this class deliberately never re-derives.
    /// </summary>
    private PrimaryPhoneRecord? _record;

    public PrimaryPhoneRegistry(
        StateDirectory directory,
        ISecretProtector protector,
        ILogger<PrimaryPhoneRegistry> logger)
    {
        _store = new AtomicJsonFileStore<PrimaryPhoneRecord>(directory, FileName, logger);
        _protector = protector;
        _logger = logger;
    }

    public PrimaryPhone? Current
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _phone;
            }
        }
    }

    public PairedCredential Pair(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        lock (_gate)
        {
            EnsureLoaded();

            var secret = RandomNumberGenerator.GetBytes(SharedSecretBytes);
            var phone = new PrimaryPhone(
                Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(DeviceIdBytes)),
                displayName.Trim(),
                DateTimeOffset.UtcNow);

            var record = new PrimaryPhoneRecord
            {
                DeviceId = phone.DeviceId,
                DisplayName = phone.DisplayName,
                PairedAtUtc = phone.PairedAtUtc,
                ProtectedSecret = Convert.ToBase64String(_protector.Protect(secret)),
            };

            _store.Save(record);

            if (_secret is not null)
            {
                CryptographicOperations.ZeroMemory(_secret);
            }

            _phone = phone;
            _secret = secret;
            _record = record;
            _loaded = true;

            // Device id only. The secret is never logged, at any level.
            _logger.LogInformation(
                "Paired primary phone {DeviceId} ({DisplayName}).", phone.DeviceId, phone.DisplayName);

            return new PairedCredential(phone, Base64Url.EncodeToString(secret));
        }
    }

    public bool Unpair()
    {
        lock (_gate)
        {
            EnsureLoaded();

            if (_phone is null)
            {
                return false;
            }

            var deviceId = _phone.DeviceId;

            _store.Delete();

            if (_secret is not null)
            {
                CryptographicOperations.ZeroMemory(_secret);
            }

            _phone = null;
            _secret = null;
            _record = null;

            _logger.LogInformation("Unpaired primary phone {DeviceId}. Its credential no longer authenticates.", deviceId);
            return true;
        }
    }

    public bool VerifyProof(string deviceId, ReadOnlySpan<byte> message, ReadOnlySpan<byte> proof)
    {
        byte[] expected;

        lock (_gate)
        {
            EnsureLoaded();

            if (_phone is null || _secret is null)
            {
                return false;
            }

            // The device id is an identifier, never a credential: matching it proves nothing on its
            // own, and failing to match simply means there is no secret to check against.
            if (!string.Equals(_phone.DeviceId, deviceId, StringComparison.Ordinal))
            {
                return false;
            }

            expected = HMACSHA256.HashData(_secret, message);
        }

        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }

    public bool RegisterOwnerKey(string deviceId, string publicKeySpki)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeySpki);

        lock (_gate)
        {
            EnsureLoaded();

            if (_phone is null || _record is null
                || !string.Equals(_phone.DeviceId, deviceId, StringComparison.Ordinal))
            {
                return false;
            }

            if (_phone.OwnerPublicKey is { Length: > 0 } existing)
            {
                // Idempotent for the same key, refused for a different one. A phone that reinstalls
                // Nexus or has its Keystore key invalidated by a new fingerprint enrollment cannot
                // silently swap in a new one: that would make the biometric gate defeatable by
                // anyone holding the phone and its stored credential, which is the exact pair an
                // attacker has. The owner clears it at the PC.
                return string.Equals(existing, publicKeySpki, StringComparison.Ordinal);
            }

            var record = _record with
            {
                OwnerPublicKey = publicKeySpki,
                OwnerKeyRegisteredAtUtc = DateTimeOffset.UtcNow,
            };

            _store.Save(record);
            _record = record;
            _phone = _phone with
            {
                OwnerPublicKey = record.OwnerPublicKey,
                OwnerKeyRegisteredAtUtc = record.OwnerKeyRegisteredAtUtc,
            };

            _logger.LogInformation(
                "Registered the owner-authentication key for phone {DeviceId}.", _phone.DeviceId);

            return true;
        }
    }

    public bool ClearOwnerKey()
    {
        lock (_gate)
        {
            EnsureLoaded();

            if (_phone is null || _record is null || !_phone.HasOwnerKey)
            {
                return false;
            }

            var record = _record with { OwnerPublicKey = null, OwnerKeyRegisteredAtUtc = null };

            _store.Save(record);
            _record = record;
            _phone = _phone with { OwnerPublicKey = null, OwnerKeyRegisteredAtUtc = null };

            _logger.LogWarning(
                "Cleared the owner-authentication key for phone {DeviceId}; it must register a new one "
                + "before it can control this PC again.",
                _phone.DeviceId);

            return true;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        var result = _store.Load();

        if (result.Status == StateLoadStatus.Corrupt)
        {
            // Fail closed: an unreadable pairing record means no paired phone, so nothing
            // authenticates until the owner pairs again. The file has already been quarantined.
            _logger.LogError("Primary phone record is unusable. Treating the system as unpaired.");
            return;
        }

        if (!result.IsLoaded)
        {
            return;
        }

        var record = result.Value!;
        byte[]? secret;

        try
        {
            secret = _protector.Unprotect(Convert.FromBase64String(record.ProtectedSecret));
        }
        catch (FormatException)
        {
            secret = null;
        }

        if (secret is null || secret.Length == 0)
        {
            // The blob exists but cannot be unprotected — a different Windows account, a restored
            // profile, or tampering. Without the secret the record is useless, so stay unpaired.
            _logger.LogError(
                "Stored phone credential could not be unprotected for this Windows account. "
                + "Treating the system as unpaired; re-pair the phone to restore access.");
            return;
        }

        _phone = new PrimaryPhone(
            record.DeviceId,
            record.DisplayName,
            record.PairedAtUtc,
            record.OwnerPublicKey,
            record.OwnerKeyRegisteredAtUtc);
        _secret = secret;
        _record = record;

        _logger.LogInformation("Loaded primary phone {DeviceId} ({DisplayName}).", record.DeviceId, record.DisplayName);
    }
}
