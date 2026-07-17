using System.Security.Cryptography;
using System.Text;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace Wingman.Marketplace.Application;

public sealed class MarketplaceDiscoveryClassifier
{
    public const string ProfileId = "wingman-marketplace-classifier/2026-07";

    public (MarketplaceArtifactKind Kind, MarketplaceClassificationConfidence Confidence, string Category, IReadOnlyList<string> SecondaryCategories) Classify(DiscoveryCandidate candidate)
    {
        var text = string.Join(' ', new[]
        {
            candidate.Name,
            candidate.Description ?? string.Empty,
            string.Join(' ', candidate.Topics),
        }).ToLowerInvariant();

        var categories = ClassifyCategories(text);
        if (candidate.KindHint != MarketplaceArtifactKind.Unknown)
            return (candidate.KindHint, MarketplaceClassificationConfidence.Declared, categories.Primary, categories.Secondary);
        if (text.Contains("mcp", StringComparison.Ordinal))
            return (MarketplaceArtifactKind.McpServer, MarketplaceClassificationConfidence.Inferred, categories.Primary == "other" ? "integration-api" : categories.Primary, categories.Secondary);
        if (text.Contains("skill", StringComparison.Ordinal) || text.Contains("agent", StringComparison.Ordinal))
            return (MarketplaceArtifactKind.Skill, MarketplaceClassificationConfidence.Inferred, categories.Primary, categories.Secondary);
        return (MarketplaceArtifactKind.Unknown, MarketplaceClassificationConfidence.Unknown, "other", []);
    }

    private static (string Primary, IReadOnlyList<string> Secondary) ClassifyCategories(string text)
    {
        var matches = CategoryKeywords.Where(pair => pair.Value.Any(keyword => text.Contains(keyword, StringComparison.Ordinal))).Select(pair => pair.Key).Take(4).ToList();
        return matches.Count == 0 ? ("other", []) : (matches[0], matches.Skip(1).Take(3).ToList());
    }

    private static readonly IReadOnlyDictionary<string, string[]> CategoryKeywords = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["software-development"] = ["code", "program", "framework", "frontend", "backend", "mobile"],
        ["code-quality-review"] = ["review", "refactor", "debug", "performance", "lint", "static analysis"],
        ["testing-qa"] = ["test", "qa", "e2e", "quality gate"],
        ["devops-cloud"] = ["deploy", "kubernetes", "docker", "ci/cd", "terraform", "cloud"],
        ["data-databases"] = ["database", "sql", "etl", "analytics", "data"],
        ["integration-api"] = ["api", "webhook", "connector", "integration", "mcp"],
        ["web-browser"] = ["browser", "web automation", "scrap", "crawler"],
        ["search-research"] = ["search", "research", "citation", "literature"],
        ["documentation-knowledge"] = ["document", "documentation", "knowledge", "wiki", "notion", "translation"],
        ["productivity-project"] = ["task", "schedule", "project management", "workflow"],
        ["design-media"] = ["design", "figma", "image", "video", "audio", "presentation"],
        ["security-compliance"] = ["security", "compliance", "vulnerability", "privacy"],
        ["communication-collaboration"] = ["email", "meeting", "chat", "collaboration", "notification"],
        ["business-marketing-sales"] = ["marketing", "sales", "seo", "crm", "customer"],
        ["finance-operations"] = ["finance", "accounting", "procurement", "cost"],
        ["science-education"] = ["science", "education", "learning", "academic"],
        ["ai-agent-automation"] = ["agent", "llm", "prompt", "automation", "orchestration"],
    };
}

public sealed class MarketplaceDiscoveryScorer
{
    public const string ProfileId = "wingman-marketplace-discovery-score/2026-07";

