namespace AgentService.Domain.Models;

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
    Expired,
}

public enum ApprovalScope
{
    Once,
    Run,
    Workspace,
}

public sealed class AgentApproval
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string RunId { get; init; }
    public required string Operation { get; init; }
    public string? Target { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Summary { get; init; }
    public AgentCapability Capabilities { get; init; }
    public AgentRiskLevel RiskLevel { get; init; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public ApprovalScope? Scope { get; set; }
    public string? DecisionComment { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}
