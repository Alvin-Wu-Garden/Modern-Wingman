namespace AgentService.Infrastructure.Marketplace;

public sealed class MarketplacePrerequisiteException(string message) : InvalidOperationException(message);
