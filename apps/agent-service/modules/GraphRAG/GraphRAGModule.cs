using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgentService.Modules.GraphRAG.FblAuthority;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// GraphRAG V4 唯一 DI composition root。
/// 所有 extractor、store、runtime、indexing、retrieval 與 watcher 都必須由此註冊，
/// 禁止在 ServiceRegistration 重新散落一份 GraphRAG 建構邏輯。
/// </summary>
public static class GraphRagModule
{
    /// <summary>
    /// 註冊完整 V4 module。此方法只綁定技術 budget 與連線設定，不提供任何 schema profile。
    /// </summary>
    /// <param name="services">Agent Service DI container。</param>
    /// <param name="configuration">應用程式設定；Neo4j 密碼由本機設定與 DPAPI 生命週期管理。</param>
    /// <returns>同一個 service collection，供 fluent registration 使用。</returns>
    public static IServiceCollection AddGraphRagV4(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<GraphRagNeo4jOptions>()
            .Bind(configuration.GetSection(GraphRagNeo4jOptions.SectionName));
        services.AddOptions<GraphRagNeo4jRuntimeOptions>()
            .Bind(configuration.GetSection(GraphRagNeo4jRuntimeOptions.SectionName));
        services.AddOptions<GraphIndexingOptions>()
            .Bind(configuration.GetSection(GraphIndexingOptions.SectionName));
        services.AddOptions<GraphRetrievalOptions>()
            .Bind(configuration.GetSection("GraphRAG:Retrieval"));

        services.AddHttpClient(
                "neo4j-download",
                client => client.Timeout = TimeSpan.FromMinutes(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Neo4j 套件下載也必須保留 TLS 憑證撤銷檢查，避免以方便開發為由降低供應鏈安全性。
                CheckCertificateRevocationList = true,
            });

        // FBLAuthority 是唯一抽取核心；禁止再註冊通用語言 Extractor 或舊 Graph assembler。
        services.AddSingleton<FblAuthorityGraphBuilder>();
        services.AddSingleton<Neo4jGraphStore>();
        services.AddSingleton<IGraphStore>(
            provider => provider.GetRequiredService<Neo4jGraphStore>());
        services.AddSingleton<GraphCommunitySummaryQueue>();
        services.AddSingleton<IGraphCommunitySummaryQueue>(
            provider => provider.GetRequiredService<GraphCommunitySummaryQueue>());
        services.AddHostedService(
            provider => provider.GetRequiredService<GraphCommunitySummaryQueue>());
        services.AddSingleton<GraphCommunityAiService>();
        services.AddSingleton<Neo4jRuntime>();
        services.AddSingleton<INeo4jRuntime>(
            provider => provider.GetRequiredService<Neo4jRuntime>());
        services.AddHostedService<Neo4jWarmupService>();
        services.AddSingleton<
            IGraphDatabaseSourceProvider,
            ProjectGraphDatabaseSourceProvider>();
        services.AddSingleton<ProjectGraphDatabaseExtractor>();
        services.AddSingleton<GraphIndexingService>();
        services.AddSingleton<GraphRetrievalService>();
        // Watcher 必須同時以 Singleton 與 HostedService 使用同一個實例，讓專案端點能在
        // Save/Delete 完成後立即註冊或解除監看，而不是由背景服務輪詢 SQLite。
        services.AddSingleton<GraphIndexWatcherService>();
        services.AddSingleton<IGraphIndexWatcherRegistry>(
            provider => provider.GetRequiredService<GraphIndexWatcherService>());
        services.AddHostedService(
            provider => provider.GetRequiredService<GraphIndexWatcherService>());
        return services;
    }
}
