using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PcAssistant.Windows;

public sealed record ProcessInfo(int ProcessId, string Name, long WorkingSetBytes, double? CpuPercent, string? WindowTitle);
public sealed record DriveInfoResult(string Name, string Type, long? TotalBytes, long? AvailableBytes, string? Error);
public sealed record NetworkInfo(string Name, string Type, string Status, IReadOnlyList<string> Addresses);
public sealed record SystemInfo(string MachineName, string Os, string Architecture, int LogicalProcessors, ulong TotalMemoryBytes,
    ulong AvailableMemoryBytes, IReadOnlyList<DriveInfoResult> Drives, IReadOnlyList<NetworkInfo> Network);
public sealed record RecentFile(string Path, long Length, DateTime LastWriteTimeUtc);
public sealed class SystemDiscovery : IDisposable
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".csv", ".tsv", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".rtf", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".mp3", ".wav", ".mp4", ".mkv" };

    public SystemInfo GetSystemInfo()
    {
        var memory = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        Native.Check(Native.GlobalMemoryStatusEx(ref memory), "Physical memory information is unavailable.");
        var drives = DriveInfo.GetDrives().Take(64).Select(d =>
        {
            try { return new DriveInfoResult(d.Name, d.DriveType.ToString(), d.IsReady ? d.TotalSize : null, d.IsReady ? d.AvailableFreeSpace : null, null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new DriveInfoResult(d.Name, d.DriveType.ToString(), null, null, e.Message); }
        }).ToArray();
        var network = NetworkInterface.GetAllNetworkInterfaces().Take(64).Select(n => new NetworkInfo(n.Name, n.NetworkInterfaceType.ToString(),
            n.OperationalStatus.ToString(), n.GetIPProperties().UnicastAddresses.Take(8).Select(a => a.Address.ToString()).ToArray())).ToArray();
        return new SystemInfo(Environment.MachineName, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), Environment.ProcessorCount,
            memory.TotalPhysical, memory.AvailablePhysical, drives, network);
    }

    public async Task<IReadOnlyList<ProcessInfo>> ListProcessesAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var processes = Process.GetProcesses();
        try
        {
            var samples = new List<(Process Process, TimeSpan? Cpu, long Timestamp)>();
            foreach (var process in processes.Take(4096))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { samples.Add((process, process.TotalProcessorTime, Stopwatch.GetTimestamp())); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { samples.Add((process, null, Stopwatch.GetTimestamp())); }
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var result = new List<ProcessInfo>();
            foreach (var (process, cpu, timestamp) in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    process.Refresh();
                    double? percent = null;
                    if (cpu is not null)
                    {
                        try { percent = Math.Clamp((process.TotalProcessorTime - cpu.Value).TotalSeconds / Stopwatch.GetElapsedTime(timestamp).TotalSeconds / Environment.ProcessorCount * 100, 0, 100); }
                        catch (Win32Exception) { }
                    }
                    result.Add(new ProcessInfo(process.Id, process.ProcessName, process.WorkingSet64, percent, process.MainWindowTitle));
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { /* Exited or inaccessible process. */ }
            }
            return result.OrderByDescending(p => p.WorkingSetBytes).Take(limit).ToArray();
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    // Deliberately nonrecursive and bounded; this is discovery, not a disk index.
    public IReadOnlyList<RecentFile> RecentFiles(int limit = 50, string? directory = null, string? extension = null)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (directory is not null && (!IsLocalPath(directory) || !Directory.Exists(directory)))
            throw new ArgumentException("Directory must be an existing absolute local directory.", nameof(directory));
        if (extension is not null && (extension.Length is < 2 or > 20 || extension[0] != '.' || extension[1..].Any(c => !char.IsAsciiLetterOrDigit(c))))
            throw new ArgumentException("Extension must be a dot followed by letters or digits, such as .pdf.", nameof(extension));
        var roots = directory is not null ? new[] { directory } : new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.Recent) };
        var result = new List<RecentFile>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root).Take(3000))
                {
                    if (extension is not null && !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    try { var file = new FileInfo(path); result.Add(new RecentFile(file.FullName, file.Length, file.LastWriteTimeUtc)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(f => f.LastWriteTimeUtc).Take(limit).ToArray();
    }

    public string OpenUrl(string url)
    {
        ValidateUrl(url);
        using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true, Verb = "open" });
        return "URL handed to the default browser.";
    }
    public static void ValidateUrl(string url)
    {
        if (url is null || url.Length > 8192 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Use an absolute http or https URL without embedded credentials.", nameof(url));
    }

    public string OpenDocument(string path)
    {
        var full = ValidateDocumentPath(path);
        using var process = Process.Start(new ProcessStartInfo(full) { UseShellExecute = true, Verb = "open" });
        return "Document handed to its registered application.";
    }
    public static string ValidateDocumentPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsLocalPath(path) || !DocumentExtensions.Contains(Path.GetExtension(path)))
            throw new ArgumentException("Use an absolute local document or media path; executable, script, shortcut and network paths are excluded.", nameof(path));
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("The document does not exist.", full);
        var target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
        if (target is not null)
        {
            full = target.FullName;
            if (!IsLocalPath(full) || !DocumentExtensions.Contains(Path.GetExtension(full)))
                throw new ArgumentException("The document link target is not an allowed local document or media file.", nameof(path));
        }
        return full;
    }
    private static bool IsLocalPath(string path) => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length >= 3 && path[1] == ':' && !path[2..].Contains(':');

    // Finding and launching applications lives in AppLauncher: it resolves Start Menu
    // shortcuts, Settings pages and shell folders too, and verifies that a window actually
    // appeared. Two discovery paths, only one of which a model could see, was the ambiguity
    // this class used to carry.
    public void Dispose() { }
}
