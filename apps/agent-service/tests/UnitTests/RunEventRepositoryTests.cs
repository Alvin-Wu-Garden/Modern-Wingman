using AgentService.Application.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentService.UnitTests;
public sealed class RunEventRepositoryTests
{
    [Fact]public async Task AppendAndCursorList_PreserveOrder()
    {
        await using var connection=new SqliteConnection("DataSource=:memory:");await connection.OpenAsync();var options=new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;await using(var db=new AppDbContext(options))await db.Database.EnsureCreatedAsync();var factory=new Factory(options);var repo=new RunEventRepository(factory);var first=await repo.AppendAsync(RunStreamEvent.Phase("run","explore"));var second=await repo.AppendAsync(RunStreamEvent.PlanReady("run","plan"));var all=await repo.ListAsync("run",0,10);Assert.Equal(2,all.Count);Assert.Equal(first,all[0].Sequence);var remaining=await repo.ListAsync("run",first,10);Assert.Single(remaining);Assert.Equal(second,remaining[0].Sequence);
    }
    private sealed class Factory(DbContextOptions<AppDbContext> options):IDbContextFactory<AppDbContext>{public AppDbContext CreateDbContext()=>new(options);}
}
