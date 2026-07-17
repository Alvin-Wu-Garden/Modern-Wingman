using Wingman.Marketplace.Application;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.UnitTests.Marketplace;

public sealed class MarketplaceDiscoveryScorerTests
{
    [Fact]
    public void Score_IsDeterministicAndSeparatesDiscoveryEvidence()
    {
        var candidate = new DiscoveryCandidate(
            "R_kgDOExample",
            "https://github.com/owner/example",
            "owner",
            "example",
            "example",
            "An MCP skill with documentation",
            ["mcp", "agent-skill"],
            "MIT",
            false,
            100,
            10,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            MarketplaceArtifactKind.McpServer);
        var now = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

        var result = new MarketplaceDiscoveryScorer().Score("record-1", candidate, now);

        Assert.Equal(MarketplaceDiscoveryScorer.ProfileId, result.ProfileId);
        Assert.Equal("discovery", result.ScoreKind);
        Assert.InRange(result.TotalScore, 0, 100);
        Assert.Contains("maintenance", result.Components.Keys);
        Assert.Equal(
            MarketplaceDiscoveryScorer.MetadataFingerprint(candidate),
            MarketplaceDiscoveryScorer.MetadataFingerprint(candidate));
    }

    [Theory]
    [InlineData("topic:mcp", MarketplaceArtifactKind.McpServer)]
    [InlineData("agent skill", MarketplaceArtifactKind.Skill)]
    public void Classifier_UsesHintsAndEvidence(string description, MarketplaceArtifactKind expected)
    {
        var candidate = new DiscoveryCandidate(null, "https://github.com/o/r", "o", "r", "r", description,
            [], null, false, 0, 0, null, null, MarketplaceArtifactKind.Unknown);
        var result = new MarketplaceDiscoveryClassifier().Classify(candidate);
        Assert.Equal(expected, result.Kind);
        Assert.Equal(MarketplaceClassificationConfidence.Inferred, result.Confidence);
    }
}
