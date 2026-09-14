using System.Text.Json;
using PcAssistant.Core.Model;
using PcAssistant.Core.Policy;
using Xunit;

namespace PcAssistant.Tests;

public class RiskClassifierTests
{
    private const string Workspace = @"C:\projects\demo";

    private static RiskAssessment Classify(string tool, object input, string cwd = Workspace)
    {
        var json = JsonSerializer.SerializeToElement(input);
        var call = ToolInvocation.FromJson(tool, json, cwd);
        return new RiskClassifier().Classify(call);
    }

    private static RiskCategory Bash(string command, string cwd = Workspace)
        => Classify("Bash", new { command }, cwd).Category;

    [Theory]
    [InlineData("Read")]
    [InlineData("Glob")]
    [InlineData("Grep")]
    [InlineData("WebSearch")]
    public void ReadOnlyToolsAreRead(string tool)
        => Assert.Equal(RiskCategory.Read, Classify(tool, new { pattern = "*.cs" }).Category);

    [Fact]
    public void WriteInsideWorkspaceIsWorkspaceWrite()
        => Assert.Equal(RiskCategory.WorkspaceWrite,
            Classify("Edit", new { file_path = @"C:\projects\demo\src\Main.cs" }).Category);

    [Fact]
    public void WriteOutsideWorkspaceIsOutsideWorkspace()
        => Assert.Equal(RiskCategory.OutsideWorkspace,
            Classify("Write", new { file_path = @"C:\Windows\System32\drivers\etc\hosts" }).Category);

    [Fact]
    public void ParentTraversalOutOfWorkspaceIsCaught()
        => Assert.Equal(RiskCategory.OutsideWorkspace,
            Classify("Write", new { file_path = @"C:\projects\demo\..\other\evil.txt" }).Category);

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet test --no-build")]
    [InlineData("npm run build")]
    [InlineData("git status")]
    [InlineData("git diff HEAD")]
    [InlineData("git log --oneline -20")]
    [InlineData("ls -la")]
    [InlineData("echo hello")]
    public void OrdinaryDevelopmentCommandsAreSafe(string command)
        => Assert.Equal(RiskCategory.CommandSafe, Bash(command));

    [Theory]
    [InlineData("git push origin main")]
    [InlineData("git commit -m wip")]
    [InlineData("git reset --hard HEAD~3")]
    [InlineData("git clean -fdx")]
    [InlineData("git remote add origin https://example.com/x.git")]
    [InlineData("git rebase -i main")]
    public void MutatingGitIsDestructiveGit(string command)
        => Assert.Equal(RiskCategory.DestructiveGit, Bash(command));

    [Theory]
    [InlineData("rm -rf build")]
    [InlineData("Remove-Item -Recurse -Force .\\bin")]
    [InlineData("del /s /q *.tmp")]
    [InlineData("Get-ChildItem | Remove-Item")]
    public void BulkDeletionIsHighImpact(string command)
        => Assert.Equal(RiskCategory.CommandHighImpact, Bash(command));

    [Theory]
    [InlineData("reg add HKCU\\Software\\Test /v A /d 1")]
    [InlineData("schtasks /create /tn evil /tr calc.exe /sc onlogon")]
    [InlineData("winget install Some.Package")]
    [InlineData("Set-ExecutionPolicy Unrestricted")]
    [InlineData("netsh advfirewall set allprofiles state off")]
    [InlineData("New-Service -Name Foo -BinaryPathName C:\\x.exe")]
    public void SystemLevelChangesAreSystemChange(string command)
        => Assert.Equal(RiskCategory.SystemChange, Bash(command));

    [Theory]
    [InlineData("sudo apt install nginx")]
    [InlineData("Start-Process powershell -Verb RunAs")]
    [InlineData("runas /user:Administrator cmd.exe")]
    public void ElevationIsDetected(string command)
        => Assert.Equal(RiskCategory.Elevation, Bash(command));

    [Theory]
    [InlineData("taskkill /F /IM chrome.exe")]
    [InlineData("Stop-Process -Name node -Force")]
    public void ProcessControlIsDetected(string command)
        => Assert.Equal(RiskCategory.ProcessControl, Bash(command));

    [Fact]
    public void AbsolutePathOutsideWorkspaceInCommandEscalates()
        => Assert.Equal(RiskCategory.OutsideWorkspace, Bash(@"type C:\Users\Someone\secrets.txt"));

    [Fact]
    public void AbsolutePathInsideWorkspaceStaysSafe()
        => Assert.Equal(RiskCategory.CommandSafe, Bash(@"dotnet build C:\projects\demo\demo.csproj"));

