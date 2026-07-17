using AgentService.Domain.Models;

namespace AgentService.Application.Models;

public sealed record ApprovalOutcome(
    bool Approved,
    ApprovalScope? Scope,
    string? Comment);

public sealed record ResolveApprovalCommand(
    bool Approved,
    ApprovalScope Scope = ApprovalScope.Once,
    string? Comment = null);
