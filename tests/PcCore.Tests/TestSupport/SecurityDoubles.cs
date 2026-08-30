using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Detection;
using PcCore.Capabilities.Security.Evidence;

namespace PcCore.Tests.TestSupport;

/// <summary>
/// A detector the test drives.
/// <para>
/// The real one is verified against this machine's actual hooks by the host self-test, which is the
/// only place the attribution can be proved. Everything above it — the state gate, the noise filter,
/// what an incident does — is ordinary logic, and this is what lets it be tested as such.
/// </para>
/// </summary>
public sealed class ScriptedActivityDetector : ISecurityActivityDetector
{
    public event Action<LocalActivity>? Observed;

    public bool IsRunning { get; private set; }

    public string? Diagnostic => IsRunning ? null : "not started";

    public bool Disposed { get; private set; }

    public void Start() => IsRunning = true;

    /// <summary>A person doing something.</summary>
    public void Local(ActivityKind kind = ActivityKind.KeyboardActivity, string? detail = null) =>
        Emit(new LocalActivity(kind, ActivitySource.Local, DateTimeOffset.UtcNow, detail));

    /// <summary>Input Nexus injected on the owner's behalf. Must never look like a person.</summary>
    public void FromNexus(ActivityKind kind = ActivityKind.PointerActivity) =>
        Emit(new LocalActivity(kind, ActivitySource.NexusRemote, DateTimeOffset.UtcNow));

    /// <summary>Input injected by some other program. Also not a person.</summary>
    public void Synthetic(ActivityKind kind = ActivityKind.PointerActivity) =>
        Emit(new LocalActivity(kind, ActivitySource.Synthetic, DateTimeOffset.UtcNow));

    /// <summary>Enough pointer movement to clear the noise threshold.</summary>
    public void LocalPointerMovement(int observations = 10)
    {
        for (var i = 0; i < observations; i++)
        {
            Local(ActivityKind.PointerActivity);
        }
    }

    public void Emit(LocalActivity activity) => Observed?.Invoke(activity);

    public void Dispose() => Disposed = true;
}

/// <summary>An evidence source the test controls: what it produces, and whether it works at all.</summary>
public sealed class FakeEvidenceSource : IScreenEvidenceSource, ICameraEvidenceSource, IAudioEvidenceSource
{
    private int _count;

    public FakeEvidenceSource(string name, string extension, string contentType)
    {
        Name = name;
        Extension = extension;
        ContentType = contentType;
    }

    public string Name { get; }

    public string Extension { get; }

    public string ContentType { get; }

    /// <summary>When false, every capture returns null — a camera another app owns, a muted mic.</summary>
    public bool Available { get; set; } = true;

    /// <summary>When true, capture throws instead of returning null. Must still not break the incident.</summary>
    public bool Throws { get; set; }

    /// <summary>When true, a file is created but left empty, as a failed encode would leave it.</summary>
    public bool ProducesEmptyFile { get; set; }

    public int Captures { get; private set; }

    public IReadOnlyList<EvidenceDevice> Devices { get; set; } =
        [new EvidenceDevice("device-1", "Test device", true)];

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Available);

    public ValueTask<IReadOnlyList<EvidenceDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Available ? Devices : []);

    public ValueTask<CapturedEvidence?> CaptureStillAsync(string folder, CancellationToken cancellationToken) =>
        CaptureAsync(folder, 0);

    public ValueTask<CapturedEvidence?> CaptureClipAsync(
        string folder,
        TimeSpan duration,
        CancellationToken cancellationToken) => CaptureAsync(folder, (long)duration.TotalMilliseconds);

    private ValueTask<CapturedEvidence?> CaptureAsync(string folder, long durationMs)
    {
        if (Throws)
        {
            throw new InvalidOperationException("the device failed");
        }

        if (!Available)
        {
            return ValueTask.FromResult<CapturedEvidence?>(null);
        }

        Captures++;

        var fileName = $"{Name}-{++_count}{Extension}";
        var path = Path.Combine(folder, fileName);

        File.WriteAllBytes(path, ProducesEmptyFile ? [] : System.Text.Encoding.UTF8.GetBytes($"{Name} {_count}"));

        return ValueTask.FromResult<CapturedEvidence?>(
            new CapturedEvidence(fileName, ContentType, durationMs, $"{Name} sample"));
    }
}

/// <summary>An owner verifier whose answer the test chooses.</summary>
public sealed class ScriptedOwnerVerifier : IOwnerVerifier
{
    public OwnerVerificationResult Result { get; set; } = OwnerVerificationResult.Verified;

    public bool Available { get; set; } = true;

    public int Prompts { get; private set; }

    public string? LastReason { get; private set; }

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Available);

    public ValueTask<OwnerVerificationResult> VerifyAsync(string reason, CancellationToken cancellationToken)
    {
        Prompts++;
        LastReason = reason;

        return ValueTask.FromResult(Result);
    }
}