    [Fact]
    public void ChainedCommandTakesTheDangerousHalf()
        => Assert.Equal(RiskCategory.CommandHighImpact, Bash("echo hi && rm -rf ."));

    [Fact]
    public void SafeGitPrefixDoesNotLaunderAPush()
        => Assert.Equal(RiskCategory.DestructiveGit, Bash("git status && git push"));

    [Fact]
    public void UnknownToolIsTreatedConservatively()
        => Assert.Equal(RiskCategory.CommandHighImpact, Classify("SomeBrandNewTool", new { x = 1 }).Category);

    [Fact]
    public void McpToolIsTreatedConservatively()
        => Assert.Equal(RiskCategory.CommandHighImpact, Classify("mcp__server__do_thing", new { }).Category);

    [Fact]
    public void ReadOnlyGitHelperAgrees()
    {
        Assert.True(RiskClassifier.LooksLikeReadOnlyGit("git status"));
        Assert.False(RiskClassifier.LooksLikeReadOnlyGit("git push"));
    }
}

public class ApprovalPolicyTests
{
    [Theory]
    [InlineData(RiskCategory.Read)]
    [InlineData(RiskCategory.WorkspaceWrite)]
    [InlineData(RiskCategory.CommandSafe)]
    public void AutoAllowsRoutineWork(RiskCategory category)
        => Assert.Equal(PolicyDecision.Allow, ApprovalPolicy.Evaluate(AssistantMode.Auto, category));

    [Theory]
    [InlineData(RiskCategory.CommandHighImpact)]
    [InlineData(RiskCategory.OutsideWorkspace)]
    [InlineData(RiskCategory.SystemChange)]
    [InlineData(RiskCategory.Elevation)]
    [InlineData(RiskCategory.DestructiveGit)]
    [InlineData(RiskCategory.ProcessControl)]
    public void AutoStillAsksForHighImpact(RiskCategory category)
        => Assert.Equal(PolicyDecision.Ask, ApprovalPolicy.Evaluate(AssistantMode.Auto, category));

    [Fact]
    public void AskAllowsReadingWithoutSpam()
        => Assert.Equal(PolicyDecision.Allow, ApprovalPolicy.Evaluate(AssistantMode.Ask, RiskCategory.Read));

    [Theory]
    [InlineData(RiskCategory.WorkspaceWrite)]
    [InlineData(RiskCategory.CommandSafe)]
    [InlineData(RiskCategory.CommandHighImpact)]
    [InlineData(RiskCategory.SystemChange)]
    public void AskGatesEveryMutationAndCommand(RiskCategory category)
        => Assert.Equal(PolicyDecision.Ask, ApprovalPolicy.Evaluate(AssistantMode.Ask, category));

    [Fact]
    public void EveryCategoryHasAnExplanation()
    {
        foreach (RiskCategory category in Enum.GetValues<RiskCategory>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ApprovalPolicy.ExplainAsk(AssistantMode.Auto, category)));
            Assert.False(string.IsNullOrWhiteSpace(RiskCategoryText.Describe(category)));
        }
    }

    [Fact]
    public void StoppingABackgroundCommandThisTaskStartedIsRoutineInBothModes()
    {
        // Found by a real mixed-workflow run: stopping its own background dev server cost the
        // owner an approval card, classified as an unrecognized tool. The only thing these can
        // reach is a command the task already ran and the owner already saw.
        foreach (var tool in new[] { "TaskStop", "KillShell", "KillBash" })
        {
            var assessment = new RiskClassifier().Classify(
                ToolInvocation.FromJson(tool, JsonSerializer.SerializeToElement(new { shell_id = "bash_1" }), @"C:\w"));
            Assert.Equal(RiskCategory.CommandSafe, assessment.Category);
            Assert.Equal(PolicyDecision.Allow, ApprovalPolicy.Evaluate(AssistantMode.Auto, assessment.Category));
            // ASK still puts every command to the owner, including this one.
            Assert.Equal(PolicyDecision.Ask, ApprovalPolicy.Evaluate(AssistantMode.Ask, assessment.Category));
        }

        // Reading a background command's output stays a read.
        foreach (var tool in new[] { "TaskOutput", "BashOutput", "Monitor" })
            Assert.Equal(RiskCategory.Read, new RiskClassifier().Classify(
                ToolInvocation.FromJson(tool, JsonSerializer.SerializeToElement(new { }), @"C:\w")).Category);

        // Terminating something the task did not start is still process control.
        Assert.Equal(RiskCategory.ProcessControl, new RiskClassifier().Classify(
            ToolInvocation.FromJson("Bash", JsonSerializer.SerializeToElement(new { command = "taskkill /F /IM chrome.exe" }), @"C:\w")).Category);
    }
}
