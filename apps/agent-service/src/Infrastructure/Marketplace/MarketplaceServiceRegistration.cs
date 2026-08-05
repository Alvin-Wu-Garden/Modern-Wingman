using Wingman.Marketplace.Application;
using Wingman.Marketplace.Contracts;

namespace AgentService.Infrastructure.Marketplace;

public static class MarketplaceServiceRegistration
{
    public static IServiceCollection AddMarketplaceServices(this IServiceCollection services)
    {
        services.AddHttpClient("marketplace-github", client =>
            {
                client.BaseAddress = new Uri("https://api.github.com/");
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Modern-Wingman/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Marketplace 只允許透過正式 GitHub TLS 連線，不因企業網路問題永久關閉撤銷檢查。
                CheckCertificateRevocationList = true,
            });
        services.AddSingleton<MarketplaceDiscoveryClassifier>();
        services.AddSingleton<MarketplaceDiscoveryScorer>();
        services.AddSingleton<MarketplaceArtifactQualityScorer>();
        services.AddSingleton<IMarketplaceStore, MarketplaceSqliteStore>();
        services.AddSingleton<IMarketplaceArtifactStore, MarketplaceArtifactSqliteStore>();
        services.AddSingleton<IMarketplaceDeploymentStore, MarketplaceDeploymentSqliteStore>();
        services.AddSingleton<IMarketplaceInstallabilityStore, MarketplaceInstallabilitySqliteStore>();
        services.AddSingleton<IDiscoveryProvider, GitHubDiscoveryProvider>();
        services.AddSingleton<IMarketplaceService, MarketplaceService>();
        services.AddSingleton<IArtifactResolver, FolderArtifactResolver>();
        services.AddSingleton<MarketplaceRegistryPathResolver>();
        services.AddSingleton<IMarketplaceArtifactService, MarketplaceArtifactService>();
        foreach (var adapter in BuiltInAgentTargets.Create()) services.AddSingleton(typeof(IAgentTargetAdapter), adapter);
        services.AddSingleton<IMarketplaceDeploymentService, MarketplaceDeploymentService>();
        services.AddSingleton<IMarketplaceMcpDeploymentService, MarketplaceMcpDeploymentService>();
        services.AddSingleton<IGitHubRepositoryImportService, GitHubRepositoryImportService>();
        return services;
    }
}
