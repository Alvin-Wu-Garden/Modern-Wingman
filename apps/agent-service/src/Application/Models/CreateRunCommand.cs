using AgentService.Domain.Models;

namespace AgentService.Application.Models;

/// <summary>啟動一個新 Run 所需的指令資料。</summary>
public sealed record CreateRunCommand(
    string SessionId,
    string UserMessage,
    /// <summary>null = 使用 AgentService:ActiveProfileId 設定的 profile。</summary>
    string? ProviderProfileId = null,
    /// <summary>工作區根目錄（絕對路徑）。</summary>
    string? WorkspacePath = null,
    string? ProjectId = null,
    AgentMode Mode = AgentMode.Plan,
    WorkspaceStrategy WorkspaceStrategy = WorkspaceStrategy.Direct,
    bool IncludeUncommittedChanges = true,
    string? ParentRunId = null,
    string? AgentRole = null
);
