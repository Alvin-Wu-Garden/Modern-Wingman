using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using AgentService.Infrastructure.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class RunRepositoryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<AppDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        // in-memory SQLite：連線保持開啟期間資料庫存活
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task SaveAndGet_RoundTrips()
    {
        var repo = new RunRepository(_factory);
        var run = new RunEntity
        {
            SessionId = "sess-1",
            UserMessage = "修正登入 bug",
            WorkspacePath = @"C:\work\proj",
            ProjectId = "project-1",
            Mode = AgentMode.Auto,
            WorkspaceStrategy = WorkspaceStrategy.GitWorktree,
        };
        run.Status = RunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;

        await repo.SaveAsync(run);
        var loaded = await repo.GetAsync(run.Id);

        Assert.NotNull(loaded);
        Assert.Equal(run.Id, loaded.Id);
        Assert.Equal("sess-1", loaded.SessionId);
        Assert.Equal("修正登入 bug", loaded.UserMessage);
        Assert.Equal(RunStatus.Running, loaded.Status);
        Assert.Equal("project-1", loaded.ProjectId);
        Assert.Equal(AgentMode.Auto, loaded.Mode);
        Assert.Equal(WorkspaceStrategy.GitWorktree, loaded.WorkspaceStrategy);
    }

    [Fact]
    public async Task Save_IsUpsert_UpdatesStatus()
    {
        var repo = new RunRepository(_factory);
        var run = new RunEntity { SessionId = "s", UserMessage = "m" };

        await repo.SaveAsync(run);
        run.Status = RunStatus.Completed;
        run.EndedAt = DateTimeOffset.UtcNow;
        await repo.SaveAsync(run);

        var loaded = await repo.GetAsync(run.Id);
        Assert.Equal(RunStatus.Completed, loaded!.Status);
        Assert.NotNull(loaded.EndedAt);
    }

    [Fact]
    public async Task ListBySession_FiltersAndOrders()
    {
        var repo = new RunRepository(_factory);
        var a = new RunEntity { SessionId = "s1", UserMessage = "first" };
        var b = new RunEntity { SessionId = "s1", UserMessage = "second" };
        var other = new RunEntity { SessionId = "s2", UserMessage = "other" };

        await repo.SaveAsync(a);
        await repo.SaveAsync(b);
        await repo.SaveAsync(other);

        var list = await repo.ListBySessionAsync("s1");
        Assert.Equal(2, list.Count);
        Assert.All(list, r => Assert.Equal("s1", r.SessionId));
    }

    [Fact]
    public async Task Get_Unknown_ReturnsNull()
    {
        var repo = new RunRepository(_factory);
        Assert.Null(await repo.GetAsync("nope"));
    }

    [Fact]
    public async Task Recovery_MarksOnlyInterruptedRunsPaused()
    {
        var repo=new RunRepository(_factory);
        var running=new RunEntity{SessionId="s",UserMessage="running"};running.Status=RunStatus.Running;
        var waiting=new RunEntity{SessionId="s",UserMessage="waiting"};waiting.Status=RunStatus.WaitingApproval;
        var completed=new RunEntity{SessionId="s",UserMessage="done"};completed.Status=RunStatus.Completed;
        await repo.SaveAsync(running);await repo.SaveAsync(waiting);await repo.SaveAsync(completed);
        await new RunRecoveryService(repo,NullLogger<RunRecoveryService>.Instance).StartAsync(CancellationToken.None);
        Assert.Equal(RunStatus.Paused,(await repo.GetAsync(running.Id))!.Status);
        Assert.Equal(RunStatus.Paused,(await repo.GetAsync(waiting.Id))!.Status);
        Assert.Equal(RunStatus.Completed,(await repo.GetAsync(completed.Id))!.Status);
    }

    [Theory]
    [InlineData("vcs")]
    [InlineData("mcp")]
    public async Task ReplayGuard_BlocksSuccessfulExternalSideEffects(string toolType)
    {
        const string runId = "run-side-effect";
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AiProviderProfiles.Add(new AiProviderProfileRecord
            {
                ProfileId = "provider",
                DisplayName = "Provider",
                Kind = "test",
            });
            var request = new AiRequestLogRecord
            {
                ProviderProfileId = "provider",
                FeatureArea = "agent",
                RunId = runId,
            };
            db.AiRequestLogs.Add(request);
            db.AiToolCallLogs.Add(new AiToolCallLogRecord
            {
                RequestLog = request,
                ToolName = "side-effect",
                ToolType = toolType,
                Status = "succeeded",
            });
            await db.SaveChangesAsync();
        }

        var guard = new RunReplayGuard(_factory);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.EnsureReplayAllowedAsync(runId));
        Assert.Contains("cannot be replayed", error.Message);
    }

    [Fact]
    public async Task ReplayGuard_AllowsReadOnlyOrFailedTools()
    {
        var guard = new RunReplayGuard(_factory);
        await guard.EnsureReplayAllowedAsync("run-without-side-effects");
    }
}