    public MarketplaceScoreSnapshot Score(string discoveryRecordId, DiscoveryCandidate candidate, DateTimeOffset now)
    {
        var components = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["metadataCompleteness"] = ScoreMetadata(candidate),
            ["documentationSignal"] = string.IsNullOrWhiteSpace(candidate.Description) ? 0 : 20,
            ["maintenance"] = ScoreMaintenance(candidate, now),
            ["sourceMaturity"] = candidate.IsArchived ? 0 : 15,
            ["communitySignal"] = Math.Min(15, Math.Log10(Math.Max(1, candidate.Stars + candidate.Forks)) * 5),
            ["freshness"] = ScoreFreshness(candidate.GitHubUpdatedAt, now),
        };
        var evidence = $"{{\"archived\":{candidate.IsArchived.ToString().ToLowerInvariant()},\"stars\":{candidate.Stars},\"forks\":{candidate.Forks}}}";
        return new(
            Guid.NewGuid().ToString("N"),
            discoveryRecordId,
            "discovery",
            ProfileId,
            Math.Round(components.Values.Sum(), 2),
            components,
            evidence,
            now);
    }

    public static string MetadataFingerprint(DiscoveryCandidate candidate)
    {
        var source = string.Join('|', candidate.GitHubNodeId, candidate.CanonicalUrl, candidate.Name,
            candidate.Description, string.Join(',', candidate.Topics), candidate.License, candidate.IsArchived,
            candidate.GitHubUpdatedAt?.ToUnixTimeSeconds(), candidate.PushedAt?.ToUnixTimeSeconds());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static double ScoreMetadata(DiscoveryCandidate candidate)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(candidate.Description)) count++;
        if (candidate.Topics.Count > 0) count++;
        if (!string.IsNullOrWhiteSpace(candidate.License)) count++;
        if (!string.IsNullOrWhiteSpace(candidate.GitHubNodeId)) count++;
        return count * 5;
    }

    private static double ScoreMaintenance(DiscoveryCandidate candidate, DateTimeOffset now)
        => candidate.IsArchived ? 0 : Math.Min(20, ScoreFreshness(candidate.PushedAt ?? candidate.GitHubUpdatedAt, now) * 2);

    private static double ScoreFreshness(DateTimeOffset? updatedAt, DateTimeOffset now)
    {
        if (updatedAt is null) return 0;
        var days = Math.Max(0, (now - updatedAt.Value).TotalDays);
        return Math.Max(0, 10 - (days / 36.5));
    }
}

/// <summary>Static artifact evidence only; this score never claims runtime success or malware safety.</summary>
public sealed class MarketplaceArtifactQualityScorer
{
    public const string ProfileId = "wingman-marketplace-artifact-score/2026-07";

    public MarketplaceArtifactScoreSnapshot Score(MarketplaceArtifact artifact)
    {
        var files = Directory.Exists(artifact.SnapshotPath)
            ? Directory.EnumerateFiles(artifact.SnapshotPath, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasReadme = files.Any(name => name!.StartsWith("README", StringComparison.OrdinalIgnoreCase));
        var hasExamples = files.Any(name => name!.Contains("example", StringComparison.OrdinalIgnoreCase) || name.Contains("sample", StringComparison.OrdinalIgnoreCase));
        var hasLicense = files.Any(name => name!.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase));
        var complete = artifact.Status == MarketplaceDiscoveryStatus.Resolved;
        var components = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["formatCompleteness"] = complete ? 20 : 5,
            ["documentationClarity"] = hasReadme ? 15 : 3,
            ["installabilityEvidence"] = complete ? 20 : 0,
            ["maintenance"] = 7.5,
            ["examples"] = hasExamples ? 10 : 0,
            ["compatibilityEvidence"] = complete ? 10 : 0,
            ["sourceMaturity"] = hasLicense ? 10 : 4,
        };
        var evidence = $"{{\"hasReadme\":{hasReadme.ToString().ToLowerInvariant()},\"hasExamples\":{hasExamples.ToString().ToLowerInvariant()},\"hasLicense\":{hasLicense.ToString().ToLowerInvariant()},\"resolved\":{complete.ToString().ToLowerInvariant()}}}";
        return new(Guid.NewGuid().ToString("N"), artifact.Id, ProfileId, Math.Round(components.Values.Sum(), 2), components, evidence, DateTimeOffset.UtcNow);
    }
}
