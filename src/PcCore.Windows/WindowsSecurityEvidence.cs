using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Security.Evidence;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Security.Credentials.UI;
using Windows.Storage.Streams;

namespace PcCore.Windows;

/// <summary>
/// Shared plumbing for the media evidence sources.
/// <para>
/// <b>Recordings go to a stream Nexus owns, not to a StorageFile.</b> Handing Windows a file to
/// record into means Windows decides when to let go of it, and it does not let go when the recording
/// stops — on this machine an MP4 was still locked several seconds after <c>StopRecordAsync</c> and
/// after the capture object was disposed, which made hashing the evidence fail. Recording into an
/// in-memory stream and writing the bytes here removes the question: the file is created, written and
/// closed by one owner, and is readable the instant it exists.
/// </para>
/// <para>
/// Clip lengths are bounded well below anything that would make buffering a problem — seconds of VGA
/// video or compressed audio, not minutes of it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class MediaEvidence
{
    /// <summary>Reads everything a capture wrote and saves it, returning the size on disk.</summary>
    public static async Task<long> SaveAsync(
        InMemoryRandomAccessStream stream,
        string path,
        CancellationToken cancellationToken)
    {
        var size = (uint)stream.Size;

        if (size == 0)
        {
            return 0;
        }

        var bytes = new byte[size];

        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync(size).AsTask(cancellationToken).ConfigureAwait(false);
            reader.ReadBytes(bytes);
        }

        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return bytes.Length;
    }
}

/// <summary>
/// Screen evidence, taken with the same one-shot capture the screenshot capability uses.
/// <para>
/// Deliberately <see cref="IScreenCapture"/> and not the live stream. Evidence must not depend on a
/// phone being connected — an incident happens whether or not anyone is watching — and a capture
/// path shared with streaming would mean the stream stopping could stop the recording of the very
/// event it was meant to document.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenEvidence : IScreenEvidenceSource
{
    private readonly IScreenCapture _capture;
    private readonly ILogger<WindowsScreenEvidence> _logger;

    public WindowsScreenEvidence(IScreenCapture capture, ILogger<WindowsScreenEvidence> logger)
    {
        _capture = capture;
        _logger = logger;
    }

    public string Name => "screen";

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public async ValueTask<CapturedEvidence?> CaptureStillAsync(string folder, CancellationToken cancellationToken)
    {
        try
        {
            // Bounded width: evidence is looked at on a phone, and a 4K PNG per screenshot would
            // fill the retention budget in an afternoon.
            var frame = _capture.Capture(DisplaySelection.Virtual, maxWidth: 1600);
            var fileName = $"screen-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.png";

            await File.WriteAllBytesAsync(Path.Combine(folder, fileName), frame.Png, cancellationToken)
                .ConfigureAwait(false);

            return new CapturedEvidence(
                fileName,
                "image/png",
                Note: $"{frame.Width}x{frame.Height} from {frame.SourceWidth}x{frame.SourceHeight}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Screen evidence could not be captured.");
            return null;
        }
    }
}

