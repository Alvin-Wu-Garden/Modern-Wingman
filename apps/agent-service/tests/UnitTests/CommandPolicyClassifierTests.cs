using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;

namespace AgentService.UnitTests;

public sealed class CommandPolicyClassifierTests
{
    [Theory]
    [InlineData("git", "status")]
    [InlineData("git", "diff")]
    [InlineData("svn", "info")]
    [InlineData("rg", "TODO")]
    public void ReadOnlyCommands_AreLowRisk(string executable, string argument)
    {
        var (capabilities, risk) = CommandPolicyClassifier.Classify(executable, [argument]);

        Assert.Equal(AgentRiskLevel.Low, risk);
        Assert.True(capabilities.HasFlag(AgentCapability.Read));
        Assert.False(capabilities.HasFlag(AgentCapability.Write));
    }

    [Fact]
    public void GitPush_IsExternalHighRisk()
    {
        var (capabilities, risk) = CommandPolicyClassifier.Classify(
            "git",
            ["push", "origin", "main"]);

        Assert.Equal(AgentRiskLevel.High, risk);
        Assert.True(capabilities.HasFlag(AgentCapability.ExternalSideEffect));
    }

    [Fact]
    public void RecursiveDelete_IsDestructiveHighRisk()
    {
        var (capabilities, risk) = CommandPolicyClassifier.ClassifyRaw(
            "Remove-Item C:\\work\\src -Recurse -Force");

        Assert.Equal(AgentRiskLevel.High, risk);
        Assert.True(capabilities.HasFlag(AgentCapability.Destructive));
    }

    [Theory]
    [InlineData("git push --force origin main")]
    [InlineData("git push -f origin main")]
    [InlineData("Set-ExecutionPolicy Unrestricted")]
    [InlineData("reg add HKLM\\Software\\Example")]
    public void ForbiddenSystemOrForceOperations_AreCritical(string command)
    {
        var (capabilities, risk) = CommandPolicyClassifier.ClassifyRaw(command);
        Assert.Equal(AgentRiskLevel.Critical, risk);
        Assert.True(capabilities.HasFlag(AgentCapability.Destructive));
    }
}
