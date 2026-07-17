using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using AgentService.Infrastructure.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentService.UnitTests;

public sealed class AuditQueryServiceTests: IAsyncLifetime
{
    private SqliteConnection _connection=null!;private IDbContextFactory<AppDbContext> _factory=null!;
    public async Task InitializeAsync(){_connection=new("DataSource=:memory:");await _connection.OpenAsync();var options=new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;_factory=new Factory(options);await using var db=await _factory.CreateDbContextAsync();await db.Database.EnsureCreatedAsync();db.AuditEvents.AddRange(new AuditEventRecord{Id="1",TraceId="trace-1",EventType="vcs.push",TargetType="project",TargetId="p1",Action="push",Result="success",DetailsJson="{\"token\":\"secret-value\"}",CreatedAt=DateTimeOffset.UtcNow},new AuditEventRecord{Id="2",TraceId="trace-2",EventType="provider.key",TargetType="provider",TargetId="openai",Action="update",Result="failed",CreatedAt=DateTimeOffset.UtcNow.AddMinutes(-1)});await db.SaveChangesAsync();}
    public async Task DisposeAsync()=>await _connection.DisposeAsync();

    [Fact]public async Task Query_FiltersAndRedactsDetails(){var service=new AuditQueryService(_factory,new SensitiveDataRedactor());var page=await service.QueryAsync(new(EventType:"vcs.push"));var item=Assert.Single(page.Items);Assert.Equal("trace-1",item.TraceId);Assert.DoesNotContain("secret-value",item.DetailsJson);Assert.Contains("REDACTED",item.DetailsJson);}
    [Fact]public async Task ExportCsv_QuotesRows(){var service=new AuditQueryService(_factory,new SensitiveDataRedactor());var csv=await service.ExportCsvAsync(new());Assert.StartsWith("createdAt,id",csv);Assert.Contains("\"vcs.push\"",csv);Assert.DoesNotContain("secret-value",csv);}
    [Fact]public async Task Facets_ReturnFriendlySelectableValues(){var service=new AuditQueryService(_factory,new SensitiveDataRedactor());var facets=await service.GetFacetsAsync();Assert.Contains("vcs.push",facets.EventTypes);Assert.Contains("project",facets.TargetTypes);Assert.Contains(facets.Targets,x=>x.Value=="p1"&&x.Group=="project");Assert.Contains("trace-1",facets.TraceIds);}
    [Fact]public async Task ToolCsv_IsAvailableWhenNoRowsMatch(){var service=new AuditQueryService(_factory,new SensitiveDataRedactor());var csv=await service.ExportToolCallsCsvAsync(new());Assert.StartsWith("startedAt,id,traceId",csv);}
    [Theory][InlineData("Authorization: Bearer abcdef","abcdef")][InlineData("https://user:password@example.test/repo","password")][InlineData("{\"apiKey\":\"sk-test\"}","sk-test")]
    public void Redactor_RemovesCommonSecrets(string input,string secret)=>Assert.DoesNotContain(secret,new SensitiveDataRedactor().Redact(input));
    private sealed class Factory(DbContextOptions<AppDbContext> options):IDbContextFactory<AppDbContext>{public AppDbContext CreateDbContext()=>new(options);}
}
