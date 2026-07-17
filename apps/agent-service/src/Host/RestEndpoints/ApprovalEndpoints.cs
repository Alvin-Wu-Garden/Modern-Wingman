using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Host.RestEndpoints;

public static class ApprovalEndpoints
{
    public sealed record ApprovalDecisionRequest(
        bool Approved,
        string? Scope,
        string? Comment);

    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/approvals");
        group.MapGet("/runs/{runId}", ListPending);
        group.MapPost("/{approvalId}/decision", Resolve);
        return app;
    }

    private static async Task<IResult> ListPending(
        string runId,
        IApprovalRepository repository,
        CancellationToken ct)
    {
        var approvals = await repository.ListPendingByRunAsync(runId, ct);
        return Results.Ok(approvals.Select(ToDto));
    }

    private static async Task<IResult> Resolve(
        string approvalId,
        ApprovalDecisionRequest request,
        IApprovalCoordinator coordinator,
        CancellationToken ct)
    {
        var scope = request.Scope?.Trim().ToLowerInvariant() switch
        {
            "run" => ApprovalScope.Run,
            "workspace" => ApprovalScope.Workspace,
            _ => ApprovalScope.Once,
        };
        var resolved = await coordinator.ResolveAsync(
            approvalId,
            new ResolveApprovalCommand(request.Approved, scope, request.Comment),
            ct);
        return resolved ? Results.NoContent() : Results.NotFound();
    }

    private static object ToDto(AgentApproval approval) => new
    {
        approval.Id,
        approval.RunId,
        approval.Operation,
        approval.Target,
        approval.WorkingDirectory,
        approval.Summary,
        capabilities = approval.Capabilities.ToString(),
        riskLevel = approval.RiskLevel.ToString().ToLowerInvariant(),
        status = approval.Status.ToString().ToLowerInvariant(),
        approval.CreatedAt,
    };
}
