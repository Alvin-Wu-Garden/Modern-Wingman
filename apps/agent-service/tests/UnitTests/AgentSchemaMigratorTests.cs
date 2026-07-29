using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentService.UnitTests;

/// <summary>確認直接清理後的輕量 schema 可重複建立，且不會復活舊功能資料表。</summary>
public sealed class AgentSchemaMigratorTests
{
    [Fact]
    public async Task Apply_IsIdempotentAndCreatesOnlyCurrentExtensionTables()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await AgentSchemaMigrator.ApplyAsync(db);
        await AgentSchemaMigrator.ApplyAsync(db);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN (
                'project_index_manifests',
                'discovery_records',
                'discovery_score_snapshots',
                'marketplace_sync_runs',
                'artifact_candidates',
                'artifacts',
                'artifact_score_snapshots',
                'installability_results',
                'deployments',
                'agent_approvals',
                'agent_schedules',
                'mcp_servers',
                'marketplace_update_checks',
                'marketplace_activity_events',
                'jira_analysis_runs'
              )
            ORDER BY name;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));

        Assert.Equal(
            [
                "artifact_candidates",
                "artifact_score_snapshots",
                "artifacts",
                "deployments",
                "discovery_records",
                "discovery_score_snapshots",
                "installability_results",
                "jira_analysis_runs",
                "marketplace_sync_runs",
                "project_index_manifests",
            ],
            names);
    }
}
