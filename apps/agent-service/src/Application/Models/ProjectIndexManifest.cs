namespace AgentService.Application.Models;

public enum IndexManifestStatus
{
    Indexing,
    Fresh,
    Partial,
    Failed,
    Stale,
}

public sealed record IndexedFileManifest(
    string RelativePath,
    string Language,
    long Length,
    string ContentHash,
    string Status = "Indexed",
    string? Reason = null,
    DateTimeOffset? LastWriteAt = null,
    string? DeclarationHash = null);

/// <summary>
/// 一次索引嘗試的不可變描述。失敗嘗試會保留供診斷，
/// 但永遠不會覆蓋上一個完整的圖譜版本。
/// </summary>
public sealed record ProjectIndexManifest(
    string ProjectId,
    string Version,
    string RepositoryRoot,
    string? HeadCommit,
    string WorkingTreeFingerprint,
    IReadOnlyList<string> UntrackedFiles,
    IReadOnlyList<IndexedFileManifest> Files,
    IReadOnlyList<string> PendingFiles,
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
    bool? RequiresRetry = null)
{
    public int PendingFileCount => PendingFiles.Count;
    public int FailedFileCount => Files.Count(file =>
        !string.Equals(file.Status, "Indexed", StringComparison.OrdinalIgnoreCase));
}

public sealed record ProjectIndexDiagnostics(
    ProjectIndexManifest? Current,
    ProjectIndexManifest? LatestAttempt,
    IReadOnlyList<string> PendingFiles,
    bool IsStale);
