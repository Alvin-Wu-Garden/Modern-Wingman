using AgentService.Application.Contracts;
using AgentService.Host.DependencyInjection;
using AgentService.Infrastructure.Persistence;
using AgentService.Modules.GraphRAG;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 內建 Neo4j 的密碼是目前 Windows 使用者的 DPAPI 資料，不寫入原始碼或設定檔。
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Neo4j:Password"] = GraphRagNeo4jCredentialStore.Resolve(builder.Configuration),
});

builder.Services.AddAgentServices(builder.Configuration, builder.Environment);

var app = builder.Build();

// Modern Wingman 尚未發布，本次重構採乾淨 schema。
// EF Core 負責核心資料表，少數不屬於 EF entity 的 GraphRAG／Marketplace 表由單一 migrator 建立。
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await AgentSchemaMigrator.ApplyAsync(db);

    // Provider 清單直接由 appsettings 定義；SQLite 只在使用者實際儲存 Key、
    // 自訂網址或排序時才建立資料列，因此全新安裝保持零設定資料。
}

app.MapAgentEndpoints();

app.Run();

// 讓隔離式整合測試可以建立最小 ASP.NET Host。
public partial class Program;
