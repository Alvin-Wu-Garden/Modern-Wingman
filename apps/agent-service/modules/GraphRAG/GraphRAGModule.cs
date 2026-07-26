using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// GraphRAG V3 唯一 DI composition root。
/// 所有 extractor、store、runtime、indexing、retrieval 與 watcher 都必須由此註冊，
/// 禁止在 ServiceRegistration 重新散落一份 GraphRAG 建構邏輯。
/// </summary>
public static class GraphRagModule
{
    /// <summary>
    /// 註冊完整 V3 module。此方法只綁定技術 budget 與連線設定，不提供任何 schema profile。
    /// </summary>
    /// <param name="services">Agent Service DI container。</param>
    /// <param name="configuration">應用程式設定；Neo4j 密碼應由環境變數或 DPAPI 注入。</param>
    /// <returns>同一個 service collection，供 fluent registration 使用。</returns>
    public static IServiceCollection AddGraphRagV3(
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
                CheckCertificateRevocationList = false,
            });

        services.AddSingleton<CSharpGraphExtractor>();
        services.AddSingleton<JavaGraphExtractor>();
        services.AddSingleton<FrontendGraphExtractor>();
        services.AddSingleton<SqlServerGraphExtractor>();
        services.AddSingleton<IGraphExtractor>(
            provider => provider.GetRequiredService<CSharpGraphExtractor>());
        services.AddSingleton<IGraphExtractor>(
            provider => provider.GetRequiredService<JavaGraphExtractor>());
        services.AddSingleton<IGraphExtractor>(
            provider => provider.GetRequiredService<FrontendGraphExtractor>());
        services.AddSingleton<IGraphExtractor>(
            provider => provider.GetRequiredService<SqlServerGraphExtractor>());

        services.AddSingleton<IGraphStore, Neo4jGraphStore>();
        services.AddSingleton<Neo4jRuntime>();
        services.AddSingleton<INeo4jRuntime>(
            provider => provider.GetRequiredService<Neo4jRuntime>());
        services.AddSingleton<
            IGraphDatabaseSourceProvider,
            EnvironmentGraphDatabaseSourceProvider>();
        services.AddSingleton<GraphIndexingService>();
        services.AddSingleton<GraphRetrievalService>();
        services.AddHostedService<GraphIndexWatcherService>();
        return services;
    }
}
