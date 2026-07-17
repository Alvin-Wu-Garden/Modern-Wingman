namespace AgentService.Domain.Models;

public enum RunStatus
{
    Created,
    Running,
    WaitingApproval,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

public enum AgentMode
{
    Ask,
    Plan,
    Auto,
    FullAuto,
}

public enum WorkspaceStrategy
{
    Direct,
    GitWorktree,
    SvnShadowGit,
    Snapshot,
}

/// <summary>
/// 一次 Agent 執行的執行期狀態（WS2 起持久化至 SQLite，見 RunRepository）。
/// </summary>
public sealed class RunEntity
{
    public RunEntity()
    {
    }

    /// <summary>從持久層還原時使用：指定既有 ID。</summary>
    public RunEntity(string id)
    {
        Id = id;
    }

    /// <summary>唯一 ID，格式為 32 hex chars（無連字號）。</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public required string SessionId { get; init; }
    public required string UserMessage { get; init; }

    /// <summary>null = 使用當前作用中的 active profile。</summary>
    public string? ProviderProfileId { get; set; }
    public string? ResolvedModelId { get; set; }

    /// <summary>工作區目錄路徑（絕對路徑），用於注入系統提示。</summary>
    public string? WorkspacePath { get; init; }

    public string? ProjectId { get; init; }
    public string? ParentRunId { get; init; }
    public string? AgentRole { get; init; }

    public AgentMode Mode { get; set; } = AgentMode.Plan;

    public WorkspaceStrategy WorkspaceStrategy { get; set; } = WorkspaceStrategy.Direct;

    public string? CheckpointId { get; set; }
    public string TraceId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ExecutionWorkspacePath { get; set; }
    public string? Branch { get; set; }
    public string? BaseRevision { get; set; }
    public bool IncludeUncommittedChanges { get; set; } = true;

    public RunStatus Status { get; set; } = RunStatus.Created;

    /// <summary>失敗時的錯誤訊息。</summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}
