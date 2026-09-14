using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PcAssistant.ClaudeCode;
using PcAssistant.Windows;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// Guards the deadlock that made interactive input impossible: an approval card can only be answered by
/// clicking PC Assistant, which takes the foreground away from the very window the approved input needs.
/// Composing focus and input as separate steps can never win that race, so input owns both.
/// </summary>
[Collection(DesktopCollection.Name)]
public class ForegroundRegressionTests
{
    [SkippableFact]
    public async Task ApprovedInputReacquiresTheForegroundTheApprovalStoleAndSucceedsOnOneApproval()
    {
        Skip.If(Environment.GetEnvironmentVariable("PCASSISTANT_NATIVE_SMOKE") != "1",
            "Set PCASSISTANT_NATIVE_SMOKE=1 to allow foreground/input against the disposable fixtures.");

        var restore = GetForegroundWindow();
        using var target = await WindowFixture.CreateAsync("Regression target (stands in for the browser)", 60, 60);
        using var approver = await WindowFixture.CreateAsync("Regression approver (stands in for PC Assistant)", 520, 60);

        var inputApprovals = 0;
        long stolenForeground = 0;
        var pipeName = "pcassistant-foreground-test-" + Guid.NewGuid().ToString("N");

        await using var pipe = new ApprovalPipeServer(pipeName, async (request, _) =>
        {
            if (!request.ToolName.EndsWith("__input", StringComparison.Ordinal)) return HookReply.Allow("not under test");
            Interlocked.Increment(ref inputApprovals);

            // The owner answers the card by clicking PC Assistant's window. That focus theft is not
            // incidental to the approval - it is the approval.
            approver.Activate();
            stolenForeground = await WaitForForegroundAsync(approver.Hwnd);

            // Only the process that owns the foreground may pass that right on, so it happens here.
            Assert.True(ForegroundHandover.GrantTo(request.ProcessId),
                "PC Assistant must be able to hand the foreground privilege to the PC tools process.");
            return HookReply.Allow("Approved by the owner.");
        });
        pipe.Start();

        using var tools = PcToolsProcess.Start(pipeName);
        try
        {
            await tools.HandshakeAsync();

            target.Activate();
            Assert.Equal(target.Hwnd, await WaitForForegroundAsync(target.Hwnd));

            var centre = target.PhysicalCentre();

            // One call. No window_action focus, no minimizing PC Assistant, no retry.
            var result = await tools.CallAsync("input", new { hwnd = target.Hwnd, action = "click", x = centre.X, y = centre.Y });

            Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(),
                "Approved input must not fail: " + result);
            Assert.Equal(approver.Hwnd, stolenForeground);          // the approval really did steal the foreground
            Assert.Equal(1, inputApprovals);                        // and it was only asked once
            Assert.Equal(target.Hwnd, (long)GetForegroundWindow()); // input put the target back in front itself
            await WaitForAsync(() => target.ClickCount == 1);
            Assert.Equal(1, target.ClickCount);                     // and the click actually landed
        }
        finally
        {
            InputService.ReleaseAll();
            if (restore != 0) SetForegroundWindow(restore);
        }
    }

    /// <summary>
    /// The same approved click, answered from the phone through Nexus: nobody touches the desktop, so PC
    /// Assistant never becomes the foreground process and cannot hand the privilege on. Another program
    /// owns the foreground throughout, as it would while the owner is away. The click must still land.
    /// </summary>
    [SkippableFact]
    public async Task RemotelyApprovedInputReachesItsTargetWithoutAnyDesktopClick()
    {
        Skip.If(Environment.GetEnvironmentVariable("PCASSISTANT_NATIVE_SMOKE") != "1",
            "Set PCASSISTANT_NATIVE_SMOKE=1 to allow foreground/input against the disposable fixtures.");

        var restore = GetForegroundWindow();
        using var target = await WindowFixture.CreateAsync("Remote approval target", 60, 60);
        using var bystander = BystanderProcess.Start("Remote approval bystander (another program in front)");

        var inputApprovals = 0;
        var pipeName = "pcassistant-remote-approval-test-" + Guid.NewGuid().ToString("N");

        await using var pipe = new ApprovalPipeServer(pipeName, (request, _) =>
        {
            if (!request.ToolName.EndsWith("__input", StringComparison.Ordinal)) return Task.FromResult(HookReply.Allow("not under test"));
            Interlocked.Increment(ref inputApprovals);

            // No handover at all. ClaudeCodeBridge still tries one, but from a process nobody clicked it
            // fails, and whether this test's own process happens to be able to grant it must not decide
            // the outcome.
            return Task.FromResult(HookReply.Allow("Approved from the phone."));
        });
        pipe.Start();

        using var tools = PcToolsProcess.Start(pipeName);
        try
        {
            await tools.HandshakeAsync();
            var bystanderWindow = await bystander.WaitForWindowAsync();
            await WaitForForegroundAsync(bystanderWindow);

            // Whatever holds the foreground now — the bystander, or whatever the owner left in front — it
            // is neither the target nor anything the approval touched.
            var before = GetForegroundWindow();
            Assert.NotEqual(target.Hwnd, (long)before);
            GetWindowThreadProcessId(before, out var holder);
            var holderName = holder == 0 ? "none" : SafeProcessName((int)holder);

            var centre = target.PhysicalCentre();
            var result = await tools.CallAsync("input", new { hwnd = target.Hwnd, action = "click", x = centre.X, y = centre.Y });

            Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(),
                $"Remotely approved input must not fail (foreground held by {holderName}): " + result);
            Assert.Equal(1, inputApprovals);
            Assert.Equal(target.Hwnd, (long)GetForegroundWindow());
            await WaitForAsync(() => target.ClickCount == 1);
            Assert.Equal(1, target.ClickCount);
        }
        finally
        {
            InputService.ReleaseAll();
            if (restore != 0) SetForegroundWindow(restore);
        }
    }

    /// <summary>A window owned by a separate process, so the test's own process does not hold the foreground.</summary>
    private sealed class BystanderProcess(Process process, string title) : IDisposable
    {
        public static BystanderProcess Start(string title)
        {
            var script =
                "Add-Type -AssemblyName System.Windows.Forms; " +
                "$f = New-Object System.Windows.Forms.Form; " +
                $"$f.Text = '{title}'; $f.StartPosition = 'Manual'; $f.Left = 30; $f.Top = 30; $f.Width = 700; $f.Height = 500; " +
                "$f.TopMost = $false; [void]$f.ShowDialog()";
            var process = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
            {
                UseShellExecute = false, CreateNoWindow = true,
            })!;
            return new BystanderProcess(process, title);
        }

        public async Task<long> WaitForWindowAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var window = new DesktopWindows().ListWindows(300).FirstOrDefault(w => w.Title == title);
                if (window is not null) return window.Hwnd;
                await Task.Delay(100, timeout.Token);
            }
        }

        public void Dispose()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.Dispose();
        }
    }

    [Fact]
    public async Task ThirdInputOnAnUnreachableWindowIsRefusedWithoutAskingTheOwnerAgain()
    {
        // End to end through the real PC tools process: the owner is asked twice, never a third time.
        using var unreachable = await WindowFixture.CreateHiddenAsync();
        var approvals = 0;
        var pipeName = "pcassistant-foreground-guard-" + Guid.NewGuid().ToString("N");

        await using var pipe = new ApprovalPipeServer(pipeName, (request, _) =>
        {
            if (request.ToolName.EndsWith("__input", StringComparison.Ordinal)) Interlocked.Increment(ref approvals);
            return Task.FromResult(HookReply.Allow("owner allowed"));
        });
        pipe.Start();

        using var tools = PcToolsProcess.Start(pipeName);
        await tools.HandshakeAsync();
        var call = new { hwnd = unreachable.Hwnd, action = "click", x = 10, y = 10 };

        var first = await tools.CallAsync("input", call);
        var second = await tools.CallAsync("input", call);
        var third = await tools.CallAsync("input", call);

        Assert.True(first.GetProperty("isError").GetBoolean());
        Assert.True(second.GetProperty("isError").GetBoolean());
        Assert.True(third.GetProperty("isError").GetBoolean());

        Assert.Equal(2, approvals); // the third never reached the owner
        Assert.Contains("times in a row", Text(third), StringComparison.Ordinal);
        Assert.Contains("was not attempted and the owner was not asked again", Text(third), StringComparison.Ordinal);
    }

    private static string Text(JsonElement result) =>
        string.Concat(result.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("text").GetString()));

    [Fact]
    public async Task TwoConsecutiveForegroundFailuresStopAskingInsteadOfLoopingOnApprovals()
    {
        // A window with a handle but no visibility can never come to the front, which is the deterministic
        // stand-in for Windows refusing activation - and it never touches the owner's real foreground.
        using var unreachable = await WindowFixture.CreateHiddenAsync();
        ForegroundService.Reset(unreachable.Hwnd);
        try
        {
            Assert.False(ForegroundService.IsBlocked(unreachable.Hwnd));

            var first = await ForegroundService.EnsureAsync(unreachable.Hwnd);
            Assert.False(first.Verified);
            Assert.Equal(1, ForegroundService.FailureCount(unreachable.Hwnd));
            Assert.False(ForegroundService.IsBlocked(unreachable.Hwnd), "One failure is a retry, not a dead end.");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var second = await ForegroundService.EnsureAsync(unreachable.Hwnd);
            clock.Stop();
            Assert.False(second.Verified);
            Assert.True(ForegroundService.IsBlocked(unreachable.Hwnd), "The second consecutive failure stops the loop.");
            Assert.Contains("Stop retrying", second.Message, StringComparison.Ordinal);
            Assert.Contains("wait for them", ForegroundService.BlockedMessage(unreachable.Hwnd), StringComparison.Ordinal);

            // Bounded, not unbounded: a failing acquisition returns quickly instead of parking the tool call.
            Assert.True(second.Attempts <= 8, $"attempts were {second.Attempts}");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"acquisition took {clock.Elapsed}");
        }
        finally { ForegroundService.Reset(unreachable.Hwnd); }
    }

    [Fact]
    public async Task InputFailsClosedWhenTheForegroundCannotBeTakenAndReportsItAsAForegroundFailure()
    {
        using var unreachable = await WindowFixture.CreateHiddenAsync();
        ForegroundService.Reset(unreachable.Hwnd);
        try
        {
            using var input = new InputService(new PcAssistant.Windows.Capture.CaptureRegistry());
            var error = await Assert.ThrowsAsync<ForegroundUnavailableException>(
                () => input.ClickAsync(unreachable.Hwnd, 10, 10));
            Assert.Contains("could not be brought to the front", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, unreachable.ClickCount); // nothing was injected anywhere
        }
        finally { ForegroundService.Reset(unreachable.Hwnd); }
    }

    [Fact]
    public void CredentialFieldsPauseForTheOwnerRatherThanReadingAsAToolFailure()
    {
        var rule = (string)typeof(InputService)
            .GetField("CredentialRule", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;

        Assert.Contains("never types secrets", rule, StringComparison.Ordinal);
        Assert.Contains("multi-factor", rule, StringComparison.Ordinal);
        Assert.Contains("continue the task", rule, StringComparison.Ordinal);

        // A credential pause is not a foreground failure, so it can never count toward the two-strike guard.
        Assert.IsNotType<ForegroundUnavailableException>(new CredentialEntryException(rule));
    }

    // ------------------------------------------------------------------ helpers

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    private static string SafeProcessName(int processId)
    {
        try { return Process.GetProcessById(processId).ProcessName; }
        catch (Exception) { return "pid " + processId; }
    }

    private static async Task<long> WaitForForegroundAsync(long hwnd)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((long)GetForegroundWindow() != hwnd)
        {
            try { await Task.Delay(20, timeout.Token); }
            catch (OperationCanceledException) { break; }
        }
        return (long)GetForegroundWindow();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            try { await Task.Delay(20, timeout.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>A disposable real window: one full-surface button, so any centre click is a hit.</summary>
    private sealed class WindowFixture(Window window, long hwnd) : IDisposable
    {
        public long Hwnd { get; } = hwnd;
        public int ClickCount => window.Dispatcher.Invoke(() => (int)(window.Tag ?? 0));

        public void Activate() => window.Dispatcher.Invoke(() =>
        {
            window.Activate();
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
        });

        public (int X, int Y) PhysicalCentre()
        {
            var bounds = new DesktopWindows().ListWindows(300).Single(w => w.Hwnd == Hwnd).Bounds;
            return (bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }

        public static Task<WindowFixture> CreateAsync(string title, int left, int top) => CreateAsync(title, left, top, show: true);

        public static Task<WindowFixture> CreateHiddenAsync() => CreateAsync("Regression unreachable window", 0, 0, show: false);

        private static Task<WindowFixture> CreateAsync(string title, int left, int top, bool show)
        {
            var completion = new TaskCompletionSource<WindowFixture>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var button = new Button { Content = "Regression fixture surface" };
                    var window = new Window
                    {
                        Title = title, Left = left, Top = top, Width = 320, Height = 240,
                        ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow,
                        Content = button,
                    };
                    button.Click += (_, _) => window.Tag = (int)(window.Tag ?? 0) + 1;

                    // A hidden fixture gets a real handle without ever becoming visible, so acquisition
                    // always fails while the owner's actual desktop is left alone.
                    var handle = new WindowInteropHelper(window).EnsureHandle().ToInt64();
                    if (show) window.Show();

                    completion.SetResult(new WindowFixture(window, handle));
                    Dispatcher.Run();
                }
                catch (Exception error) { completion.TrySetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        public void Dispose() => window.Dispatcher.Invoke(() =>
        {
            window.Close();
            window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        });
    }

    /// <summary>The real PC tools executable over real stdio, driven the way Claude Code drives it.</summary>
    private sealed class PcToolsProcess(Process process) : IDisposable
    {
        private int _id = 1;

        public static PcToolsProcess Start(string pipeName)
        {
            var executable = FindPcTools();
            Assert.True(File.Exists(executable), "Desktop build must deploy the PC tools helper.");
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                },
            };
            process.StartInfo.Environment[HookProtocol.PipeEnvironmentVariable] = pipeName;
            Assert.True(process.Start());
            return new PcToolsProcess(process);
        }

        public async Task HandshakeAsync()
        {
            await SendAsync(new
            {
                jsonrpc = "2.0", id = 0, method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18", capabilities = new { },
                    clientInfo = new { name = "PcAssistant.Tests", version = "1" },
                },
            });
            await ResponseAsync(0);
            await SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
        }

        public async Task<JsonElement> CallAsync(string name, object arguments)
        {
            var id = Interlocked.Increment(ref _id);
            await SendAsync(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } });
            return (await ResponseAsync(id)).GetProperty("result");
        }

        private async Task SendAsync(object message)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            await process.StandardInput.FlushAsync();
        }

        private async Task<JsonElement> ResponseAsync(int id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.GetInt32() == id)
                    return document.RootElement.Clone();
            }
            throw new InvalidOperationException("PC tools closed before responding. " +
                await process.StandardError.ReadToEndAsync(timeout.Token));
        }

        private static string FindPcTools()
        {
            var deployed = Path.Combine(AppContext.BaseDirectory, "PcTools", "PcAssistant.PcTools.exe");
            if (File.Exists(deployed)) return deployed;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "PcAssistant.Desktop", "bin", "Debug",
                    "net10.0-windows", "PcTools", "PcAssistant.PcTools.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return deployed;
        }

        public void Dispose()
        {
            try { process.StandardInput.Close(); } catch { /* already gone */ }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.Dispose();
        }
    }
}
