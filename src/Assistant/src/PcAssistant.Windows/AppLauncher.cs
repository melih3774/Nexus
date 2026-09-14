using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PcAssistant.Windows;

/// <summary>One thing the owner could ask to open, and how it would be opened.</summary>
public sealed record AppEntry(string Name, string Kind, string? Target, string Reference, string? Detail = null);

/// <summary>What actually happened after a launch, read back from the desktop rather than assumed.</summary>
public sealed record LaunchOutcome(bool WindowAppeared, string What, long? Hwnd, string? WindowTitle,
    int? ProcessId, string? ProcessName, string Note);

/// <summary>
/// Finding and opening the things an owner names out loud: "open Discord", "open Bluetooth
/// settings", "open Downloads".
///
/// Four sources, none of which is a disk scan:
///
/// * Start Menu shortcuts (user and machine), resolved through IShellLink so the real target
///   is known before anything runs.
/// * App Paths, the registry's own answer to "where is chrome.exe".
/// * The Windows apps folder, for packaged applications with no exe path at all.
/// * A curated table of documented <c>ms-settings:</c> pages, shell folders and system tools,
///   so "Bluetooth settings" resolves to <c>ms-settings:bluetooth</c> rather than to a guess
///   about Explorer internals.
///
/// A launch is not a claim. Every open waits for a real window and reports the window it
/// found, or says plainly that none appeared.
/// </summary>
public sealed class AppLauncher : IDisposable
{
    private sealed record Target(AppEntry Entry, string? Path, string? Arguments, string? AppUserModelId,
        string? Uri, bool IsShortcut, DateTimeOffset Expires);

