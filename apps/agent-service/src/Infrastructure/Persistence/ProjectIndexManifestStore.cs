using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class ProjectIndexManifestStore(IDbContextFactory<AppDbContext> dbFactory)
    : IProjectIndexManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task SaveAttemptAsync(ProjectIndexManifest manifest, CancellationToken ct = default) =>
        SaveAsync(manifest, promote: false, ct);

    public Task PromoteAsync(ProjectIndexManifest manifest, CancellationToken ct = default) =>
        SaveAsync(manifest, promote: true, ct);

    public Task<ProjectIndexManifest?> GetCurrentAsync(string projectId, CancellationToken ct = default) =>
        GetAsync(projectId, currentOnly: true, ct);

    public Task<ProjectIndexManifest?> GetLatestAttemptAsync(string projectId, CancellationToken ct = default) =>
        GetAsync(projectId, currentOnly: false, ct);

    public async Task<ProjectIndexManifest?> GetByVersionAsync(
        string projectId, string version, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT ManifestJson FROM project_index_manifests WHERE ProjectId = $projectId AND Version = $version LIMIT 1";
        var projectParameter = command.CreateParameter();
        projectParameter.ParameterName = "$projectId";
        projectParameter.Value = projectId;
        command.Parameters.Add(projectParameter);
        var versionParameter = command.CreateParameter();
        versionParameter.ParameterName = "$version";
        versionParameter.Value = version;
        command.Parameters.Add(versionParameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ProjectIndexManifest>(json, JsonOptions);
    }

    public async Task DeleteProjectAsync(string projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM project_index_manifests WHERE ProjectId = {projectId}", ct);
    }

    private async Task SaveAsync(ProjectIndexManifest manifest, bool promote, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (promote)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE project_index_manifests SET IsCurrent = 0 WHERE ProjectId = {manifest.ProjectId}", ct);
        }

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var completedAt = manifest.CompletedAt?.ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO project_index_manifests
                (Version, ProjectId, Status, ManifestJson, StartedAt, CompletedAt, IsCurrent)
            VALUES
                ({manifest.Version}, {manifest.ProjectId}, {manifest.Status.ToString()}, {json},
                 {manifest.StartedAt.ToString("O")}, {completedAt}, {promote})
            ON CONFLICT(Version) DO UPDATE SET
                Status = CASE
                    WHEN project_index_manifests.IsCurrent = 1 AND excluded.IsCurrent = 0
                    THEN project_index_manifests.Status ELSE excluded.Status END,
                ManifestJson = CASE
                    WHEN project_index_manifests.IsCurrent = 1 AND excluded.IsCurrent = 0
                    THEN project_index_manifests.ManifestJson ELSE excluded.ManifestJson END,
                CompletedAt = CASE
                    WHEN project_index_manifests.IsCurrent = 1 AND excluded.IsCurrent = 0
                    THEN project_index_manifests.CompletedAt ELSE excluded.CompletedAt END,
                IsCurrent = CASE
                    WHEN excluded.IsCurrent = 1 THEN 1 ELSE project_index_manifests.IsCurrent END
            """, ct);
        await tx.CommitAsync(ct);
    }

    private async Task<ProjectIndexManifest?> GetAsync(
        string projectId, bool currentOnly, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = currentOnly
            ? "SELECT ManifestJson FROM project_index_manifests WHERE ProjectId = $projectId AND IsCurrent = 1 ORDER BY CompletedAt DESC LIMIT 1"
            : "SELECT ManifestJson FROM project_index_manifests WHERE ProjectId = $projectId ORDER BY StartedAt DESC LIMIT 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$projectId";
        parameter.Value = projectId;
        command.Parameters.Add(parameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ProjectIndexManifest>(json, JsonOptions);
    }
}
