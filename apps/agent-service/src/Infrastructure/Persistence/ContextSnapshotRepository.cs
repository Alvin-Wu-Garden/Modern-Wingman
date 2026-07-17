using AgentService.Application.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class ContextSnapshotRecord
{
    public string Id { get; set; } = "";
    public string? RunId { get; set; }
    public string OriginalHash { get; set; } = "";
    public string CompressedHash { get; set; } = "";
    public int OriginalCharacters { get; set; }
    public int CompressedCharacters { get; set; }
    public string SourcesJson { get; set; } = "[]";
    public string CompressedContent { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ContextSnapshotRepository(IDbContextFactory<AppDbContext> factory)
    : IContextSnapshotRepository
{
    public async Task SaveAsync(ContextSnapshot snapshot, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ContextSnapshots.Add(new ContextSnapshotRecord
        {
            Id=snapshot.Id,RunId=snapshot.RunId,OriginalHash=snapshot.OriginalHash,
            CompressedHash=snapshot.CompressedHash,OriginalCharacters=snapshot.OriginalCharacters,
            CompressedCharacters=snapshot.CompressedCharacters,SourcesJson=snapshot.SourcesJson,
            CompressedContent=snapshot.CompressedContent,CreatedAt=snapshot.CreatedAt,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ContextSnapshot>> ListByRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.ContextSnapshots.AsNoTracking().Where(x=>x.RunId==runId).OrderBy(x=>x.CreatedAt).ToListAsync(ct))
            .Select(x=>new ContextSnapshot(x.Id,x.RunId,x.OriginalHash,x.CompressedHash,x.OriginalCharacters,x.CompressedCharacters,x.SourcesJson,x.CompressedContent,x.CreatedAt)).ToList();
    }
}
