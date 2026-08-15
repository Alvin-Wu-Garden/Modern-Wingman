using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class ProjectIndexManifestStore(IDbContextFactory<AppDbContext> dbFactory)
    : IProjectIndexManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public async Task<IReadOnlyList<ProjectIndexManifest>> ListSuccessfulAsync(
        string projectId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT ManifestJson
            FROM project_index_manifests
            WHERE ProjectId = $projectId AND Status = 'Fresh'
            ORDER BY CompletedAt DESC
            LIMIT 2
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$projectId";
        parameter.Value = projectId;
        command.Parameters.Add(parameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        var result = new List<ProjectIndexManifest>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var manifest = JsonSerializer.Deserialize<ProjectIndexManifest>(reader.GetString(0), JsonOptions);
            if (manifest is not null)
                result.Add(manifest);
        }
        return result;
    }

    public async Task ActivateAsync(
        string projectId,
        string version,
        CancellationToken ct = default)
    {
        var manifest = await GetByVersionAsync(projectId, version, ct);
        if (manifest is null || manifest.Status != IndexManifestStatus.Fresh)
            throw new KeyNotFoundException($"找不到可啟用的成功圖譜版本：{projectId}/{version}");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE project_index_manifests SET IsCurrent = 0 WHERE ProjectId = {projectId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE project_index_manifests SET IsCurrent = 1 WHERE ProjectId = {projectId} AND Version = {version}", ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteVersionAsync(
        string projectId,
        string version,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM project_index_files WHERE ProjectId = {projectId} AND GraphVersion = {version}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM project_index_manifests WHERE ProjectId = {projectId} AND Version = {version} AND IsCurrent = 0", ct);
        await tx.CommitAsync(ct);
    }

    public async Task PruneSuccessfulAsync(
        string projectId,
        string? previousVersion,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM project_index_files
            WHERE ProjectId = {projectId}
              AND GraphVersion NOT IN (
                  SELECT Version FROM project_index_manifests
                  WHERE ProjectId = {projectId}
                    AND (IsCurrent = 1 OR Version = {previousVersion}))
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM project_index_manifests
            WHERE ProjectId = {projectId}
              AND IsCurrent = 0
              AND ({previousVersion} IS NULL OR Version <> {previousVersion})
            """, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SaveFileSnapshotAsync(
        string projectId,
        string version,
        IReadOnlyList<ProjectIndexedFile> files,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var delete = connection.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = "DELETE FROM project_index_files WHERE ProjectId = $projectId AND GraphVersion = $version";
        var deleteProject = delete.CreateParameter();
        deleteProject.ParameterName = "$projectId";
        deleteProject.Value = projectId;
        delete.Parameters.Add(deleteProject);
        var deleteVersion = delete.CreateParameter();
        deleteVersion.ParameterName = "$version";
        deleteVersion.Value = version;
        delete.Parameters.Add(deleteVersion);
        await delete.ExecuteNonQueryAsync(ct);

        // 重用同一個 prepared command，避免每個檔案都重新建立 EF SQL 與參數物件。
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO project_index_files (ProjectId, GraphVersion, RelativePath, ContentHash)
            VALUES ($projectId, $version, $relativePath, $contentHash)
            """;
        var insertProject = insert.CreateParameter();
        insertProject.ParameterName = "$projectId";
        insertProject.Value = projectId;
        insert.Parameters.Add(insertProject);
        var insertVersion = insert.CreateParameter();
        insertVersion.ParameterName = "$version";
        insertVersion.Value = version;
        insert.Parameters.Add(insertVersion);
        var relativePath = insert.CreateParameter();
        relativePath.ParameterName = "$relativePath";
        insert.Parameters.Add(relativePath);
        var contentHash = insert.CreateParameter();
        contentHash.ParameterName = "$contentHash";
        insert.Parameters.Add(contentHash);
        await insert.PrepareAsync(ct);
        foreach (var file in files)
        {
            relativePath.Value = file.RelativePath;
            contentHash.Value = file.ContentHash;
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ProjectIndexedFile>> GetFileSnapshotAsync(
        string projectId,
        string version,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT RelativePath, ContentHash
            FROM project_index_files
            WHERE ProjectId = $projectId AND GraphVersion = $version
            ORDER BY RelativePath
            """;
        foreach (var pair in new[] { ("$projectId", projectId), ("$version", version) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = pair.Item1;
            parameter.Value = pair.Item2;
            command.Parameters.Add(parameter);
        }
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        var result = new List<ProjectIndexedFile>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ProjectIndexedFile(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    /// <summary>
    /// 恢復發布前的本機 active manifest；若先前沒有版本，則清除目前指標。
    /// 此補償只操作 Modern Wingman 自身 SQLite，不接觸外部資料庫。
    /// </summary>
    public async Task RestoreCurrentAsync(
        string projectId,
        string? previousVersion,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (string.IsNullOrWhiteSpace(previousVersion))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE project_index_manifests SET IsCurrent = 0 WHERE ProjectId = {projectId}", ct);
            return;
        }

        var previous = await GetByVersionAsync(projectId, previousVersion, ct);
        if (previous is null)
        {
            throw new InvalidOperationException(
                $"找不到需要恢復的索引 manifest：{projectId}/{previousVersion}");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE project_index_manifests SET IsCurrent = 0 WHERE ProjectId = {projectId}", ct);
        var json = JsonSerializer.Serialize(previous, JsonOptions);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE project_index_manifests
            SET IsCurrent = 1, Status = {previous.Status.ToString()}, ManifestJson = {json},
                CompletedAt = {previous.CompletedAt?.ToString("O")}
            WHERE ProjectId = {projectId} AND Version = {previous.Version}
            """, ct);
    }

    public async Task DeleteProjectAsync(string projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM project_index_files WHERE ProjectId = {projectId}", ct);
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
