using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class ApprovalRecord
{
    public string Id { get; set; } = "";
    public string RunId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string? Target { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Summary { get; set; }
    public int Capabilities { get; set; }
    public string RiskLevel { get; set; } = nameof(AgentRiskLevel.Low);
    public string Status { get; set; } = nameof(ApprovalStatus.Pending);
    public string? Scope { get; set; }
    public string? DecisionComment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class ApprovalRepository(IDbContextFactory<AppDbContext> dbFactory)
    : IApprovalRepository
{
    public async Task SaveAsync(AgentApproval approval, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.AgentApprovals.FindAsync([approval.Id], ct);
        if (record is null)
        {
            record = new ApprovalRecord { Id = approval.Id };
            db.AgentApprovals.Add(record);
        }

        record.RunId = approval.RunId;
        record.Operation = approval.Operation;
        record.Target = approval.Target;
        record.WorkingDirectory = approval.WorkingDirectory;
        record.Summary = approval.Summary;
        record.Capabilities = (int)approval.Capabilities;
        record.RiskLevel = approval.RiskLevel.ToString();
        record.Status = approval.Status.ToString();
        record.Scope = approval.Scope?.ToString();
        record.DecisionComment = approval.DecisionComment;
        record.CreatedAt = approval.CreatedAt;
        record.ResolvedAt = approval.ResolvedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task<AgentApproval?> GetAsync(
        string approvalId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.AgentApprovals.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == approvalId, ct);
        return record is null ? null : ToEntity(record);
    }

    public async Task<IReadOnlyList<AgentApproval>> ListPendingByRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = nameof(ApprovalStatus.Pending);
        var records = await db.AgentApprovals.AsNoTracking()
            .Where(x => x.RunId == runId && x.Status == pending)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        return records.Select(ToEntity).ToList();
    }

    private static AgentApproval ToEntity(ApprovalRecord record) => new()
    {
        Id = record.Id,
        RunId = record.RunId,
        Operation = record.Operation,
        Target = record.Target,
        WorkingDirectory = record.WorkingDirectory,
        Summary = record.Summary,
        Capabilities = (AgentCapability)record.Capabilities,
        RiskLevel = Enum.TryParse<AgentRiskLevel>(record.RiskLevel, true, out var risk)
            ? risk
            : AgentRiskLevel.High,
        Status = Enum.TryParse<ApprovalStatus>(record.Status, true, out var status)
            ? status
            : ApprovalStatus.Pending,
        Scope = Enum.TryParse<ApprovalScope>(record.Scope, true, out var scope)
            ? scope
            : null,
        DecisionComment = record.DecisionComment,
        CreatedAt = record.CreatedAt,
        ResolvedAt = record.ResolvedAt,
    };
}
