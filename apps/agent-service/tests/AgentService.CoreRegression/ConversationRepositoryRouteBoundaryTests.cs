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
    public async Task 舊版Schema會被破壞式重建而不保留相容欄位()
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
        Assert.Empty(await verify.Conversations.ToListAsync());
    }

    [Fact]
    public async Task 舊版Projects殘留PendingFileCount時_重建後仍可新增專案()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Projects\" ADD COLUMN \"PendingFileCount\" INTEGER NOT NULL;");

        await AgentSchemaMigrator.ApplyAsync(db);
        db.Projects.Add(new ProjectRecord
        {
            Id = "project-after-reset",
            Name = "重建後專案",
            RootPath = "D:\\Project",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"Projects\");";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        }
        Assert.DoesNotContain("PendingFileCount", columns);
        Assert.Single(await db.Projects.ToListAsync());
    }

    [Fact]
    public async Task 相同TurnId重試不會重複保存UserMessage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
            await db.Database.EnsureCreatedAsync();

        var repository = new ConversationRepository(new TestDbContextFactory(options));
        var conversation = await repository.CreateGeneralAsync("provider");
        var first = await repository.BeginTurnAsync(
            conversation.Id,
            "turn-1",
            "同一個問題",
            "provider",
            "model");
        var duplicate = await repository.BeginTurnAsync(
            conversation.Id,
            "turn-1",
            "同一個問題",
            "provider",
            "model");

        Assert.Equal(first.UserMessageId, duplicate.UserMessageId);
        await using var verify = new AppDbContext(options);
        Assert.Single(await verify.Messages
            .Where(x => x.ConversationId == conversation.Id && x.Role == MessageRole.User)
            .ToListAsync());
        Assert.Equal(1, duplicate.AttemptCount);
    }

    [Fact]
    public async Task 失敗Turn使用相同識別碼重試會恢復而不新增UserMessage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
            await db.Database.EnsureCreatedAsync();

        var repository = new ConversationRepository(new TestDbContextFactory(options));
        var conversation = await repository.CreateGeneralAsync("provider");
        var first = await repository.BeginTurnAsync(
            conversation.Id,
            "turn-retry",
            "需要重試",
            "provider",
            "model");
        await repository.FailTurnAsync(
            conversation.Id,
            "turn-retry",
            ConversationTurnStatus.Failed,
            "agent_execution_failed");

        var retried = await repository.BeginTurnAsync(
            conversation.Id,
            "turn-retry",
            "需要重試",
            "provider",
            "model");

        Assert.Equal(first.UserMessageId, retried.UserMessageId);
        Assert.Equal(2, retried.AttemptCount);
        Assert.Equal(ConversationTurnStatus.Running, retried.Status);
        await using var verify = new AppDbContext(options);
        Assert.Single(await verify.Messages
            .Where(x => x.ConversationId == conversation.Id && x.Role == MessageRole.User)
            .ToListAsync());
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