    private readonly Lazy<ApartmentWorker> shell = new(() => new(ApartmentState.STA));
    private readonly Dictionary<string, Target> targets = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>
    /// Shells and script hosts are never launched by name. The owner has proper routes to a
    /// terminal — the shell tools, and the elevated terminal operation — and neither of them
    /// is "ask the assistant to open something called cmd".
    /// </summary>
    private static readonly HashSet<string> CommandHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "rundll32.exe", "regsvr32.exe", "msiexec.exe", "bash.exe", "wsl.exe", "wt.exe",
        "conhost.exe", "openconsole.exe", "powershell_ise.exe",
    };

    // ------------------------------------------------------------ discovery

    /// <summary>
    /// Everything matching <paramref name="query"/>, best matches first. With no query this is
    /// a bounded catalogue; with one it is the answer to "what would 'discord' open".
    /// </summary>
    public IReadOnlyList<AppEntry> Discover(string? query = null, int limit = 40)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (query is { Length: > 128 }) throw new ArgumentOutOfRangeException(nameof(query), "Use a short application name.");

        var ranked = Catalogued()
            .Select(c => (c.Entry, c.Target, Score: Score(c.Entry.Name, query)))
            .Where(c => c.Score > 0)
            .GroupBy(c => (c.Entry.Name.ToUpperInvariant(), c.Entry.Kind))
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();

        lock (gate)
        {
            Prune();
            var result = new List<AppEntry>(ranked.Length);
            foreach (var item in ranked)
            {
                var reference = "app-" + Guid.NewGuid().ToString("N")[..10];
                targets[reference] = item.Target with { Entry = item.Entry with { Reference = reference } };
                result.Add(targets[reference].Entry);
            }
            return result;
        }
    }

    /// <summary>Exact name beats a prefix beats a substring; no match at all scores zero.</summary>
    private static int Score(string name, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 1;
        var needle = query.Trim();
        if (name.Equals(needle, StringComparison.OrdinalIgnoreCase)) return 100;
        if (name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) return 70;
        if (name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 40;

        // "vs code" should still find "Visual Studio Code": every word has to appear somewhere.
        var words = needle.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return 30;
        return 0;
    }

    // -------------------------------------------------------------- opening

    /// <summary>
    /// Opens a discovery reference, or a plain name. A name that matches several things
    /// equally well is not guessed at: the candidates come back so the choice is explicit.
    /// </summary>
    public Task<LaunchOutcome> OpenAsync(string nameOrReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrReference);
        return StartAndVerifyAsync(Resolve(nameOrReference), cancellationToken);
    }

    /// <summary>
    /// Opens an existing local file or folder <em>with</em> a named application — "open this
    /// folder in VS Code".
    ///
    /// The application comes from discovery and the argument list is exactly one validated
    /// path. There is no way to reach a second argument, a flag, or a shell from here, which is
    /// what keeps "open X in Y" from becoming "run whatever you like".
    /// </summary>
    public async Task<LaunchOutcome> OpenPathWithAsync(string nameOrReference, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = ValidateLocalPath(path);
        var target = Resolve(nameOrReference);

        if (target.Path is null && target.Uri is null && target.AppUserModelId is null)
            throw new ArgumentException(
                $"'{target.Entry.Name}' was found in the list of installed software, but Windows registers no way to " +
                "launch it. Ask the owner how they normally start it, or look for it under a different name.",
                nameof(nameOrReference));

        if (target.Uri is not null || target.AppUserModelId is not null)
            throw new ArgumentException(
                $"'{target.Entry.Name}' is a {target.Entry.Kind.ToLowerInvariant()} and cannot be given a path to open. " +
                "Use open_document to hand the file to whichever application Windows has registered for it.", nameof(nameOrReference));

        var (executable, _) = LaunchTarget(target, cancellationToken);

        var before = TopLevelWindows();
        var startedAt = DateTime.UtcNow;
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? Environment.CurrentDirectory,
        };
        // A list, never a command string: nothing here is re-parsed by a shell.
        info.ArgumentList.Add(full);

        int? pid = null;
        using (var process = Process.Start(info))
            if (process is not null) { try { pid = process.Id; } catch (InvalidOperationException) { } }

        return await WaitForWindowAsync($"{target.Entry.Name} with {Path.GetFileName(full)}", executable, pid,
            before, startedAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a folder in File Explorer. Shows it to the owner; changes nothing in it.</summary>
    public async Task<LaunchOutcome> OpenFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = ValidateLocalPath(path);
        if (!Directory.Exists(full))
            throw new ArgumentException("That path is not an existing folder. Use open_document for a file.", nameof(path));

        var before = TopLevelWindows();
        var startedAt = DateTime.UtcNow;
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(full);
        using (Process.Start(info)) { }

        return await WaitForWindowAsync(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : full,
            "explorer.exe", null, before, startedAt, cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Length < 3 || path[1] != ':' || path[2..].Contains(':'))
            throw new ArgumentException("Use an absolute local path; network paths and alternate data streams are excluded.", nameof(path));
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new ArgumentException("That path does not exist.", nameof(path));
        return full;
    }

    /// <summary>Resolves a reference or a name to exactly one target, or explains why it cannot.</summary>
    private Target Resolve(string nameOrReference)
    {
        lock (gate)
        {
            if (targets.TryGetValue(nameOrReference, out var known) && known.Expires > DateTimeOffset.UtcNow) return known;
        }
        if (nameOrReference.StartsWith("app-", StringComparison.Ordinal))
            throw new ArgumentException("That application reference is unknown or has expired; discover applications again.", nameof(nameOrReference));

        var matches = Discover(nameOrReference, 6);
        if (matches.Count == 0)
            throw new ArgumentException(
                $"Nothing installed matches '{nameOrReference}'. Discover applications with a shorter fragment of the " +
                "name, or ask the owner what it is called on this PC.", nameof(nameOrReference));

        var best = Score(matches[0].Name, nameOrReference);
        var rivals = matches.Skip(1).Where(m => Score(m.Name, nameOrReference) == best).ToArray();
        if (rivals.Length > 0 && best < 100)
            throw new AmbiguousApplicationException(
                $"'{nameOrReference}' matches {rivals.Length + 1} things equally well: " +
                string.Join("; ", matches.Take(4).Select(m => $"{m.Name} ({m.Kind})")) +
                ". Open one by its reference from installed_apps rather than by this name.");

        lock (gate) { return targets[matches[0].Reference]; }
    }

    /// <summary>
    /// Starts the target and then answers the only question that matters: did a window appear.
    ///
    /// The three launch shapes need three different starts — a URI through the shell, a
    /// packaged app through the activation manager, an executable directly — but all three
    /// converge on the same verification, because "Process.Start returned" is not evidence
    /// that anything opened.
    /// </summary>
    private async Task<LaunchOutcome> StartAndVerifyAsync(Target target, CancellationToken cancellationToken)
    {
        if (target.Path is null && target.Uri is null && target.AppUserModelId is null)
            throw new ArgumentException(
                $"'{target.Entry.Name}' was found in the list of installed software, but Windows registers no way to " +
                "launch it. Ask the owner how they normally start it, or look for it under a different name.");

        var before = TopLevelWindows();
        var startedAt = DateTime.UtcNow;
        int? pid = null;
        string what;

        if (target.Uri is { } uri)
        {
            what = uri;
            using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            // The shell hands a URI to whichever app owns it; the process that appears is not
            // this one, so the pid is deliberately not claimed here.
        }
        else if (target.AppUserModelId is { } aumid)
        {
            what = target.Entry.Name;
            pid = (int)await shell.Value.RunAsync(() => ShellApplications.Activate(aumid), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // A Start Menu entry is resolved here and only here: what it actually points at is
            // checked immediately before it runs, which is the moment the answer matters.
            var (executable, arguments) = LaunchTarget(target, cancellationToken);
            what = executable;
            var info = new ProcessStartInfo(target.IsShortcut ? target.Path! : executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            };
            // Launching the shortcut itself keeps the arguments and working directory the owner's
            // own Start Menu entry carries; nothing is composed here.
            if (!target.IsShortcut && !string.IsNullOrWhiteSpace(arguments)) info.Arguments = arguments;
            using var process = Process.Start(info);
            if (process is not null) { try { pid = process.Id; } catch (InvalidOperationException) { } }
        }

        return await WaitForWindowAsync(target.Entry.Name, what, pid, before, startedAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls for a new top-level window belonging to the launch. A window that already existed
    /// is never accepted as proof: an application that was already open and simply came forward
    /// is a different outcome from one that started, and the note says which.
    /// </summary>
    private static async Task<LaunchOutcome> WaitForWindowAsync(string name, string what, int? pid,
        HashSet<long> before, DateTime startedAt, CancellationToken cancellationToken)
    {
        var expectedName = Path.GetFileNameWithoutExtension(what);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var desktop = new DesktopWindows();

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            foreach (var window in desktop.ListWindows(200))
            {
                if (before.Contains(window.Hwnd) || string.IsNullOrWhiteSpace(window.Title)) continue;
                if (ProtectedWindows.IsProtectedWindow(window.Hwnd)) continue;

                var process = SafeProcessName((int)window.ProcessId, out var started);
                var matchesPid = pid is not null && window.ProcessId == pid;
                var matchesName = process is not null && expectedName.Length > 2 &&
                                  process.Contains(expectedName, StringComparison.OrdinalIgnoreCase);
                var isNew = started is not null && started >= startedAt.AddSeconds(-2);

                if (!matchesPid && !matchesName && !isNew) continue;

                return new LaunchOutcome(true, what, window.Hwnd, window.Title, (int)window.ProcessId, process,
                    $"'{name}' opened and its window is on the desktop. Observe it with capture_window before acting on it; " +
                    "the title above is the only thing verified so far.");
            }
        }

        var existing = pid is not null ? SafeProcessName(pid.Value, out _) : null;
        return new LaunchOutcome(false, what, null, null, pid, existing,
            $"'{name}' was started but no new window appeared within 8 seconds. That is not the same as failure: it may " +
            "be a background or tray application, it may already have been running and simply focused an existing " +
            "window, or it may still be loading. Check the window list and the process list before telling the owner it worked.");
    }

    private static HashSet<long> TopLevelWindows() =>
        new DesktopWindows().ListWindows(400).Select(w => w.Hwnd).ToHashSet();

    private static string? SafeProcessName(int processId, out DateTime? startTime)
    {
        startTime = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            try { startTime = process.StartTime.ToUniversalTime(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The executable a target will actually start, and its arguments. For a Start Menu entry
    /// this resolves the .lnk; for anything else it is already known. Either way the result is
    /// validated before a process is created, so a shortcut that points at a shell is refused
    /// here rather than discovered afterwards.
    /// </summary>
    private (string Executable, string? Arguments) LaunchTarget(Target target, CancellationToken cancellationToken)
    {
        var path = target.Path ?? throw new InvalidOperationException("The application has no launch target.");
        if (!target.IsShortcut)
        {
            EnsureLaunchable(path);
            return (path, target.Arguments);
        }

        var resolved = shell.Value.RunAsync(_ => ShellLinks.Resolve(path), cancellationToken).GetAwaiter().GetResult()
            ?? throw new ArgumentException(
                $"'{target.Entry.Name}' is a Start Menu shortcut that does not point at a runnable application " +
                "(it may be a document, a web link, or broken). Discover applications again, or ask the owner.",
                nameof(target));

        EnsureLaunchable(resolved.Path);
        return (resolved.Path, resolved.Arguments);
    }

    private static void EnsureLaunchable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Only absolute local application paths are launched.", nameof(path));
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only .exe applications are launched by name.", nameof(path));
        if (CommandHosts.Contains(Path.GetFileName(path)))
            throw new ArgumentException(
                $"'{Path.GetFileName(path)}' is a shell or script host, and PC Assistant does not open one by name. " +
                "Run commands through the shell tools, which are classified and approved on their own terms.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("The application is no longer at the discovered path; discover again.", path);
    }

    // --------------------------------------------------------------- sources

    /// <summary>
    /// Discovery is a name lookup, not a COM walk.
    ///
    /// The first version of this resolved every Start Menu .lnk through IShellLink on each
    /// call. With about a thousand shortcuts on a real machine that is a thousand cross-
    /// apartment COM round trips per search, which took long enough to look like a hang — and
    /// searching happens far more often than launching.
    ///
    /// So shortcuts are listed by filename here and resolved exactly once, at the moment one
    /// is actually opened, which is also the only moment the target matters. The packaged-app
    /// enumeration, which is genuinely expensive and genuinely static, is cached for a minute.
    /// </summary>
    private sealed record Catalogue(IReadOnlyList<(AppEntry Entry, Target Target)> Items, DateTimeOffset Expires);

    private Catalogue? catalogue;
    private static readonly TimeSpan CatalogueLifetime = TimeSpan.FromMinutes(1);

    private IReadOnlyList<(AppEntry Entry, Target Target)> Catalogued()
    {
        lock (gate)
        {
            if (catalogue is { } cached && cached.Expires > DateTimeOffset.UtcNow) return cached.Items;
        }

        var items = new List<(AppEntry, Target)>();
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);

        void Add(AppEntry entry, string? path, string? arguments, string? aumid, string? uri, bool shortcut = false)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
                items.Add((entry, new Target(entry, path, arguments, aumid, uri, shortcut, expires)));
        }

        foreach (var (name, uri, detail) in KnownTargets.All)
            Add(new AppEntry(name, KnownTargets.KindOf(uri), uri, "", detail), null, null, null, uri);

        foreach (var (name, link) in StartMenuShortcuts())
            Add(new AppEntry(name, "Start Menu", link, "", "Resolved to its real target when opened."),
                link, null, null, null, shortcut: true);

        foreach (var (name, path) in AppPaths())
            Add(new AppEntry(name, "App Paths", path, ""), path, null, null, null);

        foreach (var packaged in PackagedApps())
            Add(new AppEntry(packaged.Name, "Windows packaged application", null, ""), null, null, packaged.AppUserModelId, null);

        // Listed but not launchable. Kept separate so a caller can tell "installed" from
        // "openable" instead of inferring one from the other.
        var known = new HashSet<string>(items.Select(i => i.Item1.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, publisher, version) in InstalledSoftware())
        {
            if (!known.Add(name)) continue;
            Add(new AppEntry(name, "Installed software", null, "",
                string.Join(" · ", new[] { publisher, version }.Where(v => !string.IsNullOrWhiteSpace(v))) is { Length: > 0 } detail
                    ? detail + " — discovery only; no launch target is registered."
                    : "Discovery only; no launch target is registered."), null, null, null, null);
        }

        lock (gate)
        {
            catalogue = new Catalogue(items, DateTimeOffset.UtcNow + CatalogueLifetime);
            return items;
        }
    }

    /// <summary>Start Menu shortcut files by name. No COM, no target resolution, no disk scan beyond these two folders.</summary>
    private static IReadOnlyList<(string Name, string Path)> StartMenuShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };

        var result = new List<(string, string)>();
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             MaxRecursionDepth = 4,
                             IgnoreInaccessible = true,
                         }).Take(1500))
                {
                    result.Add((Path.GetFileNameWithoutExtension(file), file));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return result;
    }

    /// <summary>The registry's own answer to "where is chrome.exe": App Paths, both hives, both views.</summary>
    private static IEnumerable<(string Name, string Path)> AppPaths()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var paths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (paths is null) continue;
            foreach (var name in paths.GetSubKeyNames().Take(1000))
            {
                string? path = null;
                try
                {
                    using var key = paths.OpenSubKey(name);
                    path = (key?.GetValue(null) as string)?.Trim('"');
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { }
                if (path is null || CommandHosts.Contains(Path.GetFileName(path))) continue;
                if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(path)) continue;
                yield return (Path.GetFileNameWithoutExtension(name), path);
            }
        }
    }

    /// <summary>
    /// Software the owner has installed, from the uninstall registry. Most of it has no
    /// launchable target — that is the point: "what is installed on this PC" is a different
    /// question from "what can you open", and answering only the second one made the first
    /// unanswerable through the exposed tools.
    /// </summary>
    private static IEnumerable<(string Name, string? Publisher, string? Version)> InstalledSoftware()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames().Take(2000))
            {
                string? display = null, publisher = null, version = null;
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    display = key?.GetValue("DisplayName") as string;
                    publisher = key?.GetValue("Publisher") as string;
                    version = key?.GetValue("DisplayVersion") as string;
                    // System components and updates are noise in an owner-facing list.
                    if (key?.GetValue("SystemComponent") is int component && component != 0) display = null;
                    if (key?.GetValue("ParentKeyName") is string) display = null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { }
                if (!string.IsNullOrWhiteSpace(display)) yield return (display!, publisher, version);
            }
        }
    }

    private IReadOnlyList<ShellApplications.PackagedApp> PackagedApps()
    {
        try { return shell.Value.RunAsync(ShellApplications.Discover, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is COMException or InvalidOperationException or OperationCanceledException) { return []; }
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in targets.Where(t => t.Value.Expires <= now).Select(t => t.Key).ToArray())
            targets.Remove(stale);
        if (targets.Count <= 600) return;
        foreach (var oldest in targets.OrderBy(t => t.Value.Expires).Take(targets.Count - 600).Select(t => t.Key).ToArray())
            targets.Remove(oldest);
    }

    public void Dispose() { if (shell.IsValueCreated) shell.Value.Dispose(); }
}

