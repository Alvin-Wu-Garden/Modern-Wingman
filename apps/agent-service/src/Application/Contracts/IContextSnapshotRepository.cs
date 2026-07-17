namespace AgentService.Application.Contracts;

public sealed record ContextSnapshot(
    string Id,
    string? RunId,
    string OriginalHash,
    string CompressedHash,
    int OriginalCharacters,
    int CompressedCharacters,
    string SourcesJson,
    string CompressedContent,
    DateTimeOffset CreatedAt);

public interface IContextSnapshotRepository
{
    Task SaveAsync(ContextSnapshot snapshot, CancellationToken ct = default);
    Task<IReadOnlyList<ContextSnapshot>> ListByRunAsync(string runId, CancellationToken ct = default);
}
