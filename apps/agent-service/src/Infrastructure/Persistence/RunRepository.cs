using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>EF Core 持久化用的 Run 資料列（與 Domain RunEntity 分離，避免污染領域模型）。</summary>
public sealed class RunRecord
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string UserMessage { get; set; } = "";
    public string? ProviderProfileId { get; set; }
    public string? ResolvedModelId { get; set; }
    public string? WorkspacePath { get; set; }
    public string? ProjectId { get; set; }
    public string? ParentRunId { get; set; }
    public string? AgentRole { get; set; }
    public string Mode { get; set; } = nameof(AgentMode.Plan);
    public string WorkspaceStrategy { get; set; } = nameof(Domain.Models.WorkspaceStrategy.Direct);
    public string? CheckpointId { get; set; }
    public string TraceId { get; set; } = "";
    public string? ExecutionWorkspacePath { get; set; } public string? Branch { get; set; } public string? BaseRevision { get; set; }
    public bool IncludeUncommittedChanges { get; set; } = true;
    public string Status { get; set; } = nameof(RunStatus.Created);
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>
/// SQLite Run 持久化實作（WS2）。
/// 寫入採 upsert：StartRun 時建立、狀態轉換時更新。
/// </summary>
public sealed class RunRepository(IDbContextFactory<AppDbContext> dbFactory) : IRunRepository
{
    public async Task SaveAsync(RunEntity run, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var record = await db.Runs.FindAsync([run.Id], ct);
        if (record is null)
        {
            record = new RunRecord { Id = run.Id };
            db.Runs.Add(record);
        }

        record.SessionId = run.SessionId;
        record.UserMessage = run.UserMessage;
        record.ProviderProfileId = run.ProviderProfileId;
        record.ResolvedModelId = run.ResolvedModelId;
        record.WorkspacePath = run.WorkspacePath;
        record.ProjectId = run.ProjectId;
        record.ParentRunId = run.ParentRunId;
        record.AgentRole = run.AgentRole;
        record.Mode = run.Mode.ToString();
        record.WorkspaceStrategy = run.WorkspaceStrategy.ToString();
        record.CheckpointId = run.CheckpointId;
        record.TraceId = run.TraceId;
        record.ExecutionWorkspacePath=run.ExecutionWorkspacePath;record.Branch=run.Branch;record.BaseRevision=run.BaseRevision;
        record.IncludeUncommittedChanges = run.IncludeUncommittedChanges;
        record.Status = run.Status.ToString();
        record.Error = run.Error;
        record.CreatedAt = run.CreatedAt;
        record.StartedAt = run.StartedAt;
        record.EndedAt = run.EndedAt;

        await db.SaveChangesAsync(ct);
    }

    public async Task<RunEntity?> GetAsync(string runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        return record is null ? null : ToEntity(record);
    }

    public async Task<IReadOnlyList<RunEntity>> ListBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var records = await db.Runs.AsNoTracking()
            .Where(r => r.SessionId == sessionId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        return records.Select(ToEntity).ToList();
    }
    public async Task<IReadOnlyList<RunEntity>> ListByStatusesAsync(IReadOnlyCollection<RunStatus> statuses,CancellationToken ct=default)
    {
        var names=statuses.Select(x=>x.ToString()).ToList();await using var db=await dbFactory.CreateDbContextAsync(ct);return (await db.Runs.AsNoTracking().Where(x=>names.Contains(x.Status)).OrderBy(x=>x.CreatedAt).ToListAsync(ct)).Select(ToEntity).ToList();
    }
    private static RunEntity ToEntity(RunRecord r)
    {
        var entity = new RunEntity(r.Id)
        {
            SessionId = r.SessionId,
            UserMessage = r.UserMessage,
            ProviderProfileId = r.ProviderProfileId,
            ResolvedModelId = r.ResolvedModelId,
            WorkspacePath = r.WorkspacePath,
            ProjectId = r.ProjectId,
            ParentRunId = r.ParentRunId,
            AgentRole = r.AgentRole,
            Mode = Enum.TryParse<AgentMode>(r.Mode, true, out var mode)
                ? mode
                : AgentMode.Plan,
            WorkspaceStrategy = Enum.TryParse<Domain.Models.WorkspaceStrategy>(
                r.WorkspaceStrategy, true, out var strategy)
                ? strategy
                : Domain.Models.WorkspaceStrategy.Direct,
        };
        entity.Status = Enum.TryParse<RunStatus>(r.Status, out var s) ? s : RunStatus.Failed;
        entity.Error = r.Error;
        entity.CheckpointId = r.CheckpointId;
        entity.TraceId = string.IsNullOrWhiteSpace(r.TraceId) ? r.Id : r.TraceId;
        entity.ExecutionWorkspacePath=r.ExecutionWorkspacePath;entity.Branch=r.Branch;entity.BaseRevision=r.BaseRevision;
        entity.IncludeUncommittedChanges = r.IncludeUncommittedChanges;
        entity.StartedAt = r.StartedAt;
        entity.EndedAt = r.EndedAt;
        return entity;
    }
}