/// <summary>A name that could mean several installed things. Answered with the candidates, not a guess.</summary>
public sealed class AmbiguousApplicationException(string message) : ArgumentException(message);

/// <summary>
/// Resolves .lnk files through IShellLink so the actual target is known before anything is
/// launched — which is what makes it possible to refuse a shortcut that points at a shell.
/// </summary>
internal static class ShellLinks
{
    /// <summary>
    /// Resolves one .lnk to its target and arguments, or null if it does not point at an
    /// executable. Called once per launch rather than once per discovered shortcut: resolving a
    /// thousand of them through cross-apartment COM on every search is slow enough to look like
    /// a hang, and the target is only needed when something is about to run.
    /// </summary>
    public static AppLauncherShortcut? Resolve(string linkPath)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!);
            var link = (IShellLinkW)instance!;
            ((IPersistFile)instance!).Load(linkPath, 0);

            var path = new StringBuilder(1024);
            var arguments = new StringBuilder(1024);
            // SLGP_RAWPATH avoids the network and installer lookups a full Resolve would do for
            // a dead shortcut, which is exactly the case that would otherwise block.
            link.GetPath(path, path.Capacity, 0, 0x4);
            link.GetArguments(arguments, arguments.Capacity);

            var target = Environment.ExpandEnvironmentVariables(path.ToString()).Trim();
            if (target.Length == 0 || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(target)) return null;

            return new AppLauncherShortcut(Path.GetFileNameWithoutExtension(linkPath), target,
                arguments.ToString().Trim() is { Length: > 0 } a ? a : null);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally { if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int length, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string? fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}

internal sealed record AppLauncherShortcut(string Name, string Path, string? Arguments);
