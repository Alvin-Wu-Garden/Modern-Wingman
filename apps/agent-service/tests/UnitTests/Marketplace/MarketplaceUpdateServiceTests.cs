using System.Net;
using System.Text;
using AgentService.Infrastructure.Marketplace;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.UnitTests.Marketplace;

public sealed class MarketplaceUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_PersistsManualCheckAndDoesNotImportOrDeploy()
    {
        var artifact = new MarketplaceArtifact("artifact", "candidate", MarketplaceArtifactKind.Skill, "sample", "snapshot", "hash", MarketplaceDiscoveryStatus.Resolved, null, DateTimeOffset.UtcNow);
        var history = new HistoryStore();
        var activity = new ActivityRecorder();
        var client = new HttpClient(new JsonHandler("{\"sha\":\"new-sha\"}")) { BaseAddress = new Uri("https://api.github.com/") };
        var service = new MarketplaceUpdateService(new ClientFactory(client), new ArtifactStore(artifact), history, new Importer(), activity);

        var results = await service.CheckAsync();

        var result = Assert.Single(results);
        Assert.Equal("UpdateAvailable", result.Status);
        Assert.Equal("new-sha", result.AvailableCommitSha);
        var saved = Assert.Single(history.Items);
        Assert.Equal("artifact", saved.ArtifactId);
        Assert.Equal("UpdateAvailable", saved.Status);
        Assert.Contains(activity.Items, item => item.EventType == "updates-check");
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
    private sealed class ArtifactStore(MarketplaceArtifact artifact) : IMarketplaceArtifactStore
    {
        public Task SaveImportAsync(IReadOnlyList<MarketplaceArtifactCandidate> candidates, IReadOnlyList<MarketplaceArtifact> artifacts, IReadOnlyList<MarketplaceArtifactScoreSnapshot> scoreSnapshots, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MarketplaceArtifact>> ListArtifactsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketplaceArtifact>>([artifact]);
        public Task<MarketplaceArtifact?> GetArtifactAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<MarketplaceArtifact?>(id == artifact.Id ? artifact : null);
        public Task<MarketplaceArtifact?> GetArtifactByContentHashAsync(string contentHash, MarketplaceArtifactKind kind, CancellationToken cancellationToken = default) => Task.FromResult<MarketplaceArtifact?>(null);
        public Task<IReadOnlyList<MarketplaceArtifactSource>> ListArtifactSourcesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketplaceArtifactSource>>([new(artifact, "https://github.com/owner/repository@old-sha")]);
    }
    private sealed class HistoryStore : IMarketplaceUpdateHistoryStore
    {
        public List<MarketplaceUpdateCheck> Items { get; } = [];
        public Task SaveAsync(IReadOnlyList<MarketplaceUpdateCheck> checks, CancellationToken cancellationToken = default) { Items.AddRange(checks); return Task.CompletedTask; }
        public Task<IReadOnlyList<MarketplaceUpdateCheck>> ListAsync(string? artifactId, int take, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketplaceUpdateCheck>>(Items);
    }
    private sealed class ActivityRecorder : IMarketplaceActivityRecorder
    {
        public List<MarketplaceActivityEvent> Items { get; } = [];
        public Task RecordAsync(MarketplaceActivityEvent activity, CancellationToken cancellationToken = default) { Items.Add(activity); return Task.CompletedTask; }
        public Task<IReadOnlyList<MarketplaceActivityEvent>> ListAsync(int take = 100, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MarketplaceActivityEvent>>(Items);
    }
    private sealed class Importer : IGitHubRepositoryImportService
    {
        public Task<GitHubRepositoryImportResult> ImportAsync(string repositoryUrl, string? reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Check must not import.");
    }
}
