using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace PcCore.Windows.Input;

/// <summary>What the installer found, and what the owner should do about it.</summary>
public sealed record NativeInputInstallation
{
    public required bool Registered { get; init; }

    /// <summary>The executable the registered task actually points at, when there is one.</summary>
    public string? RegisteredCommand { get; init; }

    /// <summary>Where the broker lives now. A mismatch means the build moved and needs repairing.</summary>
    public required string ExpectedCommand { get; init; }

    /// <summary>True when the task runs with the highest privileges available to the owner.</summary>
    public required bool Elevated { get; init; }

    public bool UpToDate => Registered
        && Elevated
        && string.Equals(RegisteredCommand, ExpectedCommand, StringComparison.OrdinalIgnoreCase);

    public string? Diagnostic { get; init; }
}

/// <summary>
/// Installs, repairs and removes the Nexus Input Broker's place in the normal Windows lifecycle.
/// <para>
/// <b>Why a scheduled task and not a service.</b> The broker has to run <em>in the owner's own
/// interactive session</em>, because that is where the desktop it injects into lives; a service runs
/// in session 0 and would have to spawn a helper into the session anyway. It also has to start at
/// logon without asking for anything, and it has to be visible: the owner can see it in Task
/// Scheduler, under a name that says what it is, and delete it there if they ever want to. A logon
/// task with highest privileges is the documented Windows mechanism for exactly this shape of thing,
/// and it is the least machinery that does the job.
/// </para>
/// <para>
/// <b>What this deliberately is not.</b> It is not stealth persistence: one named task, one folder,
/// one executable, and an uninstall that removes all of it. It does not touch UAC policy, driver
/// signing, Secure Boot or Memory Integrity, and it does not install a driver. Creating the task
/// needs administrator rights once — which is the honest price of the capability, and is asked for
/// through the ordinary Windows consent prompt rather than worked around.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeInputInstaller
{
    /// <summary>The task's full path. Visible under this name in Task Scheduler.</summary>
    public const string TaskPath = @"\Nexus\Nexus Input Broker";

    public const string BrokerExecutableName = "PcCore.InputBroker.exe";

    private readonly ILogger<NativeInputInstaller> _logger;

    public NativeInputInstaller(ILogger<NativeInputInstaller> logger) => _logger = logger;

    /// <summary>
    /// Where the broker executable is expected to be: beside PC Core's own.
    /// <para>
    /// Both are published to the same directory, which is what lets the broker verify that the
    /// process connecting to it is the PC Core it shipped with rather than merely something running
    /// as the owner.
    /// </para>
    /// </summary>
    public static string ExpectedBrokerPath => Path.Combine(AppContext.BaseDirectory, BrokerExecutableName);

    public NativeInputInstallation GetStatus()
    {
        var expected = ExpectedBrokerPath;
        var query = Run("/Query", "/TN", TaskPath, "/XML", "ONE");

        if (query.ExitCode != 0)
        {
            return new NativeInputInstallation
            {
                Registered = false,
                ExpectedCommand = expected,
                Elevated = false,
            };
        }

        try
        {
            // The XML is Task Scheduler's own, namespaced. Read by local name so a schema version
            // bump does not turn "installed" into "not installed".
            var document = XDocument.Parse(query.Output);

            var command = document.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim().Trim('"');

            var runLevel = document.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "RunLevel")?.Value.Trim();

            return new NativeInputInstallation
            {
                Registered = true,
                RegisteredCommand = command,
                ExpectedCommand = expected,
                Elevated = string.Equals(runLevel, "HighestAvailable", StringComparison.OrdinalIgnoreCase),
            };
        }
        catch (System.Xml.XmlException ex)
        {
            return new NativeInputInstallation
            {
                Registered = true,
                ExpectedCommand = expected,
                Elevated = false,
                Diagnostic = $"the registered task could not be read ({ex.Message})",
            };
        }
    }

    /// <summary>
    /// Registers or updates the logon task. Requires administrator rights.
    /// </summary>
    /// <returns>The status afterwards, whether or not it changed.</returns>
    public NativeInputInstallation Install()
    {
        var broker = ExpectedBrokerPath;

        if (!File.Exists(broker))
        {
            return new NativeInputInstallation
            {
                Registered = false,
                ExpectedCommand = broker,
                Elevated = false,
                Diagnostic = $"{BrokerExecutableName} is not next to PC Core; build the solution first.",
            };
        }

        if (!IsElevated)
        {
            return GetStatus() with
            {
                Diagnostic = "Registering the logon task needs administrator rights. "
                    + "Run this command from an elevated terminal.",
            };
        }

        var file = Path.Combine(Path.GetTempPath(), $"nexus-input-broker-{Guid.NewGuid():N}.xml");

        try
        {
            // Written as UTF-16 with a BOM: schtasks refuses task XML in anything else.
            File.WriteAllText(file, TaskDefinition(broker), new UnicodeEncoding(false, true));

            var result = Run("/Create", "/TN", TaskPath, "/XML", file, "/F");

            if (result.ExitCode != 0)
            {
                return GetStatus() with { Diagnostic = Summarise(result) };
            }

            _logger.LogInformation("Registered the Nexus Input Broker logon task at {Task}.", TaskPath);

            // Started now as well as at the next logon, so installing does not mean signing out.
            var started = Run("/Run", "/TN", TaskPath);

            if (started.ExitCode != 0)
            {
                _logger.LogWarning(
                    "The broker task was registered but did not start: {Diagnostic}", Summarise(started));
            }

            return GetStatus();
        }
        catch (IOException ex)
        {
            return GetStatus() with { Diagnostic = ex.Message };
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leftover temporary file is not worth failing an install over.
            }
        }
    }

    /// <summary>Removes the task and stops the running broker. Requires administrator rights.</summary>
    public bool Uninstall()
    {
        if (!IsElevated)
        {
            _logger.LogError("Removing the logon task needs administrator rights.");
            return false;
        }

        Run("/End", "/TN", TaskPath);
        var result = Run("/Delete", "/TN", TaskPath, "/F");

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("Removed the Nexus Input Broker logon task.");
            return true;
        }

        _logger.LogWarning("The broker task could not be removed: {Diagnostic}", Summarise(result));
        return false;
    }

    public static bool IsElevated =>
        OperatingSystem.IsWindows()
        && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// The task definition.
    /// <para>
    /// <c>InteractiveToken</c> with <c>HighestAvailable</c> is what makes the broker start elevated
    /// in the owner's own desktop session at logon, without a consent prompt every day and without
    /// anything running when nobody is signed in. No time limit, because it is meant to live as long
    /// as the session; restart on failure, because a broker that has died silently is the one state
    /// this design cannot notice on its own.
    /// </para>
    /// </summary>
    private static string TaskDefinition(string broker)
    {
        var user = WindowsIdentity.GetCurrent().Name;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Nexus</Author>
                <Description>Starts the Nexus Input Broker so Nexus can control ordinary elevated application windows. Removing this task returns Nexus to standard-user input.</Description>
                <URI>{TaskPath}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Owner">
                  <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Owner">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(broker)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string Summarise(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        var line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();

        return string.IsNullOrEmpty(line) ? $"schtasks exited with {result.ExitCode}" : line;
    }

    private static ProcessResult Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // schtasks writes localised text; asking for UTF-8 back keeps a Turkish error message
            // readable rather than turning it into replacement characters in a log.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);

            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, "schtasks could not be started");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(30));

            return new ProcessResult(process.HasExited ? process.ExitCode : -1, output, error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string Output, string Error);
}
