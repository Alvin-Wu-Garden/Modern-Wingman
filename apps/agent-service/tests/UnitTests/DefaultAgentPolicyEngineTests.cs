using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Orchestration;

namespace AgentService.UnitTests;

public sealed class DefaultAgentPolicyEngineTests
{
    private readonly DefaultAgentPolicyEngine _policy = new();

    [Theory]
    [InlineData(AgentMode.Ask)]
    [InlineData(AgentMode.Plan)]
    public void ReadOnlyModes_AllowRead_AndDenyWrite(AgentMode mode)
    {
        var context = new AgentPolicyContext(mode, @"C:\work\project");

        var read = _policy.Evaluate(
            context,
            new AgentPermissionRequest("read", AgentCapability.Read, AgentRiskLevel.Low));
        var write = _policy.Evaluate(
            context,
            new AgentPermissionRequest("write", AgentCapability.Write, AgentRiskLevel.Medium));

        Assert.Equal(PolicyDecisionKind.Allow, read.Kind);
        Assert.Equal(PolicyDecisionKind.Deny, write.Kind);
    }

    [Fact]
    public void Auto_RequiresApprovalForExternalSideEffect()
    {
        var decision = _policy.Evaluate(
            new AgentPolicyContext(AgentMode.Auto, @"C:\work\project"),
            new AgentPermissionRequest(
                "git_push",
                AgentCapability.Execute | AgentCapability.ExternalSideEffect,
                AgentRiskLevel.High));

        Assert.Equal(PolicyDecisionKind.RequireApproval, decision.Kind);
    }

    [Fact]
    public void FullAuto_AllowsNormalWrite_ButProtectedRefRequiresApproval()
    {
        var request = new AgentPermissionRequest(
            "write",
            AgentCapability.Write,
            AgentRiskLevel.Medium,
            @"C:\work\project\src\a.cs");

        var normal = _policy.Evaluate(
            new AgentPolicyContext(AgentMode.FullAuto, @"C:\work\project"),
            request);
        var protectedRef = _policy.Evaluate(
            new AgentPolicyContext(AgentMode.FullAuto, @"C:\work\project", true),
            request);

        Assert.Equal(PolicyDecisionKind.Allow, normal.Kind);
        Assert.Equal(PolicyDecisionKind.RequireApproval, protectedRef.Kind);
    }

    [Fact]
    public void AnyMode_DeniesWriteOutsideWorkspace()
    {
        var decision = _policy.Evaluate(
            new AgentPolicyContext(AgentMode.FullAuto, @"C:\work\project"),
            new AgentPermissionRequest(
                "write",
                AgentCapability.Write,
                AgentRiskLevel.Medium,
                @"C:\other\secret.txt"));

        Assert.Equal(PolicyDecisionKind.Deny, decision.Kind);
    }

    [Fact]
    public void CriticalRisk_IsAlwaysDenied()
    {
        var decision = _policy.Evaluate(
            new AgentPolicyContext(AgentMode.FullAuto, @"C:\work\project"),
            new AgentPermissionRequest(
                "critical",
                AgentCapability.Destructive,
                AgentRiskLevel.Critical));

        Assert.Equal(PolicyDecisionKind.Deny, decision.Kind);
    }
}

public sealed class AgentPolicyProfileTests
{
    [Fact]
    public void AdministratorProfile_CannotBeBypassedByFullAuto()
    {
        var profile = new FixedProfileProvider(new AgentPolicyProfile(
            new HashSet<AgentMode> { AgentMode.Ask, AgentMode.Plan, AgentMode.Auto, AgentMode.FullAuto },
            AgentCapability.Network,
            AgentRiskLevel.High));
        var policy = new DefaultAgentPolicyEngine(profile);
        var decision = policy.Evaluate(
            new AgentPolicyContext(AgentMode.FullAuto, Path.GetTempPath()),
            new AgentPermissionRequest("network", AgentCapability.Network, AgentRiskLevel.Low));

        Assert.Equal(PolicyDecisionKind.Deny, decision.Kind);
        Assert.Contains("administrator", decision.Reason);
    }

    private sealed class FixedProfileProvider(AgentPolicyProfile profile) : IAgentPolicyProfileProvider
    {
        public AgentPolicyProfile Current => profile;
    }
}
