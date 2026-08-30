using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Security.Evidence;
using PcCore.Capabilities.Storage;

namespace PcCore.Capabilities.Security;

/// <summary>
/// What the owner has chosen the security subsystem should do.
/// <para>
/// Deliberately few settings. Every one of them changes what gets recorded about the owner's own
/// life, so each has to be understood to be set — and a page of thirty toggles is a page nobody
/// reads. Good defaults matter more than choice here.
/// </para>
/// </summary>
public sealed record SecurityConfiguration
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Turning this off means arming the PC no longer raises incidents.</summary>
    public bool DetectionEnabled { get; init; } = true;

    public ScreenEvidenceMode Screen { get; init; } = ScreenEvidenceMode.Screenshots;

    public CameraEvidenceMode Camera { get; init; } = CameraEvidenceMode.EventClips;

    public AudioEvidenceMode Audio { get; init; } = AudioEvidenceMode.EventClips;

    /// <summary>Which camera to use when the machine has more than one. Null means the default.</summary>
    public string? CameraDeviceId { get; init; }

    public string? AudioDeviceId { get; init; }

    public int RetentionDays { get; init; } = 30;

    /// <summary>
    /// Describes a change in words, for the audit timeline. Only what changed, never a credential.
    /// </summary>
    public IReadOnlyList<string> Differences(SecurityConfiguration other)
    {
        var changes = new List<string>();

        void Compare<T>(string name, T mine, T theirs)
        {
            if (!Equals(mine, theirs))
            {
                changes.Add($"{name}: {mine} -> {theirs}");
            }
        }

        Compare("Detection", DetectionEnabled ? "Enabled" : "Disabled", other.DetectionEnabled ? "Enabled" : "Disabled");
        Compare("Screen evidence", Screen, other.Screen);
        Compare("Camera evidence", Camera, other.Camera);
        Compare("Microphone evidence", Audio, other.Audio);
        Compare("Retention days", RetentionDays, other.RetentionDays);

        // Device ids are opaque strings that identify hardware, not the owner; the fact that the
        // selection changed is what matters for the audit line.
        if (CameraDeviceId != other.CameraDeviceId)
        {
            changes.Add("Camera device: changed");
        }

        if (AudioDeviceId != other.AudioDeviceId)
        {
            changes.Add("Microphone device: changed");
        }

        return changes;
    }

    /// <summary>True when a change reduces what is recorded, which is what owner verification gates.</summary>
    public bool WeakensProtection(SecurityConfiguration other) =>
        (DetectionEnabled && !other.DetectionEnabled)
        || (Screen != ScreenEvidenceMode.Disabled && other.Screen == ScreenEvidenceMode.Disabled)
        || (Camera != CameraEvidenceMode.Disabled && other.Camera == CameraEvidenceMode.Disabled)
        || (Audio != AudioEvidenceMode.Disabled && other.Audio == AudioEvidenceMode.Disabled)
        || other.RetentionDays < RetentionDays;
}

/// <summary>Reads and writes the owner's security configuration, persisted beside the incident history.</summary>
public sealed class SecurityConfigurationStore
{
    public const string FileName = "security-config.json";

    private readonly AtomicJsonFileStore<SecurityConfiguration> _store;
    private readonly ILogger<SecurityConfigurationStore> _logger;
    private readonly Lock _gate = new();

    private SecurityConfiguration _current;

    public SecurityConfigurationStore(StateDirectory directory, ILogger<SecurityConfigurationStore> logger)
    {
        _logger = logger;
        _store = new AtomicJsonFileStore<SecurityConfiguration>(directory, FileName, logger);

        var loaded = _store.Load();

        // A missing or corrupt file falls back to the protective defaults rather than to nothing.
        // Failing towards "record less" would be the wrong direction for a security subsystem.
        _current = loaded.IsLoaded ? loaded.Value! : new SecurityConfiguration();

        _logger.LogInformation(
            "Security configuration: detection {Detection}, screen {Screen}, camera {Camera}, microphone {Audio}, "
            + "retention {Days} days.",
            _current.DetectionEnabled ? "enabled" : "disabled",
            _current.Screen,
            _current.Camera,
            _current.Audio,
            _current.RetentionDays);
    }

    public SecurityConfiguration Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Stores a new configuration and returns what changed, for the audit timeline.</summary>
    public IReadOnlyList<string> Update(SecurityConfiguration configuration)
    {
        lock (_gate)
        {
            var changes = _current.Differences(configuration);

            if (changes.Count == 0)
            {
                return changes;
            }

            _current = configuration with { SchemaVersion = 1 };
            _store.Save(_current);

            _logger.LogInformation("Security configuration changed: {Changes}", string.Join("; ", changes));
            return changes;
        }
    }
}
