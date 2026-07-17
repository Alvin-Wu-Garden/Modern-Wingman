using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentService.UnitTests;

public sealed class ApprovalRepositoryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<AppDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
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

    [Fact]
    public async Task SaveResolveAndListPending_RoundTrips()
    {
        var repository = new ApprovalRepository(_factory);
        var approval = new AgentApproval
        {
            RunId = "run-1",
            Operation = "git_push",
            Capabilities = AgentCapability.ExternalSideEffect,
            RiskLevel = AgentRiskLevel.High,
        };

        await repository.SaveAsync(approval);
        Assert.Single(await repository.ListPendingByRunAsync("run-1"));

        approval.Status = ApprovalStatus.Approved;
        approval.Scope = ApprovalScope.Once;
        approval.ResolvedAt = DateTimeOffset.UtcNow;
        await repository.SaveAsync(approval);

        var loaded = await repository.GetAsync(approval.Id);
        Assert.NotNull(loaded);
        Assert.Equal(ApprovalStatus.Approved, loaded.Status);
        Assert.Equal(ApprovalScope.Once, loaded.Scope);
        Assert.Empty(await repository.ListPendingByRunAsync("run-1"));
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
