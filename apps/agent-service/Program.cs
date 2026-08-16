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

// Neo4j 常駐：App 啟動就開始確保 managed 子程序已啟動/連線就緒，不用等到使用者送出
// 第一個專案問題或手動觸發索引才呼叫 EnsureAvailableAsync，避免對話當下才發現整輪
// GraphRAG 工具都無法使用。安裝/啟動可能耗時（首次需下載 Neo4j + JRE），用背景 Task
// 執行避免拖慢 App 啟動；任何失敗只記錄 log，仍可用原始碼工具繼續對話。
_ = Task.Run(async () =>
{
    var neo4jLogger = app.Services.GetRequiredService<ILogger<Program>>();
    try
    {
        var neo4jRuntime = app.Services.GetRequiredService<INeo4jRuntime>();
        var ready = await neo4jRuntime.EnsureAvailableAsync();
        neo4jLogger.LogInformation(
            "Neo4j \u555f\u52d5\u6642\u9810\u5148\u78ba\u4fdd\u5b8c\u6210\u3002Ready={Ready}, Status={Status}, LastError={LastError}",
            ready,
            neo4jRuntime.Status,
            neo4jRuntime.LastError);
    }
    catch (Exception exception)
    {
        neo4jLogger.LogWarning(exception, "Neo4j \u555f\u52d5\u6642\u9810\u5148\u78ba\u4fdd\u5931\u6557\uff1b\u672c\u8f2a\u5c0d\u8a71\u4ecd\u53ef\u7528\u539f\u59cb\u78bc\u5de5\u5177\u3002");
    }
});

app.Run();

// 讓隔離式整合測試可以建立最小 ASP.NET Host。
public partial class Program;
