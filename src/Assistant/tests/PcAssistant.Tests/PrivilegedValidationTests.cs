using System.Linq;
using System.Runtime.InteropServices;
using PcAssistant.Elevation;
using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// The parameter rules the privileged helper applies to every request, independently of
/// anything PC Assistant did.
///
/// These are deliberately tests of what is *refused*. The validator's honest claim is narrow:
/// it bounds parameters, it does not understand meaning. An approved
/// "powershell.exe -Command ..." is arbitrary elevated code and nothing here pretends
/// otherwise — the protection there is that the owner reads the literal command.
/// </summary>
public sealed class PrivilegedValidationTests
{
    private static string System32(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    private static PrivilegedRequest Run(string? executable = null, params string[] arguments) => new()
    {
        Kind = PrivilegedOperationKind.Run,
        Executable = executable ?? System32("whoami.exe"),
        Arguments = arguments,
        Reason = "test",
        TimeoutSeconds = 30,
    };

    // ------------------------------------------------------------- protocol

    [Fact]
    public void AnUnknownProtocolIsRefusedBeforeAnythingElseIsConsidered()
    {
        var result = OperationValidator.Validate(Run() with { Protocol = PrivilegedProtocol.Version + 1 });

        Assert.False(result.Ok);
        Assert.Contains("protocol", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------- executable rules

    [Theory]
    [InlineData(@"\\server\share\tool.exe", "UNC")]
    [InlineData(@"\\?\C:\Windows\System32\whoami.exe", "UNC")]
    [InlineData("whoami.exe", "full path")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "Only .exe")]
    public void OnlyLocalFullyQualifiedExeFilesAreAccepted(string executable, string expected)
    {
        var result = OperationValidator.Validate(Run(executable));

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissingExecutableIsRefusedRatherThanAttempted()
    {
        var result = OperationValidator.Validate(Run(@"C:\Windows\System32\definitely-not-here.exe"));

        Assert.False(result.Ok);
        Assert.Contains("does not exist", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHelperWillNotRunProgramsFromItsOwnInstallationDirectory()
    {
        // Self-protection: the install directory is the one place whose contents decide what
        // SYSTEM runs at next service start.
        var inside = Path.Combine(HelperPaths.InstallRoot, "anything.exe");
        var result = OperationValidator.Validate(Run(inside));

        Assert.False(result.Ok);
        Assert.Contains("own installation directory", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------- argument bounds

    [Fact]
    public void ArgumentsAreCountAndLengthBounded()
    {
        var many = OperationValidator.Validate(
            Run(null, Enumerable.Repeat("x", OperationValidator.MaxArguments + 1).ToArray()));
        Assert.False(many.Ok);

        var long_ = OperationValidator.Validate(
            Run(null, new string('x', OperationValidator.MaxArgumentLength + 1)));
        Assert.False(long_.Ok);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void ArgumentsMayNotSmuggleNewlinesOrNulls(string argument)
    {
        var result = OperationValidator.Validate(Run(null, argument));

        Assert.False(result.Ok);
        Assert.Contains("newline", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWholeCommandLineIsBounded()
    {
        var chunk = new string('x', OperationValidator.MaxArgumentLength);
        var arguments = Enumerable.Repeat(chunk, 8).ToArray();

        var result = OperationValidator.Validate(Run(null, arguments));

        Assert.False(result.Ok);
        Assert.Contains("command line", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------- Windows security policy

    [Theory]
    [InlineData("EnableLUA")]
    [InlineData("ConsentPromptBehaviorAdmin")]
    [InlineData("PromptOnSecureDesktop")]
    [InlineData("FilterAdministratorToken")]
    public void RequestsThatWouldWeakenUacAreRefused(string marker)
    {
        var viaRun = OperationValidator.Validate(
            Run(System32("reg.exe"), "add", @"HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System",
                "/v", marker, "/t", "REG_DWORD", "/d", "0", "/f"));
        Assert.False(viaRun.Ok);
        Assert.Contains("User Account Control", viaRun.Error!, StringComparison.OrdinalIgnoreCase);

        var viaTerminal = OperationValidator.Validate(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Terminal,
            Shell = "powershell",
            InitialCommand = $"Set-ItemProperty HKLM:\\... -Name {marker} -Value 0",
            Reason = "test",
        });
        Assert.False(viaTerminal.Ok);
    }

    // ------------------------------------------------------------- terminal

    [Theory]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    [InlineData("cmd", true)]
    [InlineData("bash", false)]
    [InlineData("wt", false)]
    public void OnlyKnownShellsOpenAnAdministratorTerminal(string shell, bool accepted)
    {
        var result = OperationValidator.Validate(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Terminal, Shell = shell, Reason = "test",
        });

        Assert.Equal(accepted, result.Ok);
    }

    [Fact]
    public void AnAdministratorTerminalIsNotSomethingPcAssistantCanCancel()
    {
        var result = OperationValidator.Validate(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Terminal, Shell = "powershell", Reason = "test",
        });

        // A visible elevated window belongs to the owner's desktop the moment it appears.
        Assert.True(result.Ok);
        Assert.False(result.Description!.Interruptible);
        Assert.Contains("cannot type into an elevated window", result.Description.Impact, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------- services

    [Theory]
    [InlineData("Spooler", "query", true)]
    [InlineData("Spooler", "restart", true)]
    [InlineData("Spooler", "destroy", false)]
    [InlineData(@"..\..\evil", "query", false)]
    [InlineData("name with spaces", "query", false)]
    public void ServiceRequestsAreNameAndActionBounded(string name, string action, bool accepted)
    {
        var result = OperationValidator.Validate(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Service, ServiceName = name, ServiceAction = action, Reason = "test",
        });

        Assert.Equal(accepted, result.Ok);
    }

    [Fact]
    public void TheHelperRefusesToStopOrRestartItselfButWillStillReportItsState()
    {
        foreach (var action in new[] { "stop", "restart", "start" })
        {
            var result = OperationValidator.Validate(new PrivilegedRequest
            {
                Kind = PrivilegedOperationKind.Service,
                ServiceName = PrivilegedProtocol.ServiceName, ServiceAction = action, Reason = "test",
            });
            Assert.False(result.Ok);
            Assert.Contains("Uninstall Privileged Helper", result.Error!, StringComparison.OrdinalIgnoreCase);
        }

        var query = OperationValidator.Validate(new PrivilegedRequest
        {
            Kind = PrivilegedOperationKind.Service,
            ServiceName = PrivilegedProtocol.ServiceName, ServiceAction = "query", Reason = "test",
        });
        Assert.True(query.Ok);
    }

    // --------------------------------------------------- timeouts and ids

    [Theory]
    [InlineData(0, true)]      // 0 means "use the default"
    [InlineData(1, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    [InlineData(-5, false)]
    public void RunTimeoutsAreBounded(int seconds, bool accepted)
        => Assert.Equal(accepted, OperationValidator.Validate(Run() with { TimeoutSeconds = seconds }).Ok);

    [Theory]
    [InlineData("abc123", true)]
    [InlineData("has-a-dash", false)]
    [InlineData("", false)]
    public void OperationIdsAreBounded(string id, bool accepted)
        => Assert.Equal(accepted, OperationValidator.Validate(Run() with { OperationId = id }).Ok);

    // ------------------------------------------------- cancellation judgement

    [Fact]
    public void OrdinaryCommandsAreInterruptibleAndInstallersAreNot()
    {
        var ordinary = OperationValidator.Validate(Run(System32("whoami.exe"), "/groups"));
        Assert.True(ordinary.Description!.Interruptible);

        var installer = OperationValidator.Validate(Run(System32("msiexec.exe"), "/i", @"C:\pkg.msi"));
        Assert.True(installer.Ok);
        Assert.False(installer.Description!.Interruptible);
        Assert.Contains("half-configured", installer.Description.Impact, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------ command-line quoting

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgv(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [Theory]
    [InlineData("plain")]
    [InlineData("has spaces")]
    [InlineData("has\"quote")]
    [InlineData(@"trailing\backslash\")]
    [InlineData(@"C:\Program Files\App\thing.exe")]
    [InlineData(@"-Command Write-Output ""hello world""")]
    public void TheArgumentVectorSurvivesWindowsCommandLineParsingUnchanged(string argument)
    {
        // The helper builds a command line from an argument vector and never hands a single
        // string to a shell to re-split. This proves Windows itself reads back what was meant.
        const string executable = @"C:\Windows\System32\whoami.exe";
        var commandLine = OperationValidator.BuildCommandLine(executable, [argument, "second"]);

        var pointer = CommandLineToArgv(commandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            var parsed = new string[count];
            for (var i = 0; i < count; i++)
                parsed[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!;

            Assert.Equal(3, count);
            Assert.Equal(executable, parsed[0]);
            Assert.Equal(argument, parsed[1]);
            Assert.Equal("second", parsed[2]);
        }
        finally
        {
            LocalFree(pointer);
        }
    }
}
