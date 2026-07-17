namespace AgentService.Infrastructure.Marketplace;

public sealed class MarketplaceRegistryPathResolver
{
    public string Root { get; } = ResolveRoot();
    public string BlobRoot => Path.Combine(Root, "blobs");
    public string StagingRoot => Path.Combine(Root, "staging");

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("WINGMAN_REGISTRY_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".Wingman", "registry");
    }
}
