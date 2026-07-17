using Wingman.Marketplace.Application;
using Wingman.Marketplace.Contracts;
using AgentService.Infrastructure.AgentFramework.Plugins;

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
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { CheckCertificateRevocationList = false });
        services.AddSingleton<MarketplaceDiscoveryClassifier>();
        services.AddSingleton<MarketplaceDiscoveryScorer>();
        services.AddSingleton<MarketplaceArtifactQualityScorer>();
        services.AddSingleton<IMarketplaceStore, MarketplaceSqliteStore>();
        services.AddSingleton<IMarketplaceArtifactStore, MarketplaceArtifactSqliteStore>();
        services.AddSingleton<IMarketplaceUpdateHistoryStore, MarketplaceUpdateHistorySqliteStore>();
        services.AddSingleton<IMarketplaceDeploymentStore, MarketplaceDeploymentSqliteStore>();
        services.AddSingleton<IMarketplaceInstallabilityStore, MarketplaceInstallabilitySqliteStore>();
        services.AddSingleton<IMarketplacePluginStore, MarketplacePluginSqliteStore>();
        services.AddSingleton<IMarketplacePluginConfigurationStore, MarketplacePluginConfigurationSqliteStore>();
        services.AddSingleton<IDiscoveryProvider, GitHubDiscoveryProvider>();
        services.AddSingleton<IMarketplaceService, MarketplaceService>();
        services.AddSingleton<IArtifactResolver, FolderArtifactResolver>();
        services.AddSingleton<MarketplaceRegistryPathResolver>();
        services.AddSingleton<IMarketplaceArtifactService, MarketplaceArtifactService>();
        services.AddSingleton<IMarketplaceUpdateService, MarketplaceUpdateService>();
        services.AddSingleton<IMarketplaceActivityRecorder, MarketplaceActivityRecorder>();
        foreach (var adapter in BuiltInAgentTargets.Create()) services.AddSingleton(typeof(IAgentTargetAdapter), adapter);
        services.AddSingleton<IMarketplaceDeploymentService, MarketplaceDeploymentService>();
        services.AddSingleton<IMarketplaceMcpDeploymentService, MarketplaceMcpDeploymentService>();
        services.AddSingleton<IMarketplacePluginService, MarketplacePluginService>();
        services.AddSingleton<IEnabledPluginCapabilitySource, EnabledPluginCapabilitySource>();
        services.AddSingleton<ICodexMarketplaceImportService, CodexMarketplaceImportService>();
        services.AddSingleton<IGitHubRepositoryImportService, GitHubRepositoryImportService>();
        services.AddSingleton<MarketplaceLegacyMigration>();
        services.AddSingleton<PluginRuntimeManifestLoader>();
        services.AddSingleton<PluginRuntimeToolRegistrar>();
        services.AddSingleton<MarketplacePluginHookDispatcher>();
        services.AddSingleton<AgentService.Application.Contracts.IAgentHook>(sp => sp.GetRequiredService<MarketplacePluginHookDispatcher>());
        services.AddSingleton<IPluginRuntimeEnablementObserver>(sp => sp.GetRequiredService<MarketplacePluginHookDispatcher>());
        services.AddSingleton<AgentService.Application.Contracts.IPluginMcpServerSource, PluginMcpServerSource>();
        services.AddSingleton<MafPluginRuntimeAdapter>();
        services.AddSingleton<IPluginCapabilitySnapshotInvalidator>(sp => sp.GetRequiredService<MafPluginRuntimeAdapter>());
        services.AddSingleton<IPluginRuntimeEnablementObserver>(sp => sp.GetRequiredService<MafPluginRuntimeAdapter>());
        return services;
    }
}
