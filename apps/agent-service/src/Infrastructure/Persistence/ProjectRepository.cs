using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>EF Core 持久化用的專案資料列。</summary>
public sealed class ProjectRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string Languages { get; set; } = "";
    public string IndexStatus { get; set; } = nameof(ProjectIndexStatus.NotIndexed);
    public DateTimeOffset? IndexedAt { get; set; }
    public string? IndexError { get; set; }
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public string? IndexManifestVersion { get; set; }
    public int PendingFileCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>SQLite 專案持久化（WS3.1）。</summary>
public sealed class ProjectRepository(IDbContextFactory<AppDbContext> dbFactory) : IProjectRepository
{
    public async Task<IReadOnlyList<ProjectEntity>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var records = await db.Projects.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
        return records.Select(ToEntity).ToList();
    }

    public async Task<ProjectEntity?> GetAsync(string projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        return record is null ? null : ToEntity(record);
    }

    public async Task SaveAsync(ProjectEntity project, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.Projects.FindAsync([project.Id], ct);
        if (record is null)
        {
            record = new ProjectRecord { Id = project.Id, CreatedAt = project.CreatedAt };
            db.Projects.Add(record);
        }

        record.Name = project.Name;
        record.RootPath = project.RootPath;
        record.Languages = project.Languages;
        record.IndexStatus = project.IndexStatus.ToString();
        record.IndexedAt = project.IndexedAt;
        record.IndexError = project.IndexError;
        record.NodeCount = project.NodeCount;
        record.EdgeCount = project.EdgeCount;
        record.IndexManifestVersion = project.IndexManifestVersion;
        record.PendingFileCount = project.PendingFileCount;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Projects.Where(p => p.Id == projectId).ExecuteDeleteAsync(ct);
    }

    private static ProjectEntity ToEntity(ProjectRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        RootPath = r.RootPath,
        Languages = r.Languages,
        IndexStatus = Enum.TryParse<ProjectIndexStatus>(r.IndexStatus, out var s)
            ? s : ProjectIndexStatus.NotIndexed,
        IndexedAt = r.IndexedAt,
        IndexError = r.IndexError,
        NodeCount = r.NodeCount,
        EdgeCount = r.EdgeCount,
        IndexManifestVersion = r.IndexManifestVersion,
        PendingFileCount = r.PendingFileCount,
        CreatedAt = r.CreatedAt,
    };
}