/// <summary>
/// Camera and microphone evidence, through the standard Windows media capture APIs.
/// <para>
/// <b>Why WinRT MediaCapture rather than driving the devices directly.</b> It is the interface
/// Windows expects an application to use, which means it respects everything the owner has already
/// told Windows: the privacy toggles in Settings, the per-app permissions, and the hardware indicator
/// light. A lower-level path could have avoided some of that, and avoiding it is exactly what this
/// project will not do. If the owner has denied camera access to desktop apps, this fails — and that
/// is correct behaviour, recorded in the timeline as unavailable.
/// </para>
/// <para>
/// A capture session is created and disposed per clip rather than held open. It costs about a second
/// each time, and it buys something worth more: the camera light is on only while recording is
/// actually happening, so the indicator tells the truth.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsCameraEvidence : ICameraEvidenceSource
{
    private readonly SecurityConfigurationStore _configuration;
    private readonly ILogger<WindowsCameraEvidence> _logger;

    public WindowsCameraEvidence(SecurityConfigurationStore configuration, ILogger<WindowsCameraEvidence> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string Name => "camera";

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        var devices = await ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        return devices.Count > 0;
    }

    public async ValueTask<IReadOnlyList<EvidenceDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken)
                .ConfigureAwait(false);

            var selected = _configuration.Current.CameraDeviceId;

            return [.. found.Select(d => new EvidenceDevice(
                d.Id,
                d.Name,
                selected is null ? d.IsDefault : d.Id == selected))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Cameras could not be enumerated.");
            return [];
        }
    }

    public ValueTask<CapturedEvidence?> CaptureStillAsync(string folder, CancellationToken cancellationToken) =>
        CaptureAsync(folder, TimeSpan.Zero, cancellationToken);

    public ValueTask<CapturedEvidence?> CaptureClipAsync(
        string folder,
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        CaptureAsync(folder, duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : duration, cancellationToken);

    private async ValueTask<CapturedEvidence?> CaptureAsync(
        string folder,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        MediaCapture? capture = null;

        try
        {
            var deviceId = await ResolveDeviceIdAsync(cancellationToken).ConfigureAwait(false);

            if (deviceId is null)
            {
                return null;
            }

            capture = new MediaCapture();

            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                PhotoCaptureSource = PhotoCaptureSource.Auto,
            }).AsTask(cancellationToken).ConfigureAwait(false);

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

            if (duration <= TimeSpan.Zero)
            {
                using var photo = new InMemoryRandomAccessStream();

                await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), photo)
                    .AsTask(cancellationToken).ConfigureAwait(false);

                var name = $"camera-{stamp}.jpg";
                var written = await MediaEvidence
                    .SaveAsync(photo, Path.Combine(folder, name), cancellationToken)
                    .ConfigureAwait(false);

                return written > 0 ? new CapturedEvidence(name, "image/jpeg") : null;
            }

            using var recording = new InMemoryRandomAccessStream();

            await capture
                .StartRecordToStreamAsync(MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga), recording)
                .AsTask(cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Stopped even when cancelled, so a shutdown mid-clip still leaves a finalised,
                // playable file rather than a truncated one.
                await capture.StopRecordAsync().AsTask(CancellationToken.None).ConfigureAwait(false);
            }

            var clipName = $"camera-{stamp}.mp4";
            var clipBytes = await MediaEvidence
                .SaveAsync(recording, Path.Combine(folder, clipName), CancellationToken.None)
                .ConfigureAwait(false);

            return clipBytes > 0
                ? new CapturedEvidence(clipName, "video/mp4", (long)duration.TotalMilliseconds)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A camera another application owns, a denied privacy permission, a device unplugged
            // mid-capture. All expected, none fatal.
            _logger.LogWarning("Camera evidence unavailable: {Reason}", ex.GetType().Name);
            return null;
        }
        finally
        {
            capture?.Dispose();
        }
    }

    private async ValueTask<string?> ResolveDeviceIdAsync(CancellationToken cancellationToken)
    {
        var devices = await ListDevicesAsync(cancellationToken).ConfigureAwait(false);

        if (devices.Count == 0)
        {
            return null;
        }

        var configured = _configuration.Current.CameraDeviceId;

        return devices.FirstOrDefault(d => d.Id == configured)?.Id
            ?? devices.FirstOrDefault(d => d.IsDefault)?.Id
            ?? devices[0].Id;
    }
}

