using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentService.CoreRegression;

public sealed class ConversationRepositoryRouteBoundaryTests
{
    [Fact]
    public async Task 一般與專案對話會依ProjectId隔離()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
            await db.Database.EnsureCreatedAsync();

        var repository = new ConversationRepository(new TestDbContextFactory(options));
        var general = await repository.CreateGeneralAsync("provider");
        var projectA = await repository.CreateProjectAsync("project-a", "provider");
        var projectB = await repository.CreateProjectAsync("project-b", "provider");

        Assert.Equal([general.Id], (await repository.ListGeneralAsync()).Select(item => item.Id));
        Assert.Equal([projectA.Id], (await repository.ListProjectAsync("project-a")).Select(item => item.Id));
        Assert.Null(await repository.GetProjectAsync("project-b", projectA.Id));
        Assert.Null(await repository.GetGeneralAsync(projectA.Id));
        Assert.NotNull(await repository.GetProjectAsync("project-b", projectB.Id));
    }

    [Fact]
    public async Task 舊版Scope欄位會在啟動時移除()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Conversations\" ADD COLUMN \"Scope\" TEXT NOT NULL DEFAULT 'General';");
            db.Conversations.Add(new ConversationEntity());
            await db.SaveChangesAsync();
            await AgentSchemaMigrator.ApplyAsync(db);
        }

        await using var verify = new AppDbContext(options);
        var columns = new List<string>();
        await using var command = verify.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA table_info(\"Conversations\");";
        await verify.Database.OpenConnectionAsync();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        }

        Assert.DoesNotContain("Scope", columns);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppDbContext(options));
    }
}
