using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using AgentService.Infrastructure.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentService.UnitTests;
public sealed class LlmTelemetryRetryTests
{
    [Fact]public async Task Retry_ClosesAttemptAndCreatesNextAttempt()
    {
        await using var connection=new SqliteConnection("DataSource=:memory:");await connection.OpenAsync();var options=new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;await using(var setup=new AppDbContext(options))await setup.Database.EnsureCreatedAsync();var factory=new Factory(options);var recorder=new LlmTelemetryRecorder(factory,Options.Create(new LlmTelemetryOptions()),NullLogger<LlmTelemetryRecorder>.Instance);var profile=new ModelProviderProfile{Id="provider",DisplayName="Provider",Kind=ProviderKind.CopilotByok,ModelId="model"};var handle=await recorder.StartRequestAsync(new(new("chat",RunId:"run"),profile,"model",true,"prompt"));var retried=await recorder.RetryAsync(handle,profile,"model",LlmTimeoutKind.FirstToken);Assert.NotNull(retried);Assert.NotEqual(handle!.AttemptId,retried!.AttemptId);await using var db=new AppDbContext(options);var attempts=await db.AiRequestAttempts.OrderBy(x=>x.AttemptNo).ToListAsync();Assert.Equal(2,attempts.Count);Assert.Equal("timed_out",attempts[0].Status);Assert.Equal(LlmTimeoutKind.FirstToken,attempts[0].RetryReason);Assert.Equal("running",attempts[1].Status);
    }
    private sealed class Factory(DbContextOptions<AppDbContext> options):IDbContextFactory<AppDbContext>{public AppDbContext CreateDbContext()=>new(options);}
}
