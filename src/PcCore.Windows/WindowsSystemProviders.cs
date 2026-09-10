using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PcCore.Capabilities.System;
using PcCore.Windows.Interop;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace PcCore.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    // ProductInfo is the single source; this used to read the attribute itself, which was one of
    // three places a version was derived and therefore one of three places it could drift.
    private static readonly string Version = PcCore.Capabilities.Abstractions.ProductInfo.Version;

    public SystemInfo GetInfo()
    {
        var now = DateTimeOffset.UtcNow;

        return new SystemInfo
        {
            MachineName = Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PcCoreVersion = Version,
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            // TickCount64 is monotonic since boot and, unlike the 32-bit form, does not wrap.
            UptimeSeconds = Environment.TickCount64 / 1000,
            TimestampUtc = now,
            TimestampLocal = now.ToLocalTime(),
            IsElevated = IsElevated(),
        };
    }

    /// <summary>
    /// Reported so a caller can confirm PC Core is running unelevated, which v0.1 requires.
    /// Nothing in this codebase ever tries to change the answer.
    /// </summary>
    private static bool IsElevated()
    {
        nint token = 0;

        try
        {
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TokenQuery, out token))
            {
                return false;
            }

            return NativeMethods.GetTokenInformation(
                       token,
                       NativeMethods.TokenElevationClass,
                       out var elevation,
                       sizeof(uint),
                       out _)
                   && elevation != 0;
        }
        finally
        {
            if (token != 0)
            {
                NativeMethods.CloseHandle(token);
            }
        }
    }
}

/// <summary>
/// Enumerates processes with <see cref="Process.GetProcesses()"/>.
/// <para>
/// No privilege is requested and none is needed: pid and name come from the snapshot itself.
/// <c>WorkingSet64</c> can throw for protected processes and for processes that exit mid-walk, so
/// each read is guarded individually — one inaccessible process must never fail the whole call.
/// </para>
/// <para>
/// Command lines, executable paths, window titles and owners are deliberately not collected.
/// They leak far more than a process list needs to (SPEC.md 5).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessLister : IProcessLister
{
    private readonly ILogger<WindowsProcessLister> _logger;

    public WindowsProcessLister(ILogger<WindowsProcessLister> logger) => _logger = logger;

    public IReadOnlyList<ProcessEntry> ListProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Process enumeration failed.");
            return [];
        }

        var entries = new List<ProcessEntry>(processes.Length);
        var skipped = 0;

        foreach (var process in processes)
        {
            try
            {
                var name = process.ProcessName;
                long? workingSet = null;

                try
                {
                    workingSet = process.WorkingSet64;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Protected or already-exited process: report it without the memory figure.
                }

                entries.Add(new ProcessEntry(process.Id, name, workingSet));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                skipped++;
            }
            finally
            {
                process.Dispose();
            }
        }

        if (skipped > 0)
        {
            _logger.LogDebug("Skipped {Skipped} inaccessible process(es) during enumeration.", skipped);
        }

        entries.Sort(static (left, right) => left.Pid.CompareTo(right.Pid));
        return entries;
    }
}
