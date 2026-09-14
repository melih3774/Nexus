using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using PcAssistant.Windows.Capture;

namespace PcAssistant.Windows;

public sealed class InputService(CaptureRegistry captures) : IDisposable
{
    private static readonly SemaphoreSlim SerialInput = new(1, 1);
    private static readonly object HeldLock = new();
    private static readonly List<Native.Input> PendingReleases = [];
    private readonly ApartmentWorker automation = new(ApartmentState.MTA);
    private readonly CaptureRegistry captures = captures;

    /// <summary>
    /// Input aimed at a point in a window image rather than at a desktop pixel.
    ///
    /// The difference matters. A desktop pixel is a claim about where something was when the
    /// image was taken; a capture point is a claim about where it is in the window, resolved
    /// against that window's verified geometry at the moment of the click — after the
    /// foreground has been taken, so nothing can move in between without being noticed.
    ///
    /// Observation never disturbs the desktop. Acting on it necessarily does: SendInput goes
    /// to whatever is focused, so the target is brought to the front first, exactly as it is
    /// for every other input path.
    /// </summary>
    public Task<string> ClickCaptureAsync(string captureId, int imageX, int imageY,
        string button = "left", int count = 1, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(count));
        var (down, up) = Button(button);
        return RunAsync(TargetOf(captureId), () =>
        {
            var point = captures.Resolve(captureId, imageX, imageY);
            CheckPoint(point.Hwnd, point.X, point.Y);
            var events = new List<Native.Input> { Move(point.X, point.Y) };
            for (var i = 0; i < count; i++) { events.Add(Mouse(down)); events.Add(Mouse(up)); }
            Inject(events.ToArray(), [Mouse(up)]);
            return $"Clicked image point ({imageX},{imageY}), which is desktop pixel ({point.X},{point.Y}). " +
                   point.Note + " Capture the window again to verify what the click did.";
        }, false, cancellationToken);
    }

    public Task<string> ScrollCaptureAsync(string captureId, int imageX, int imageY, int ticks,
        bool horizontal = false, CancellationToken cancellationToken = default)
    {
        if (ticks is < -20 or > 20 || ticks == 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        return RunAsync(TargetOf(captureId), () =>
        {
            var point = captures.Resolve(captureId, imageX, imageY);
            CheckPoint(point.Hwnd, point.X, point.Y);
            var wheel = Mouse(horizontal ? 0x1000u : 0x0800u);
            wheel.Data.Mouse.MouseData = unchecked((uint)(ticks * 120));
            Inject([Move(point.X, point.Y), wheel], []);
            return $"Scrolled at image point ({imageX},{imageY}). " + point.Note;
        }, false, cancellationToken);
    }

    public Task<string> DragCaptureAsync(string captureId, int imageX, int imageY, int toImageX, int toImageY,
        int steps = 20, CancellationToken cancellationToken = default)
    {
        if (steps is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(steps));
        return RunAsync(TargetOf(captureId), () =>
        {
            var from = captures.Resolve(captureId, imageX, imageY);
            var to = captures.Resolve(captureId, toImageX, toImageY);
            CheckPoint(from.Hwnd, from.X, from.Y); CheckPoint(to.Hwnd, to.X, to.Y);
            var events = new List<Native.Input> { Move(from.X, from.Y), Mouse(0x0002) };
            for (var i = 1; i <= steps; i++)
                events.Add(Move(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            events.Add(Mouse(0x0004));
            Inject(events.ToArray(), [Mouse(0x0004)]);
            return $"Dragged from image ({imageX},{imageY}) to ({toImageX},{toImageY}). " + from.Note;
        }, false, cancellationToken);
    }

    /// <summary>
    /// Which window a capture belongs to. Resolved before the foreground is taken so an expired
    /// or unknown id costs nothing and disturbs nothing; the coordinates themselves are
    /// resolved later, once the window is verified in front.
    /// </summary>
    private long TargetOf(string captureId)
    {
        if (!captures.TryDescribe(captureId, out var hwnd, out _, out _, out _))
            throw new StaleCaptureException(
                "That capture id is unknown or has expired, so there is no verified frame of reference for those " +
                "coordinates. Capture the window again and use the id from that result.");
        return hwnd;
    }

    public Task<string> ClickAsync(long hwnd, int x, int y, string button = "left", int count = 1, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(count));
        var (down, up) = Button(button);
        return RunAsync(hwnd, () =>
        {
            CheckPoint(hwnd, x, y);
            var events = new List<Native.Input> { Move(x, y) };
            for (var i = 0; i < count; i++) { events.Add(Mouse(down)); events.Add(Mouse(up)); }
            Inject(events.ToArray(), [Mouse(up)]);
            return "Mouse click input inserted. Verify application state with a snapshot.";
        }, false, cancellationToken);
    }

    public Task<string> TypeTextAsync(long hwnd, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is < 1 or > 4000 || text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new ArgumentException("Use 1–4000 characters; only tabs and newlines are permitted control characters.", nameof(text));
        return RunAsync(hwnd, () =>
        {
            foreach (var character in text)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckForeground(hwnd);
                var up = Unicode(character, true);
                Inject([Unicode(character, false), up], [up]);
            }
            return "Unicode text input inserted. Verify the focused control's resulting text.";
        }, true, cancellationToken);
    }

    public Task<string> KeysAsync(long hwnd, string[] keys, CancellationToken cancellationToken = default)
    {
        var codes = ValidateKeys(keys);
        return RunAsync(hwnd, () =>
        {
            var releases = codes.Reverse().Select(c => Key(c, true)).ToArray();
            Inject(codes.Select(c => Key(c, false)).Concat(releases).ToArray(), releases);
            return "Key chord input inserted. Verify application state.";
        }, true, cancellationToken);
    }

    // A complete drag is queued atomically with its release; no sleeping while a button is held.
    public Task<string> DragAsync(long hwnd, int fromX, int fromY, int toX, int toY, int steps = 20, CancellationToken cancellationToken = default)
    {
        if (steps is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(steps));
        return RunAsync(hwnd, () =>
        {
            CheckPoint(hwnd, fromX, fromY); CheckPoint(hwnd, toX, toY);
            var events = new List<Native.Input> { Move(fromX, fromY), Mouse(0x0002) };
            for (var i = 1; i <= steps; i++) events.Add(Move(fromX + (toX - fromX) * i / steps, fromY + (toY - fromY) * i / steps));
            events.Add(Mouse(0x0004));
            Inject(events.ToArray(), [Mouse(0x0004)]);
            return "Complete drag input sequence inserted. Some applications require a slower manual drag; verify the result.";
        }, false, cancellationToken);
    }

    public Task<string> ScrollAsync(long hwnd, int x, int y, int ticks, bool horizontal = false, CancellationToken cancellationToken = default)
    {
        if (ticks is < -20 or > 20 || ticks == 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        return RunAsync(hwnd, () =>
        {
            CheckPoint(hwnd, x, y);
            var wheel = Mouse(horizontal ? 0x1000u : 0x0800u);
            wheel.Data.Mouse.MouseData = unchecked((uint)(ticks * 120));
            Inject([Move(x, y), wheel], []);
            return "Scroll input inserted.";
        }, false, cancellationToken);
    }

    /// <summary>
    /// Modifiers and buttons the owner is physically holding right now. Automated input refuses
    /// to run while any of them is down, so this is what a diagnostic can honestly check
    /// without injecting anything to find out.
    /// </summary>
    public static IReadOnlyList<string> HeldInputs()
    {
        var held = new List<string>();
        foreach (var (key, name) in new[]
                 {
                     (0x01, "left mouse button"), (0x02, "right mouse button"), (0x04, "middle mouse button"),
                     (0x10, "Shift"), (0x11, "Ctrl"), (0x12, "Alt"), (0x5B, "left Windows key"), (0x5C, "right Windows key"),
                 })
            if (Native.IsKeyDown(key)) held.Add(name);
        return held;
    }

    public static ushort[] ValidateKeys(string[] keys)
    {
        if (keys is null || keys.Length is < 1 or > 4) throw new ArgumentException("Provide 1–4 named keys in press order.", nameof(keys));
        var result = keys.Select(ParseKey).ToArray();
        if (result.Distinct().Count() != result.Length) throw new ArgumentException("Duplicate keys are not allowed.", nameof(keys));
        if ((result.Contains((ushort)0x12) && (result.Contains((ushort)0x09) || result.Contains((ushort)0x1B))) ||
            (result.Contains((ushort)0x11) && result.Contains((ushort)0x1B))) throw new ArgumentException("Global task-switching chords are excluded; select an explicit target window.", nameof(keys));
        return result;
    }

    // Only releases events belonging to an incomplete injection in this process.
    public static void ReleaseAll()
    {
        lock (HeldLock)
        {
            if (PendingReleases.Count == 0) return;
            var releases = PendingReleases.ToArray();
            var inserted = Native.SendInput((uint)releases.Length, releases, Marshal.SizeOf<Native.Input>());
            if (inserted == releases.Length) PendingReleases.Clear();
        }
    }

    /// <summary>
    /// One approved operation does the whole thing: take the foreground, prove it, re-check it, then inject.
    /// The owner's approval click necessarily moves the foreground to PC Assistant, so acquiring it here —
    /// rather than expecting a separate focus call to survive that click — is what makes input work at all.
    /// </summary>
    private async Task<string> RunAsync(long hwnd, Func<string> operation, bool rejectPassword, CancellationToken cancellationToken)
    {
        await SerialInput.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProtectedWindows.EnsureNotProtectedWindow(hwnd);
            var foreground = await ForegroundService.EnsureAsync(hwnd, cancellationToken).ConfigureAwait(false);
            if (!foreground.Verified) throw new ForegroundUnavailableException(foreground.Message);
            foreach (var key in new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C })
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("Release held mouse buttons and modifier keys before automated input.");
            if (rejectPassword)
                await automation.RunAsync(() =>
                {
                    var focused = AutomationElement.FocusedElement ?? throw new InvalidOperationException("Focused control cannot be verified.");
                    if (focused.Current.IsPassword) throw new CredentialEntryException(CredentialRule);
                    Native.GetWindowThreadProcessId((nint)hwnd, out var pid);
                    if (focused.Current.ProcessId != pid) throw new InvalidOperationException("Focused control is outside the target process.");
                    return true;
                }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckForeground(hwnd);
            using var dpi = new Native.DpiScope();
            return operation();
        }
        finally { ReleaseAll(); SerialInput.Release(); }
    }

    /// <summary>
    /// Reaching a sign-in form is ordinary work; filling one is not. The owner types the secret and clears
    /// any MFA step, and the task continues afterwards — so this reads as a pause, not a dead end.
    /// </summary>
    internal const string CredentialRule =
        "The focused control is a password or credential field. PC Assistant never types secrets, one-time codes or " +
        "recovery answers. Pause here: tell the owner the sign-in is ready and ask them to enter the credential and " +
        "finish any multi-factor step themselves. Once they confirm they are signed in, observe the screen again and " +
        "continue the task from there.";

    // Final guard immediately before injection: the desktop can change under a long operation.
    private static void CheckForeground(long hwnd)
    {
        var target = Native.Window(hwnd);
        if (!Native.IsWindowVisible(target) || Native.IsIconic(target) || Native.GetForegroundWindow() != target)
            throw new ForegroundUnavailableException($"Window {hwnd} lost the foreground during input; nothing further was sent. Observe the screen before retrying.");
    }
    private static void CheckPoint(long hwnd, int x, int y)
    {
        var hit = Native.WindowFromPoint(new Native.Point { X = x, Y = y });
        if (hit == 0 || Native.GetAncestor(hit, 2) != (nint)hwnd) throw new ArgumentException("The pixel point must hit the selected foreground window, not another window or an occluding popup.");
    }
    private static void Inject(Native.Input[] events, Native.Input[] releases)
    {
        lock (HeldLock)
        {
            PendingReleases.AddRange(releases);
            var inserted = Native.SendInput((uint)events.Length, events, Marshal.SizeOf<Native.Input>());
            if (inserted != events.Length) throw new InvalidOperationException($"Windows inserted {inserted}/{events.Length} input events (error {Marshal.GetLastWin32Error()}); input may be blocked by integrity restrictions. State may be partially changed.");
            PendingReleases.Clear();
        }
    }
    private static Native.Input Move(int x, int y)
    {
        var left = Native.GetSystemMetrics(76); var top = Native.GetSystemMetrics(77);
        var width = Native.GetSystemMetrics(78); var height = Native.GetSystemMetrics(79);
        if (width <= 1 || height <= 1 || x < left || y < top || x >= (long)left + width || y >= (long)top + height)
            throw new ArgumentOutOfRangeException(nameof(x), "Coordinates must be physical pixels on the virtual desktop.");
        var input = Mouse(0x0001 | 0x8000 | 0x4000);
        input.Data.Mouse.X = (int)Math.Round((x - (double)left) * 65535 / (width - 1));
        input.Data.Mouse.Y = (int)Math.Round((y - (double)top) * 65535 / (height - 1));
        return input;
    }
    private static Native.Input Mouse(uint flags) => new() { Type = 0, Data = new Native.InputUnion { Mouse = new Native.MouseInput { Flags = flags } } };
    private static Native.Input Unicode(char value, bool up) => new() { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { ScanCode = value, Flags = 0x0004u | (up ? 0x0002u : 0) } } };
    private static Native.Input Key(ushort code, bool up) => new() { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { VirtualKey = code, Flags = (up ? 0x0002u : 0) | (code is >= 0x21 and <= 0x2E ? 0x0001u : 0) } } };
    private static (uint Down, uint Up) Button(string button) => button?.ToLowerInvariant() switch { "left" => (0x0002, 0x0004), "right" => (0x0008, 0x0010), "middle" => (0x0020, 0x0040), _ => throw new ArgumentException("Button must be left, right, or middle.", nameof(button)) };
    private static ushort ParseKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32) throw new ArgumentException("Key names must contain 1–32 characters.");
        var key = name.ToUpperInvariant();
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return key[0];
        if (key.StartsWith('F') && int.TryParse(key[1..], out var function) && function is >= 1 and <= 12) return (ushort)(0x70 + function - 1);
        return key switch { "CTRL" or "CONTROL" => 0x11, "SHIFT" => 0x10, "ALT" => 0x12, "ENTER" => 0x0D, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" => 0x08, "DELETE" => 0x2E, "SPACE" => 0x20, "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, _ => throw new ArgumentException($"Unsupported key name: {name}.") };
    }
    public void Dispose() { ReleaseAll(); automation.Dispose(); }
}