/// <summary>Microphone evidence. Same contract, same failure behaviour, same privacy deference.</summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsAudioEvidence : IAudioEvidenceSource
{
    private readonly SecurityConfigurationStore _configuration;
    private readonly ILogger<WindowsAudioEvidence> _logger;

    public WindowsAudioEvidence(SecurityConfigurationStore configuration, ILogger<WindowsAudioEvidence> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string Name => "microphone";

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        var devices = await ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        return devices.Count > 0;
    }

    public async ValueTask<IReadOnlyList<EvidenceDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture).AsTask(cancellationToken)
                .ConfigureAwait(false);

            var selected = _configuration.Current.AudioDeviceId;

            return [.. found.Select(d => new EvidenceDevice(
                d.Id,
                d.Name,
                selected is null ? d.IsDefault : d.Id == selected))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Microphones could not be enumerated.");
            return [];
        }
    }

    public async ValueTask<CapturedEvidence?> CaptureClipAsync(
        string folder,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        MediaCapture? capture = null;

        // Bounded on the way in as well as by the caller: an audio clip is the most invasive thing
        // this subsystem records, and it should never be able to run long by accident.
        var length = TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds, 1, 120));

        try
        {
            var deviceId = await ResolveDeviceIdAsync(cancellationToken).ConfigureAwait(false);

            capture = new MediaCapture();

            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                AudioDeviceId = deviceId ?? string.Empty,
                StreamingCaptureMode = StreamingCaptureMode.Audio,
            }).AsTask(cancellationToken).ConfigureAwait(false);

            using var recording = new InMemoryRandomAccessStream();

            await capture
                .StartRecordToStreamAsync(MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium), recording)
                .AsTask(cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await capture.StopRecordAsync().AsTask(CancellationToken.None).ConfigureAwait(false);
            }

            var name = $"audio-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.m4a";
            var written = await MediaEvidence
                .SaveAsync(recording, Path.Combine(folder, name), CancellationToken.None)
                .ConfigureAwait(false);

            return written > 0 ? new CapturedEvidence(name, "audio/mp4", (long)length.TotalMilliseconds) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Microphone evidence unavailable: {Reason}", ex.GetType().Name);
            return null;
        }
        finally
        {
            capture?.Dispose();
        }
    }

    private async ValueTask<string?> ResolveDeviceIdAsync(CancellationToken cancellationToken)
    {
        var devices = await ListDevicesAsync(cancellationToken).ConfigureAwait(false);

        if (devices.Count == 0)
        {
            return null;
        }

        var configured = _configuration.Current.AudioDeviceId;

        return devices.FirstOrDefault(d => d.Id == configured)?.Id
            ?? devices.FirstOrDefault(d => d.IsDefault)?.Id
            ?? devices[0].Id;
    }
}

/// <summary>
/// Owner verification through Windows Hello.
/// <para>
/// One call into the standard consent API. Windows shows the prompt, Windows decides, and Nexus
/// receives a yes or a no. It never learns which method was used, never sees a face or a
/// fingerprint, and stores nothing — the operating system already holds that data and protects it
/// better than this project could.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsHelloVerifier : IOwnerVerifier
{
    private readonly ILogger<WindowsHelloVerifier> _logger;

    public WindowsHelloVerifier(ILogger<WindowsHelloVerifier> logger) => _logger = logger;

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync().AsTask(cancellationToken)
                .ConfigureAwait(false);

            return availability == UserConsentVerifierAvailability.Available;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Windows Hello availability could not be determined.");
            return false;
        }
    }

    public async ValueTask<OwnerVerificationResult> VerifyAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(reason).AsTask(cancellationToken)
                .ConfigureAwait(false);

            // Logged as a bare outcome. Which factor satisfied it is not something Nexus is told,
            // and would not be recorded if it were.
            _logger.LogInformation("Windows Hello verification for '{Reason}': {Result}", reason, result);

            return result switch
            {
                UserConsentVerificationResult.Verified => OwnerVerificationResult.Verified,

                UserConsentVerificationResult.DeviceNotPresent
                    or UserConsentVerificationResult.NotConfiguredForUser
                    or UserConsentVerificationResult.DisabledByPolicy
                    => OwnerVerificationResult.Unavailable,

                UserConsentVerificationResult.Canceled
                    or UserConsentVerificationResult.RetriesExhausted
                    => OwnerVerificationResult.Declined,

                _ => OwnerVerificationResult.Failed,
            };
        }
        catch (OperationCanceledException)
        {
            return OwnerVerificationResult.Declined;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Windows Hello verification failed: {Reason}", ex.GetType().Name);
            return OwnerVerificationResult.Failed;
        }
    }
}
