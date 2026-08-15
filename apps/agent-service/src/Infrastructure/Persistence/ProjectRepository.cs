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
    public string? SelectedSolutionPath { get; set; }
    public bool GraphStorageMigrated { get; set; }
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
        record.SelectedSolutionPath = project.SelectedSolutionPath;
        record.GraphStorageMigrated = project.GraphStorageMigrated;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // 不依賴歷史資料庫是否正確開啟 foreign_keys；明確清除全部專案附屬資料。
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM Messages WHERE ConversationId IN (SELECT Id FROM Conversations WHERE ProjectId = {projectId})", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM Conversations WHERE ProjectId = {projectId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM jira_analysis_runs WHERE WingmanProjectId = {projectId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM project_index_files WHERE ProjectId = {projectId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM project_index_manifests WHERE ProjectId = {projectId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM project_database_configurations WHERE ProjectId = {projectId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM project_vcs_bindings WHERE ProjectId = {projectId}", ct);
        await db.Projects.Where(p => p.Id == projectId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static ProjectEntity ToEntity(ProjectRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        RootPath = r.RootPath,
        Languages = r.Languages,
        IndexStatus = Enum.TryParse<ProjectIndexStatus>(r.IndexStatus, out var s)
            ? s
            : r.IndexStatus.Equals("PendingChanges", StringComparison.Ordinal)
                ? ProjectIndexStatus.Stale
                : ProjectIndexStatus.NotIndexed,
        IndexedAt = r.IndexedAt,
        IndexError = r.IndexError,
        NodeCount = r.NodeCount,
        EdgeCount = r.EdgeCount,
        IndexManifestVersion = r.IndexManifestVersion,
        SelectedSolutionPath = r.SelectedSolutionPath,
        GraphStorageMigrated = r.GraphStorageMigrated,
        CreatedAt = r.CreatedAt,
    };
}
