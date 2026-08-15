namespace AgentService.Application.Models;

public enum IndexManifestStatus
{
    Fresh,
}

/// <summary>
/// 一個已成功發布的不可變版本。失敗或取消只記錄到 Project 狀態與 log，
/// 不會在這張版本表留下 manifest。
/// </summary>
public sealed record ProjectIndexManifest(
    string ProjectId,
    string Version,
    string RepositoryRoot,
    string IndexerVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IndexManifestStatus Status,
    int NodeCount = 0,
    int EdgeCount = 0,
    string? Error = null,
    string GraphSchemaVersion = "1.0",
    string? AnalysisSnapshotHash = null,
    string IndexMode = "full",
    bool? RequiresRetry = null);

/// <summary>active 版本建立時保存的檔案雜湊；不保存檔案內容。</summary>
public sealed record ProjectIndexedFile(string RelativePath, string ContentHash);

public sealed record ProjectIndexDiagnostics(
    ProjectIndexManifest? Current,
    ProjectIndexManifest? LatestAttempt,
    bool IsStale);
