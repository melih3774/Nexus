using PcCore.Capabilities.Abstractions;

namespace PcCore.Capabilities.Identity;

/// <summary>
/// Result of <c>device.get_primary</c>. Carries no credential — there is no field here that could
/// ever hold the shared secret.
/// </summary>
public sealed record PrimaryPhoneResult
{
    public required bool Paired { get; init; }
    public string? DeviceId { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset? PairedAtUtc { get; init; }

    public static PrimaryPhoneResult From(PrimaryPhone? phone) => phone is null
        ? new PrimaryPhoneResult { Paired = false }
        : new PrimaryPhoneResult
        {
            Paired = true,
            DeviceId = phone.DeviceId,
            DisplayName = phone.DisplayName,
            PairedAtUtc = phone.PairedAtUtc,
        };
}

public sealed record BeginPairingParameters
{
    /// <summary>
    /// Required to replace a phone that is already paired. Defaults to false so a stray pairing
    /// attempt can never take over a working setup.
    /// </summary>
    public bool ReplaceExisting { get; init; }
}

/// <summary>
/// Result of <c>pairing.begin</c>. The payload is meant to be shown as a QR code and offered for
/// copying; it is single-use and expires.
/// </summary>
public sealed record BeginPairingResult
{
    public required string SessionId { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required string PcInstanceId { get; init; }

    /// <summary>The <c>NEXUS2.…</c> pairing code. Short-lived pairing material, not a permanent credential.</summary>
    public required string Payload { get; init; }

    /// <summary>The legacy <c>NEXUS1.…</c> code, for the loopback development simulator only.</summary>
    public required string PayloadV1 { get; init; }

    /// <summary>
    /// The address the code points at, or <see langword="null"/> when no Tailscale address is
    /// bound. A phone cannot use a code without one, so the owner is told here rather than
    /// discovering it on the phone.
    /// </summary>
    public string? RemoteEndpoint { get; init; }
}

public sealed record CancelPairingResult
{
    /// <summary>False when there was no active session — cancelling nothing is not an error.</summary>
    public required bool Cancelled { get; init; }
}

public sealed record UnpairResult
{
    /// <summary>False when no phone was paired. Unpairing is idempotent.</summary>
    public required bool Unpaired { get; init; }
}

public sealed class GetPrimaryDeviceAction : CapabilityAction<NoParameters, PrimaryPhoneResult>
{
    private readonly IPrimaryPhoneRegistry _registry;

    public GetPrimaryDeviceAction(IPrimaryPhoneRegistry registry) => _registry = registry;

    public override string Name => "device.get_primary";

    public override string Description => "Whether a primary phone is paired, and its non-secret details.";

    protected override ValueTask<PrimaryPhoneResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(PrimaryPhoneResult.From(_registry.Current));
}

/// <summary>
/// <c>pairing.begin</c> — the owner offers a pairing code.
/// <para>
/// Owner-facing, so it is an ordinary capability. <em>Completing</em> the pairing is not: the
/// phone is unauthenticated at that moment and goes through the device-facing surface instead.
/// </para>
/// </summary>
public sealed class BeginPairingAction : CapabilityAction<BeginPairingParameters, BeginPairingResult>
{
    private readonly IPairingService _pairing;

    public BeginPairingAction(IPairingService pairing) => _pairing = pairing;

    public override string Name => "pairing.begin";

    public override string Description => "Start a short-lived pairing session and return its QR/copyable payload.";

    protected override ValueTask<BeginPairingResult> ExecuteAsync(
        BeginPairingParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var session = _pairing.Begin(parameters.ReplaceExisting);

        return ValueTask.FromResult(new BeginPairingResult
        {
            SessionId = session.SessionId,
            ExpiresAtUtc = session.ExpiresAtUtc,
            PcInstanceId = session.PcInstanceId,
            Payload = session.Payload,
            PayloadV1 = session.PayloadV1,
            RemoteEndpoint = session.RemoteEndpoint,
        });
    }
}

public sealed class CancelPairingAction : CapabilityAction<NoParameters, CancelPairingResult>
{
    private readonly IPairingService _pairing;

    public CancelPairingAction(IPairingService pairing) => _pairing = pairing;

    public override string Name => "pairing.cancel";

    public override string Description => "Cancel the active pairing session.";

    protected override ValueTask<CancelPairingResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CancelPairingResult { Cancelled = _pairing.Cancel() });
}

/// <summary>
/// <c>device.unpair_primary</c> — remove the paired phone and destroy its credential.
/// <para>
/// Idempotent, like <c>security.disarm</c>: revoking access must never fail because it was
/// already revoked.
/// </para>
/// </summary>
public sealed class UnpairPrimaryDeviceAction : CapabilityAction<NoParameters, UnpairResult>
{
    private readonly IPrimaryPhoneRegistry _registry;
    private readonly IPairingService _pairing;

    public UnpairPrimaryDeviceAction(IPrimaryPhoneRegistry registry, IPairingService pairing)
    {
        _registry = registry;
        _pairing = pairing;
    }

    public override string Name => "device.unpair_primary";

    public override string Description => "Remove the primary phone and delete its stored credential.";

    protected override ValueTask<UnpairResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        // Any pairing session in flight is abandoned too: unpairing is a reset, and leaving a live
        // code outstanding would let it re-pair moments after the owner revoked access.
        _pairing.Cancel();

        return ValueTask.FromResult(new UnpairResult { Unpaired = _registry.Unpair() });
    }
}
