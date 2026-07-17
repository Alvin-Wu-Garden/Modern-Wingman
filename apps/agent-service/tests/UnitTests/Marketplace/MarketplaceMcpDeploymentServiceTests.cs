using System.Text.Json.Nodes;
using AgentService.Infrastructure.Marketplace;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.UnitTests.Marketplace;

public sealed class MarketplaceMcpDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wingman-marketplace-mcp-" + Guid.NewGuid().ToString("N"));
    public MarketplaceMcpDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Configure_MergesUnrelatedEntriesAndWritesSecretPlaceholder()
    {
        var artifact = await CreateArtifactAsync();
        var config = Path.Combine(_root, "target", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "{\"mcpServers\":{\"user-server\":{\"command\":\"user\"}},\"other\":true}");
        var store = new DeploymentStore();
        var service = new MarketplaceMcpDeploymentService(new ArtifactStore(artifact), store, new InstallabilityStore(), [new TestTargetAdapter(config)]);

        var result = await service.ConfigureAsync([new(artifact.Id, "test-agent", MarketplaceDeploymentScope.Global)]);

        Assert.Equal("NeedsUserInput", Assert.Single(result.Results).Status);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(config))!.AsObject();
        Assert.True(root["other"]!.GetValue<bool>());
        var servers = root["mcpServers"]!.AsObject();
        Assert.NotNull(servers["user-server"]);
        Assert.Equal("REPLACE_WITH_YOUR_API_KEY", servers["internal-search"]!["env"]!["SEARCH_API_KEY"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configure_RefusesExistingUnmanagedMcpId()
    {
        var artifact = await CreateArtifactAsync();
        var config = Path.Combine(_root, "target", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "{\"mcpServers\":{\"internal-search\":{\"command\":\"user-owned\"}}}");
        var service = new MarketplaceMcpDeploymentService(new ArtifactStore(artifact), new DeploymentStore(), new InstallabilityStore(), [new TestTargetAdapter(config)]);

        var preview = await service.PreviewAsync([new(artifact.Id, "test-agent", MarketplaceDeploymentScope.Global)]);

        var result = await service.ConfigureAsync([new(artifact.Id, "test-agent", MarketplaceDeploymentScope.Global)]);

        var failed = Assert.Single(result.Results);
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("無法證明由 Wingman 建立", failed.Message);
        Assert.Equal("BlockedByConflict", Assert.Single(preview.Items).Status);
    }

    private async Task<MarketplaceArtifact> CreateArtifactAsync()
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, "snapshot", Guid.NewGuid().ToString("N")));
        await File.WriteAllTextAsync(Path.Combine(snapshot.FullName, ".mcp.json"), """
            {"mcpServers":{"internal-search":{"command":"search","args":["--stdio"],"env":{"SEARCH_API_KEY":"should-never-be-written"}}}}
            """);
        return new("mcp-artifact", "candidate", MarketplaceArtifactKind.McpServer, "internal-search", snapshot.FullName, "hash", MarketplaceDiscoveryStatus.Resolved, "wingman-mcp-definition/v1", DateTimeOffset.UtcNow);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class TestTargetAdapter(string configPath) : IAgentTargetAdapter
    {
        public MarketplaceTargetDescriptor Descriptor { get; } = new("test-agent", "Test", true, true, true, true);
        public string ResolveSkillDirectory(MarketplaceDeploymentScope scope, string? projectPath) => Path.Combine(_rootPlaceholder, "skills");
        public string ResolveMcpConfigPath(MarketplaceDeploymentScope scope, string? projectPath) => configPath;
        private const string _rootPlaceholder = "unused";
    }

    private sealed class ArtifactStore(MarketplaceArtifact artifact) : IMarketplaceArtifactStore
    {
        public Task SaveImportAsync(IReadOnlyList<MarketplaceArtifactCandidate> candidates, IReadOnlyList<MarketplaceArtifact> artifacts, IReadOnlyList<MarketplaceArtifactScoreSnapshot> scoreSnapshots, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MarketplaceArtifact>> ListArtifactsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketplaceArtifact>>([artifact]);
        public Task<MarketplaceArtifact?> GetArtifactAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<MarketplaceArtifact?>(id == artifact.Id ? artifact : null);
        public Task<MarketplaceArtifact?> GetArtifactByContentHashAsync(string contentHash, MarketplaceArtifactKind kind, CancellationToken cancellationToken = default) => Task.FromResult<MarketplaceArtifact?>(null);
        public Task<IReadOnlyList<MarketplaceArtifactSource>> ListArtifactSourcesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketplaceArtifactSource>>([]);
    }

    private sealed class DeploymentStore : IMarketplaceDeploymentStore
    {
        private readonly List<(string TargetId, MarketplaceDeploymentScope Scope, string TargetPath, string DeployedHash, string Status)> _items = [];
        public Task SaveDeploymentAsync(string artifactId, MarketplaceDeploymentRequest request, string targetPath, string deployedHash, string status, CancellationToken cancellationToken = default)
        { _items.RemoveAll(item => item.TargetId == request.TargetId && item.Scope == request.Scope); _items.Add((request.TargetId, request.Scope, targetPath, deployedHash, status)); return Task.CompletedTask; }
        public Task<IReadOnlyList<(string TargetId, MarketplaceDeploymentScope Scope, string TargetPath, string DeployedHash, string Status)>> ListDeploymentsAsync(string artifactId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<(string, MarketplaceDeploymentScope, string, string, string)>>(_items);
        public Task UpdateDeploymentStatusAsync(string artifactId, string targetId, MarketplaceDeploymentScope scope, string status, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InstallabilityStore : IMarketplaceInstallabilityStore
    {
        public Task SaveAsync(IReadOnlyList<MarketplaceInstallabilityResult> results, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MarketplaceInstallabilityResult>> ListAsync(string artifactId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketplaceInstallabilityResult>>([]);
    }
}
