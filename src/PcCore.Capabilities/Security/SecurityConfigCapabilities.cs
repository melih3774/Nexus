using PcCore.Capabilities.Abstractions;
using PcCore.Capabilities.Security.Evidence;
using PcCore.Capabilities.Security.Sessions;
using PcCore.Protocol;

namespace PcCore.Capabilities.Security;

public sealed record SecurityConfigResult
{
    public required bool DetectionEnabled { get; init; }
    public required ScreenEvidenceMode Screen { get; init; }
    public required CameraEvidenceMode Camera { get; init; }
    public required AudioEvidenceMode Audio { get; init; }
    public required int RetentionDays { get; init; }

    /// <summary>Whether the machine can prompt for Windows Hello. Shown so the owner knows what gates exist.</summary>
    public required bool OwnerVerificationAvailable { get; init; }

    public required bool DetectionRunning { get; init; }
    public string? DetectionDiagnostic { get; init; }

    public required IReadOnlyList<EvidenceDevice> Cameras { get; init; }
    public required IReadOnlyList<EvidenceDevice> Microphones { get; init; }

    /// <summary>Evidence currently on disk, so the owner can see what retention is holding.</summary>
    public required long EvidenceBytes { get; init; }
}

/// <summary>
/// Assembles the security status: settings, plus what the machine can actually do.
/// <para>
/// Shared by the read and write actions, which both answer with the same shape — a setter that
/// returns the resulting state saves the caller a second round trip to find out what it got.
/// </para>
/// </summary>
public sealed class SecurityStatusReader
{
    private readonly SecurityConfigurationStore _configuration;
    private readonly IOwnerVerifier _verifier;
    private readonly Detection.ISecurityActivityDetector _detector;
    private readonly ICameraEvidenceSource _camera;
    private readonly IAudioEvidenceSource _audio;
    private readonly ISecuritySessionStore _store;

    public SecurityStatusReader(
        SecurityConfigurationStore configuration,
        IOwnerVerifier verifier,
        Detection.ISecurityActivityDetector detector,
        ICameraEvidenceSource camera,
        IAudioEvidenceSource audio,
        ISecuritySessionStore store)
    {
        _configuration = configuration;
        _verifier = verifier;
        _detector = detector;
        _camera = camera;
        _audio = audio;
        _store = store;
    }

    public async ValueTask<SecurityConfigResult> ReadAsync(CancellationToken cancellationToken)
    {
        var current = _configuration.Current;

        return new SecurityConfigResult
        {
            DetectionEnabled = current.DetectionEnabled,
            Screen = current.Screen,
            Camera = current.Camera,
            Audio = current.Audio,
            RetentionDays = current.RetentionDays,
            OwnerVerificationAvailable = await _verifier.IsAvailableAsync(cancellationToken).ConfigureAwait(false),
            DetectionRunning = _detector.IsRunning,
            DetectionDiagnostic = _detector.Diagnostic,
            Cameras = await _camera.ListDevicesAsync(cancellationToken).ConfigureAwait(false),
            Microphones = await _audio.ListDevicesAsync(cancellationToken).ConfigureAwait(false),
            EvidenceBytes = _store.EvidenceBytes(),
        };
    }
}

/// <summary>
/// Reads the security configuration.
/// <para>
/// Local-only, like its setter. Reading is harmless, but keeping the pair together means there is
/// one answer to "can the phone touch security configuration" rather than two.
/// </para>
/// </summary>
public sealed class GetSecurityConfigAction : CapabilityAction<NoParameters, SecurityConfigResult>
{
    private readonly SecurityStatusReader _reader;

    public GetSecurityConfigAction(SecurityStatusReader reader) => _reader = reader;

    public override string Name => "security.config.get";

    public override string Description => "The owner's security detection and evidence settings.";

    protected override ValueTask<SecurityConfigResult> ExecuteAsync(
        NoParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken) => _reader.ReadAsync(cancellationToken);
}

public sealed record SetSecurityConfigParameters
{
    public bool? DetectionEnabled { get; init; }
    public ScreenEvidenceMode? Screen { get; init; }
    public CameraEvidenceMode? Camera { get; init; }
    public AudioEvidenceMode? Audio { get; init; }
    public string? CameraDeviceId { get; init; }
    public string? AudioDeviceId { get; init; }
    public int? RetentionDays { get; init; }
}

/// <summary>
/// Changes the security configuration.
/// <para>
/// Local-only, and gated on Windows Hello when the change <em>weakens</em> protection — turning
/// detection off, disabling an evidence source, shortening retention. Strengthening it is not gated,
/// because making the machine record more of the owner's own activity needs no protection from the
/// owner.
/// </para>
/// <para>
/// Every accepted change is written to the security audit timeline, so a configuration that was
/// quietly weakened before an incident leaves a trace.
/// </para>
/// </summary>
public sealed class SetSecurityConfigAction : CapabilityAction<SetSecurityConfigParameters, SecurityConfigResult>
{
    private readonly SecurityConfigurationStore _configuration;
    private readonly IOwnerVerifier _verifier;
    private readonly SecurityIncidentService _incidents;
    private readonly SecurityStatusReader _reader;

    public SetSecurityConfigAction(
        SecurityConfigurationStore configuration,
        IOwnerVerifier verifier,
        SecurityIncidentService incidents,
        SecurityStatusReader reader)
    {
        _configuration = configuration;
        _verifier = verifier;
        _incidents = incidents;
        _reader = reader;
    }

    public override string Name => "security.config.set";

    public override string Description => "Change security settings. Weakening protection requires Windows Hello.";

    protected override async ValueTask<SecurityConfigResult> ExecuteAsync(
        SetSecurityConfigParameters parameters,
        CapabilityContext context,
        CancellationToken cancellationToken)
    {
        var current = _configuration.Current;

        if (parameters.RetentionDays is < 1 or > 3650)
        {
            throw new CapabilityException(ErrorCodes.InvalidParameters, "Retention must be between 1 and 3650 days.");
        }

        var proposed = current with
        {
            DetectionEnabled = parameters.DetectionEnabled ?? current.DetectionEnabled,
            Screen = parameters.Screen ?? current.Screen,
            Camera = parameters.Camera ?? current.Camera,
            Audio = parameters.Audio ?? current.Audio,
            CameraDeviceId = parameters.CameraDeviceId ?? current.CameraDeviceId,
            AudioDeviceId = parameters.AudioDeviceId ?? current.AudioDeviceId,
            RetentionDays = parameters.RetentionDays ?? current.RetentionDays,
        };

        var changes = current.Differences(proposed);

        if (changes.Count == 0)
        {
            return await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        var verification = "not required";

        if (current.WeakensProtection(proposed))
        {
            var result = await _verifier
                .VerifyAsync("Reduce Nexus security protection", cancellationToken)
                .ConfigureAwait(false);

            if (result != OwnerVerificationResult.Verified)
            {
                throw new CapabilityException(
                    ErrorCodes.NotAuthorized,
                    result == OwnerVerificationResult.Unavailable
                        ? "Windows Hello is not set up on this PC, so security protection cannot be reduced."
                        : "Owner verification was not completed.");
            }

            verification = "Windows Hello";
        }

        _configuration.Update(proposed);

        // Recorded as one audit line per change, with the verification outcome but never how it was
        // performed and never anything biometric.
        foreach (var change in changes)
        {
            _incidents.RecordAudit(
                "audit.config_changed",
                $"Security configuration changed. {change}. Owner verified: {verification}");
        }

        return await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}
