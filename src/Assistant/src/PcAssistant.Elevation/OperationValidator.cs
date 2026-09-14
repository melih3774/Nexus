using System.Text;
using System.Text.RegularExpressions;

namespace PcAssistant.Elevation;

/// <summary>Everything the owner needs to see before approving, plus how cancellation should behave.</summary>
public sealed record OperationDescription(
    string Action,
    string Target,
    string Impact,
    string Privilege,
    bool Interruptible);

public sealed record ValidationResult(bool Ok, string? Error, OperationDescription? Description)
{
    public static ValidationResult Reject(string error) => new(false, error, null);
    public static ValidationResult Accept(OperationDescription description) => new(true, null, description);
}

/// <summary>
/// The privileged parameter rules, in one place, run on both sides of the pipe.
///
/// The helper runs this again on every request and never relies on the client having run
/// it. Sharing the source keeps the owner-facing description and the enforced rule from
/// drifting apart; it does not make the client's copy authoritative.
///
/// Scope of what this can honestly promise: it bounds *parameters*, not *meaning*. An
/// approved "powershell.exe -Command ..." is arbitrary code running with the owner's
/// administrator token, exactly as if the owner had typed it into an elevated console. The
/// protection is that the owner reads the literal command on the approval card and that
/// nothing reaches the helper without that approval — not that this class understands
/// PowerShell.
/// </summary>
public static partial class OperationValidator
{
    public const int MaxArguments = 64;
    public const int MaxArgumentLength = 4096;
    public const int MaxCommandLineLength = 16384;
    public const int MaxReasonLength = 2000;
    public const int MaxInitialCommandLength = 8000;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3600;
    public const int DefaultTimeoutSeconds = 300;

    private static readonly string[] Shells = ["powershell", "pwsh", "cmd"];

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNameRx();

