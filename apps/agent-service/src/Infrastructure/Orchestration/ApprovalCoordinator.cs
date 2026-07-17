using System.Collections.Concurrent;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Orchestration;

public sealed class ApprovalCoordinator(
    IApprovalRepository approvalRepository,
    IRunRepository runRepository,
    IRunEventBus eventBus,
    IAuditEventRecorder audit,
    ILogger<ApprovalCoordinator> logger) : IApprovalCoordinator
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalOutcome>> _waiters = [];

    public async Task<ApprovalOutcome> RequestAsync(
        string runId,
        AgentPermissionRequest request,
        CancellationToken ct = default)
    {
        var approval = new AgentApproval
        {
            RunId = runId,
            Operation = request.Operation,
            Target = request.Target,
            WorkingDirectory = request.WorkingDirectory,
            Summary = request.Summary,
            Capabilities = request.Capabilities,
            RiskLevel = request.RiskLevel,
        };
        var waiter = new TaskCompletionSource<ApprovalOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_waiters.TryAdd(approval.Id, waiter))
            throw new InvalidOperationException("Approval identifier collision.");

        await approvalRepository.SaveAsync(approval, ct);
        var run = await runRepository.GetAsync(runId, ct);
        if (run is not null)
        {
            run.Status = RunStatus.WaitingApproval;
            await runRepository.SaveAsync(run, ct);
        }
        await audit.RecordAsync(new AuditEventWrite(
            EventType: "approval_requested",
            TargetType: "agent_approval",
            TargetId: approval.Id,
            Action: approval.Operation,
            Result: "pending",
            ActorType: "agent",
            TraceId: run?.TraceId,
            DetailsJson: JsonSerializer.Serialize(new
            {
                approval.RunId,
                approval.Target,
                approval.WorkingDirectory,
                approval.Summary,
                capabilities = approval.Capabilities.ToString(),
                risk = approval.RiskLevel.ToString(),
            })), ct);

        await eventBus.PublishAsync(RunStreamEvent.ApprovalRequested(runId, approval), ct);
        logger.LogInformation(
            "Run {RunId} waiting for approval {ApprovalId}: {Operation}",
            runId,
            approval.Id,
            approval.Operation);

        try
        {
            return await waiter.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            approval.Status = ApprovalStatus.Cancelled;
            approval.ResolvedAt = DateTimeOffset.UtcNow;
            await approvalRepository.SaveAsync(approval, CancellationToken.None);
            await audit.RecordAsync(new AuditEventWrite(
                "approval_cancelled",
                "agent_approval",
                approval.Id,
                approval.Operation,
                "cancelled",
                "system",
                TraceId: run?.TraceId), CancellationToken.None);
            throw;
        }
        finally
        {
            _waiters.TryRemove(approval.Id, out _);
        }
    }

    public async Task<bool> ResolveAsync(
        string approvalId,
        ResolveApprovalCommand command,
        CancellationToken ct = default)
    {
        var approval = await approvalRepository.GetAsync(approvalId, ct);
        if (approval is null || approval.Status != ApprovalStatus.Pending)
            return false;

        approval.Status = command.Approved
            ? ApprovalStatus.Approved
            : ApprovalStatus.Rejected;
        approval.Scope = command.Scope;
        approval.DecisionComment = command.Comment;
        approval.ResolvedAt = DateTimeOffset.UtcNow;
        await approvalRepository.SaveAsync(approval, ct);

        var outcome = new ApprovalOutcome(command.Approved, command.Scope, command.Comment);
        if (_waiters.TryGetValue(approvalId, out var waiter))
            waiter.TrySetResult(outcome);

        var run = await runRepository.GetAsync(approval.RunId, ct);
        if (run is not null && run.Status == RunStatus.WaitingApproval)
        {
            run.Status = RunStatus.Running;
            await runRepository.SaveAsync(run, ct);
        }
        await audit.RecordAsync(new AuditEventWrite(
            EventType: "approval_resolved",
            TargetType: "agent_approval",
            TargetId: approval.Id,
            Action: approval.Operation,
            Result: command.Approved ? "approved" : "rejected",
            ActorType: "user",
            TraceId: run?.TraceId,
            DetailsJson: JsonSerializer.Serialize(new
            {
                scope = command.Scope.ToString(),
                command.Comment,
                approval.RunId,
            })), ct);

        await eventBus.PublishAsync(
            RunStreamEvent.ApprovalResolved(approval.RunId, approval),
            ct);
        return true;
    }
}
