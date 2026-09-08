using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PcCore.Capabilities.Screen;
using PcCore.Capabilities.Identity;
using PcCore.Capabilities.Security;
using PcCore.Capabilities.Storage;
using PcCore.Capabilities.System;
using PcCore.Windows;

namespace PcCore.Tests.TestSupport;

/// <summary>
/// A throwaway directory under the system temp path, removed on dispose.
/// <para>
/// Tests never touch the real Desktop, Documents or Downloads: the filesystem boundary is exactly
/// what is under test, so it is configured to point at scratch directories instead.
/// </para>
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string? label = null)
    {
        // Resolved through the same Win32 path so that %TEMP% (frequently an 8.3 short path such
        // as C:\Users\RUNNER~1\AppData\Local\Temp) does not make every containment check fail.
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "pccore-tests", $"{label ?? "t"}-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);

        Path = new Win32RealPathResolver().Resolve(System.IO.Path.GetFullPath(root))
               ?? System.IO.Path.GetFullPath(root);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string CreateFile(string relativePath, string content = "x")
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDirectory(string relativePath)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// Creates a directory junction with <c>mklink /J</c>. Junctions, unlike symbolic links, need
    /// no administrator rights or Developer Mode, so this runs on an ordinary account.
    /// Returns false when creation fails, so the caller can skip rather than report a false failure.
    /// </summary>
    public static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(15_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                // Junctions are removed as directory entries; the target is untouched.
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. A leftover temp directory must never fail a test run.
        }
    }
}

public static class TestLog
{
    public static ILogger<T> For<T>() => NullLogger<T>.Instance;

    public static ILogger Null => NullLogger.Instance;
}

public static class TestSecurity
{
    /// <summary>Builds a real authorizer (Win32 resolver included) bound to the given roots.</summary>
    public static PathAuthorizer AuthorizerFor(params string[] roots) =>
        new(AllowedRoots.FromRealPaths(roots), new Win32RealPathResolver(), TestLog.For<PathAuthorizer>());

    public static PathAuthorizer EmptyAuthorizer() =>
        new(AllowedRoots.FromRealPaths(), new Win32RealPathResolver(), TestLog.For<PathAuthorizer>());
}

/// <summary>A deterministic <see cref="IScreenCapture"/> so screen tests need no desktop.</summary>
public sealed class FakeScreenCapture : IScreenCapture
{
    /// <summary>A minimal valid 1x1 PNG.</summary>
    public static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public DisplaySelection? LastSelection { get; private set; }

    public int? LastMaxWidth { get; private set; }

    public Func<DisplaySelection, int?, CapturedFrame>? OnCapture { get; init; }

    public CapturedFrame Capture(DisplaySelection selection, int? maxWidth)
    {
        LastSelection = selection;
        LastMaxWidth = maxWidth;

        return OnCapture?.Invoke(selection, maxWidth)
               ?? new CapturedFrame(OnePixelPng, 1, 1, 1920, 1080);
    }
}

public sealed class FakeSystemInfoProvider : ISystemInfoProvider
{
    public SystemInfo GetInfo() => new()
    {
        MachineName = "TEST-MACHINE",
        OsDescription = "Test OS",
        OsVersion = "10.0.0.0",
        OsArchitecture = "X64",
        ProcessArchitecture = "X64",
        PcCoreVersion = "0.1.0",
        RuntimeVersion = ".NET Test",
        ProcessorCount = 8,
        UptimeSeconds = 1234,
        TimestampUtc = DateTimeOffset.UnixEpoch,
        TimestampLocal = DateTimeOffset.UnixEpoch,
        IsElevated = false,
    };
}

public sealed class FakeProcessLister : IProcessLister
{
    public IReadOnlyList<ProcessEntry> Entries { get; init; } =
        [new ProcessEntry(1, "init", 1024), new ProcessEntry(2, "protected", null)];

    public IReadOnlyList<ProcessEntry> ListProcesses() => Entries;
}

/// <summary>
/// A throwaway <see cref="StateDirectory"/> under the system temp path.
/// <para>
/// Every test that touches persistence uses one of these. Nothing in the suite may read or write
/// the real <c>%LOCALAPPDATA%\Nexus</c>, which holds the owner's actual security state and phone
/// credential.
/// </para>
/// </summary>
public sealed class TempStateDirectory : IDisposable
{
    private readonly TempDirectory _directory;

    public TempStateDirectory(string? label = null)
    {
        _directory = new TempDirectory(label ?? "state");
        Directory = new StateDirectory(_directory.Path);
    }

    public StateDirectory Directory { get; }

    public string Path => _directory.Path;

    /// <summary>Overwrites a state file with arbitrary bytes, to exercise corruption handling.</summary>
    public void Corrupt(string fileName, string content = "{ this is not valid json")
        => System.IO.File.WriteAllText(Directory.File(fileName), content);

    public bool FileExists(string fileName) => System.IO.File.Exists(Directory.File(fileName));

    public string ReadRaw(string fileName) => System.IO.File.ReadAllText(Directory.File(fileName));

    public void Dispose() => _directory.Dispose();
}

/// <summary>
/// An <see cref="ISecretProtector"/> that does not protect anything.
/// <para>
/// Tests run against this rather than DPAPI so they stay deterministic and machine-independent.
/// DPAPI itself is covered by its own focused test.
/// </para>
/// </summary>
public sealed class PassThroughSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => (byte[])plaintext.Clone();

    public byte[]? Unprotect(byte[] ciphertext) => (byte[])ciphertext.Clone();
}

/// <summary>
/// A <see cref="IDeviceEndpointProvider"/> whose answer the test chooses, including "none".
/// <para>
/// The null case is the important one: it is what a real machine reports when Tailscale is not
/// running, and pairing codes must stay honest about that rather than inventing an address.
/// </para>
/// </summary>
public sealed class FixedDeviceEndpoint : IDeviceEndpointProvider
{
    public FixedDeviceEndpoint(DeviceEndpoint? endpoint = null) => Current = endpoint;

    public DeviceEndpoint? Current { get; }
}

/// <summary>
/// A clock a test can move.
/// <para>
/// Written here rather than taken from a package because it needs to do exactly one thing, and the
/// alternative is a dependency on a testing library for a class with two members. Everything in
/// Nexus that cares about time takes a <see cref="TimeProvider"/> for this reason.
/// </para>
/// </summary>
public sealed class AdvanceableTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