    [GeneratedRegex(@"^[A-Za-z0-9]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationIdRx();

    /// <summary>
    /// A guardrail, not a boundary. It refuses the handful of changes that would dismantle
    /// the Windows protections this whole design is built on, so a mistaken or
    /// injection-suggested request is stopped before it ever reaches the owner's approval
    /// card. It does not and cannot parse arbitrary shell text; a determined approved
    /// command can still do anything an administrator can do.
    /// </summary>
    [GeneratedRegex(
        @"EnableLUA|ConsentPromptBehaviorAdmin|PromptOnSecureDesktop|FilterAdministratorToken" +
        @"|TypeOfAdminApprovalMode|EnableSecureUIAPaths|ValidateAdminCodeSignatures" +
        @"|EnableInstallerDetection|EnableVirtualization",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecurityPolicyRx();

    /// <summary>Operations whose interruption can leave Windows half-configured.</summary>
    [GeneratedRegex(
        @"^(?:winget|msiexec|choco|scoop|setup|install|installer|dism|sfc|vs_[A-Za-z]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveExecutableRx();

    public static ValidationResult Validate(PrivilegedRequest request)
    {
        if (request.Protocol != PrivilegedProtocol.Version)
            return ValidationResult.Reject($"Protocol {request.Protocol} is not protocol {PrivilegedProtocol.Version}.");

        if (request.Reason is { Length: > MaxReasonLength })
            return ValidationResult.Reject($"Reason exceeds {MaxReasonLength} characters.");

        return request.Kind switch
        {
            PrivilegedOperationKind.Handshake or PrivilegedOperationKind.Status =>
                ValidationResult.Accept(new("Check the privileged helper", "status", "Nothing is changed.", "None", true)),
            PrivilegedOperationKind.Cancel => ValidateCancel(request),
            PrivilegedOperationKind.Terminal => ValidateTerminal(request),
            PrivilegedOperationKind.Run or PrivilegedOperationKind.Launch => ValidateProgram(request),
            PrivilegedOperationKind.Service => ValidateService(request),
            _ => ValidationResult.Reject("Unknown privileged operation."),
        };
    }

    private static ValidationResult ValidateCancel(PrivilegedRequest request)
        => string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 64
            ? ValidationResult.Reject("Cancel needs the operation id returned when the operation started.")
            : ValidationResult.Accept(new("Cancel a running administrator operation",
                request.OperationId, "Terminates only that operation's own process tree.", "None", true));

    private static ValidationResult ValidateTerminal(PrivilegedRequest request)
    {
        var shell = (request.Shell ?? "powershell").Trim().ToLowerInvariant();
        if (!Shells.Contains(shell))
            return ValidationResult.Reject("Shell must be powershell, pwsh or cmd.");

        if (request.InitialCommand is { } initial)
        {
            if (initial.Length > MaxInitialCommandLength)
                return ValidationResult.Reject($"Initial command exceeds {MaxInitialCommandLength} characters.");
            if (initial.Any(c => char.IsControl(c) && c != '\t'))
                return ValidationResult.Reject("Initial command may not contain control characters.");
            if (SecurityPolicyRx().IsMatch(initial))
                return ValidationResult.Reject(SecurityPolicyRefusal);
        }

        if (WorkingDirectoryError(request.WorkingDirectory) is { } workingDirectoryError)
            return ValidationResult.Reject(workingDirectoryError);

        var name = shell switch
        {
            "cmd" => "Command Prompt",
            "pwsh" => "PowerShell 7",
            _ => "Windows PowerShell",
        };

        var target = string.IsNullOrWhiteSpace(request.InitialCommand)
            ? name
            : name + "  >  " + request.InitialCommand!.Trim();

        return ValidationResult.Accept(new(
            "Open an Administrator terminal",
            target,
            "Opens a visible elevated shell on your desktop. It stays open and under your control; " +
            "PC Assistant cannot type into an elevated window.",
            AdministratorPrivilege,
            Interruptible: false));
    }

    private static ValidationResult ValidateProgram(PrivilegedRequest request)
    {
        var executable = request.Executable?.Trim();
        if (string.IsNullOrWhiteSpace(executable))
            return ValidationResult.Reject("An executable path is required.");
        if (executable.Length > 1024)
            return ValidationResult.Reject("Executable path is too long.");
        if (executable.StartsWith(@"\\", StringComparison.Ordinal))
            return ValidationResult.Reject("UNC and device paths are not accepted; use a local path.");
        if (executable.Any(char.IsControl))
            return ValidationResult.Reject("Executable path may not contain control characters.");
        if (!Path.IsPathFullyQualified(executable))
            return ValidationResult.Reject("Give the full path to the executable; the helper does not search PATH.");

        string full;
        try { full = Path.GetFullPath(executable); }
        catch (Exception ex) { return ValidationResult.Reject("Executable path is not usable: " + ex.Message); }

        if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Reject("Only .exe programs are launched directly. Run an installer through its own " +
                                           "exe, or an .msi through msiexec.exe with arguments.");
        // Self-protection is checked before existence on purpose: the refusal is about where
        // the path points, not about what happens to be there right now.
        if (IsInsideHelper(full))
            return ValidationResult.Reject("The helper will not run programs from its own installation directory.");
        if (!File.Exists(full))
            return ValidationResult.Reject("Executable does not exist: " + full);

        var arguments = request.Arguments ?? Array.Empty<string>();
        if (arguments.Count > MaxArguments)
            return ValidationResult.Reject($"At most {MaxArguments} arguments are accepted.");
        foreach (var argument in arguments)
        {
            if (argument is null) return ValidationResult.Reject("Arguments may not be null.");
            if (argument.Length > MaxArgumentLength)
                return ValidationResult.Reject($"An argument exceeds {MaxArgumentLength} characters.");
            if (argument.Any(c => c is '\0' or '\r' or '\n'))
                return ValidationResult.Reject("Arguments may not contain null or newline characters.");
        }

        var commandLine = BuildCommandLine(full, arguments);
        if (commandLine.Length > MaxCommandLineLength)
            return ValidationResult.Reject($"The whole command line exceeds {MaxCommandLineLength} characters.");
        if (SecurityPolicyRx().IsMatch(commandLine))
            return ValidationResult.Reject(SecurityPolicyRefusal);

        if (WorkingDirectoryError(request.WorkingDirectory) is { } directoryError)
            return ValidationResult.Reject(directoryError);

        if (request.Kind == PrivilegedOperationKind.Run)
        {
            var timeout = request.TimeoutSeconds == 0 ? DefaultTimeoutSeconds : request.TimeoutSeconds;
            if (timeout is < MinTimeoutSeconds or > MaxTimeoutSeconds)
                return ValidationResult.Reject($"Timeout must be {MinTimeoutSeconds}-{MaxTimeoutSeconds} seconds.");

            // The client names the operation up front so it can cancel it later: the helper's
            // answer only arrives when the command has already finished.
            if (request.OperationId is { } id && !OperationIdRx().IsMatch(id))
                return ValidationResult.Reject("Operation id must be 1-64 letters or digits.");
        }

        var sensitive = SensitiveExecutableRx().IsMatch(Path.GetFileNameWithoutExtension(full));

        var impact = sensitive
            ? "Installs or reconfigures software on Windows. Because interrupting this can leave the system " +
              "half-configured, PC Assistant will not terminate it once it has started."
            : request.Kind == PrivilegedOperationKind.Run
                ? "Runs with your administrator token. Output and exit code are reported back."
                : "Starts a visible elevated program on your desktop and leaves it running.";

        return ValidationResult.Accept(new(
            request.Kind == PrivilegedOperationKind.Run
                ? "Run a command as Administrator"
                : "Launch a program as Administrator",
            Display(full, arguments),
            impact,
            AdministratorPrivilege,
            Interruptible: request.Kind == PrivilegedOperationKind.Run && !sensitive));
    }

    private static ValidationResult ValidateService(PrivilegedRequest request)
    {
        var name = request.ServiceName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || !ServiceNameRx().IsMatch(name))
            return ValidationResult.Reject("Service name must be 1-256 characters of letters, digits, dot, dash or underscore.");

        var action = (request.ServiceAction ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("start" or "stop" or "restart" or "query"))
            return ValidationResult.Reject("Service action must be start, stop, restart or query.");

        if (action != "query" &&
            string.Equals(name, PrivilegedProtocol.ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Reject(
                "The helper will not stop or restart itself. Use Uninstall Privileged Helper in PC Assistant instead.");
        }

        var impact = action switch
        {
            "query" => "Reads the service's current state. Nothing is changed.",
            "start" => "Starts a Windows service. Whatever that service does begins running.",
            "stop" => "Stops a Windows service. Anything depending on it stops working until it is started again.",
            _ => "Stops and starts a Windows service. It is briefly unavailable.",
        };

        return ValidationResult.Accept(new(
            action == "query"
                ? "Query a Windows service"
                : char.ToUpperInvariant(action[0]) + action[1..] + " a Windows service",
            name + "  (" + action + ")",
            impact,
            action == "query" ? "Administrator (read only)" : AdministratorPrivilege,
            Interruptible: true));
    }

    private const string AdministratorPrivilege =
        "Administrator - your own account's elevated token, in your desktop session. Not SYSTEM.";

    private const string SecurityPolicyRefusal =
        "Refused: this changes Windows User Account Control or elevation policy. PC Assistant's privileged " +
        "helper exists to work with those protections, not to weaken them. Make such a change yourself if you " +
        "really intend it.";

    private static string? WorkingDirectoryError(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return null;
        if (workingDirectory.Length > 1024) return "Working directory path is too long.";
        if (workingDirectory.StartsWith(@"\\", StringComparison.Ordinal)) return "UNC working directories are not accepted.";
        if (!Path.IsPathFullyQualified(workingDirectory)) return "Working directory must be a full path.";
        return Directory.Exists(workingDirectory) ? null : "Working directory does not exist: " + workingDirectory;
    }

    private static bool IsInsideHelper(string path)
    {
        try
        {
            var root = Path.GetFullPath(HelperPaths.InstallRoot).TrimEnd(Path.DirectorySeparatorChar);
            return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Owner-facing rendering. Deliberately the literal command, never a paraphrase.</summary>
    public static string Display(string executable, IReadOnlyList<string> arguments)
        => arguments.Count == 0
            ? executable
            : executable + " " + string.Join(' ', arguments.Select(QuoteForDisplay));

    private static string QuoteForDisplay(string argument)
        => argument.Length > 0 && !argument.Any(char.IsWhiteSpace)
            ? argument
            : "\"" + argument.Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Windows command-line quoting, per the documented CommandLineToArgvW rules. The helper
    /// never hands a single string to a shell for re-interpretation: the caller supplies an
    /// argument vector and this produces exactly the line that reproduces it.
    /// </summary>
    public static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        AppendQuoted(builder, executable);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendQuoted(builder, argument);
        }
        return builder.ToString();
    }

    private static void AppendQuoted(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }

            if (i == argument.Length) builder.Append('\\', backslashes * 2);
            else if (argument[i] == '"') builder.Append('\\', backslashes * 2 + 1).Append('"');
            else builder.Append('\\', backslashes).Append(argument[i]);
        }
        builder.Append('"');
    }
}
