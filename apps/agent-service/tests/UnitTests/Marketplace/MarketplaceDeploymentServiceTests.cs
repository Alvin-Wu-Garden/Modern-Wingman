using AgentService.Infrastructure.Marketplace;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.UnitTests.Marketplace;

public sealed class MarketplaceDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wingman-marketplace-deploy-" + Guid.NewGuid().ToString("N"));
    public MarketplaceDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DeploySkill_CopiesSnapshotAndRecordsExplicitTargetScope()
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, "snapshot"));
        await File.WriteAllTextAsync(Path.Combine(snapshot.FullName, "SKILL.md"), "# Skill");
        var hash = await FolderArtifactResolver.HashDirectoryAsync(snapshot.FullName, CancellationToken.None);
        var artifact = new MarketplaceArtifact("artifact", "candidate", MarketplaceArtifactKind.Skill, "review-skill", snapshot.FullName, hash, MarketplaceDiscoveryStatus.Resolved, "agent-skill-standard/v1", DateTimeOffset.UtcNow);
        var targetRoot = Path.Combine(_root, "target");
        var service = new MarketplaceDeploymentService(new ArtifactStore(artifact), new DeploymentStore(), new InstallabilityStore(), [new TestTargetAdapter(targetRoot)]);

        var result = await service.DeploySkillAsync([new("artifact", "test-agent", MarketplaceDeploymentScope.Global)]);

        var item = Assert.Single(result.Results);
        Assert.Equal("Deployed", item.Status);
        Assert.True(File.Exists(Path.Combine(targetRoot, "review-skill", "SKILL.md")));
    }

    [Fact]
    public async Task DeploySkill_DoesNotOverwriteConflictingContent()
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, "snapshot"));
        await File.WriteAllTextAsync(Path.Combine(snapshot.FullName, "SKILL.md"), "# Skill");
        var hash = await FolderArtifactResolver.HashDirectoryAsync(snapshot.FullName, CancellationToken.None);
        var artifact = new MarketplaceArtifact("artifact", "candidate", MarketplaceArtifactKind.Skill, "review-skill", snapshot.FullName, hash, MarketplaceDiscoveryStatus.Resolved, null, DateTimeOffset.UtcNow);
        var targetRoot = Directory.CreateDirectory(Path.Combine(_root, "target"));
        var existing = Directory.CreateDirectory(Path.Combine(targetRoot.FullName, "review-skill"));
        await File.WriteAllTextAsync(Path.Combine(existing.FullName, "SKILL.md"), "# User change");
        var service = new MarketplaceDeploymentService(new ArtifactStore(artifact), new DeploymentStore(), new InstallabilityStore(), [new TestTargetAdapter(targetRoot.FullName)]);

        var preview = await service.PreviewSkillAsync([new("artifact", "test-agent", MarketplaceDeploymentScope.Global)]);

        var result = await service.DeploySkillAsync([new("artifact", "test-agent", MarketplaceDeploymentScope.Global)]);

        Assert.Equal("BlockedByConflict", Assert.Single(result.Results).Status);
        Assert.Equal("BlockedByConflict", Assert.Single(preview.Items).Status);
        Assert.Equal("# User change", await File.ReadAllTextAsync(Path.Combine(existing.FullName, "SKILL.md")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class TestTargetAdapter(string root) : IAgentTargetAdapter
    {
        public MarketplaceTargetDescriptor Descriptor { get; } = new("test-agent", "Test", true, false, true, true);
        public string ResolveSkillDirectory(MarketplaceDeploymentScope scope, string? projectPath) => scope == MarketplaceDeploymentScope.Global ? root : Path.Combine(projectPath!, ".test", "skills");
        public string ResolveMcpConfigPath(MarketplaceDeploymentScope scope, string? projectPath) => Path.Combine(root, "mcp.json");
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
        public Task SaveDeploymentAsync(string artifactId, MarketplaceDeploymentRequest request, string targetPath, string deployedHash, string status, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<(string TargetId, MarketplaceDeploymentScope Scope, string TargetPath, string DeployedHash, string Status)>> ListDeploymentsAsync(string artifactId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<(string, MarketplaceDeploymentScope, string, string, string)>>([]);
        public Task UpdateDeploymentStatusAsync(string artifactId, string targetId, MarketplaceDeploymentScope scope, string status, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InstallabilityStore : IMarketplaceInstallabilityStore
    {
        public Task SaveAsync(IReadOnlyList<MarketplaceInstallabilityResult> results, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MarketplaceInstallabilityResult>> ListAsync(string artifactId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketplaceInstallabilityResult>>([]);
    }
}
