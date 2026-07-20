namespace AgentService.Infrastructure.Workflow;

internal sealed record ExplorationResult(
    WorkflowRunRequest Request,
    string Context);

internal sealed record PlanResult(
    WorkflowRunRequest Request,
    string Context,
    string Plan);

internal sealed record ImpactResult(
    WorkflowRunRequest Request,
    string Context,
    string Plan);

internal sealed record CodeResult(
    WorkflowRunRequest Request,
    string Context,
    string Plan,
    string Output,
    int VerifyAttempt);

internal sealed record VerificationOutcome(
    WorkflowRunRequest Request,
    string Context,
    string Plan,
    string Output,
    int VerifyAttempt,
    bool ShouldRetry,
    string? Failure,
    string TerminalText);
