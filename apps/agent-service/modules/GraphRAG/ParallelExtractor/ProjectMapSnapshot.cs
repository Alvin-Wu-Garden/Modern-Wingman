namespace AgentService.Modules.GraphRAG.ParallelExtractor;

using Microsoft.CodeAnalysis;

/// <summary>保存 Roslyn 專案解析時共用的專案對照表，避免重複建立索引。</summary>
sealed record ProjectMapSnapshot(
    IReadOnlyDictionary<ProjectId, string> ProjectIds,
    IReadOnlyDictionary<string, string> AssemblyToProjectId,
    IReadOnlyDictionary<string, List<string>> PathToProjectIds,
    IReadOnlyDictionary<string, Project> ProjectById);
