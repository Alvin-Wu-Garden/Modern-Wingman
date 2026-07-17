namespace AgentService.Domain.Models;

[Flags]
public enum AgentCapability
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Execute = 1 << 2,
    Network = 1 << 3,
    ExternalSideEffect = 1 << 4,
    Destructive = 1 << 5,
}

public enum AgentRiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

public enum PolicyDecisionKind
{
    Allow,
    RequireApproval,
    Deny,
}

public sealed record AgentPermissionRequest(
    string Operation,
    AgentCapability Capabilities,
    AgentRiskLevel RiskLevel,
    string? Target = null,
    string? WorkingDirectory = null,
    string? Summary = null);

public sealed record AgentPolicyContext(
    AgentMode Mode,
    string? WorkspacePath,
    bool IsProtectedRef = false);

public sealed record AgentPolicyDecision(
    PolicyDecisionKind Kind,
    string Reason)
{
    public static AgentPolicyDecision Allow(string reason) =>
        new(PolicyDecisionKind.Allow, reason);

    public static AgentPolicyDecision RequireApproval(string reason) =>
        new(PolicyDecisionKind.RequireApproval, reason);

    public static AgentPolicyDecision Deny(string reason) =>
        new(PolicyDecisionKind.Deny, reason);
}
