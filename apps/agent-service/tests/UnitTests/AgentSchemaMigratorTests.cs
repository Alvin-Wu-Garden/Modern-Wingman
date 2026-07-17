using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentService.UnitTests;

public sealed class AgentSchemaMigratorTests
{
    [Fact]
    public async Task Apply_IsIdempotentAndCreatesAgentExtensionTables()
    {
        await using var connection=new SqliteConnection("DataSource=:memory:");await connection.OpenAsync();var options=new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;await using var db=new AppDbContext(options);await db.Database.EnsureCreatedAsync();await AgentSchemaMigrator.ApplyAsync(db);await AgentSchemaMigrator.ApplyAsync(db);
        await using var command=connection.CreateCommand();command.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name IN ('mcp_servers','vcs_operations','agent_approvals','agent_schedules','marketplace_sources','discovery_records','discovery_score_snapshots','marketplace_sync_runs','marketplace_update_checks','marketplace_activity_events') ORDER BY name";await using var reader=await command.ExecuteReaderAsync();var names=new List<string>();while(await reader.ReadAsync())names.Add(reader.GetString(0));Assert.Equal(["agent_approvals","agent_schedules","discovery_records","discovery_score_snapshots","marketplace_activity_events","marketplace_sources","marketplace_sync_runs","marketplace_update_checks","mcp_servers","vcs_operations"],names);
    }
}
