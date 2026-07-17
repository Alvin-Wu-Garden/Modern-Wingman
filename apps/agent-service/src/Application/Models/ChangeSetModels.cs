namespace AgentService.Application.Models;

public enum ChangedFileKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
}

public sealed record ChangedFile(
    string RelativePath,
    ChangedFileKind Kind,
    string? BaselineHash,
    string? CurrentHash,
    bool Binary,
    string? UnifiedDiff,
    string? OriginalPath = null,
    IReadOnlyList<DiffHunk>? Hunks = null);

public sealed record DiffHunk(
    int Index,
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<string> Lines);

public sealed record ChangeSet(
    string CheckpointId,
    string RunId,
    string WorkspacePath,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChangedFile> Files);

public sealed record RestoreCheckpointResult(
    bool Success,
    IReadOnlyList<string> RestoredFiles,
    IReadOnlyList<string> Conflicts);
