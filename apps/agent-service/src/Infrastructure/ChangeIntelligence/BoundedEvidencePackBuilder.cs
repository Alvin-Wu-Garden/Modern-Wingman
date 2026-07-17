using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>
/// 對所有 provider 一視同仁地排序、去重、截斷 evidence。它不負責查圖譜，因此可在 P0/P1/P3
/// 分階段接入 caller、route、test、Git 與 runtime evidence，而不改變回答層契約。
/// </summary>
public sealed class BoundedEvidencePackBuilder : IEvidencePackBuilder
{
    public EvidencePack Build(EvidencePackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxItems is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(request.MaxItems), "Evidence 項目上限必須介於 1 到 200。");
        if (request.MaxExcerptCharacters is < 0 or > 20_000)
            throw new ArgumentOutOfRangeException(nameof(request.MaxExcerptCharacters));

        var normalized = request.Evidence
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Summary))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.Relevance)
                .ThenBy(item => ConfidenceRank(item.Confidence))
                .ThenBy(item => item.FilePath, StringComparer.Ordinal)
                .First())
            .OrderByDescending(item => item.Relevance)
            .ThenBy(item => ConfidenceRank(item.Confidence))
            .ThenBy(item => item.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine)
            .ToList();

        var truncated = normalized.Count > request.MaxItems;
        var items = normalized
            .Take(request.MaxItems)
            .Select(item => item with { Excerpt = Truncate(item.Excerpt, request.MaxExcerptCharacters) })
            .ToList();

        var itemIds = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var paths = (request.Paths ?? [])
            .Where(path => path.NodeIds.Count > 0 && path.NodeIds.Any(itemIds.Contains))
            .Select(path => path with { NodeIds = path.NodeIds.Where(itemIds.Contains).ToList(), Truncated = path.Truncated || path.NodeIds.Any(id => !itemIds.Contains(id)) })
            .Where(path => path.NodeIds.Count > 0)
            .ToList();

        return new EvidencePack(
            request.Brief,
            items,
            paths,
            request.Freshness,
            request.ManifestVersion,
            (request.CapabilityGaps ?? []).Where(gap => !string.IsNullOrWhiteSpace(gap)).Distinct(StringComparer.Ordinal).ToList(),
            truncated || paths.Any(path => path.Truncated));
    }

    private static int ConfidenceRank(EvidenceConfidence confidence) => confidence switch
    {
        EvidenceConfidence.Confirmed => 0,
        EvidenceConfidence.Exact => 1,
        EvidenceConfidence.Resolved => 2,
        EvidenceConfidence.Heuristic => 3,
        EvidenceConfidence.Inferred => 4,
        _ => 5,
    };

    private static string? Truncate(string? value, int maxCharacters)
    {
        if (value is null || value.Length <= maxCharacters)
            return value;
        return maxCharacters == 0 ? null : string.Concat(value.AsSpan(0, maxCharacters), "…");
    }
}
