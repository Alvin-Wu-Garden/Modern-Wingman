using System.Text.RegularExpressions;

namespace AgentService.Application.Atlassian;

public sealed record JiraGraphRagQueries(
    string LocalQuery,
    string GlobalQuery,
    IReadOnlyList<string> FeatureCodes,
    IReadOnlyList<string> FeatureNames,
    IReadOnlyList<string> EvidenceTokens);

public sealed class JiraGraphRagQueryBuilder
{
    private static readonly Regex NonWordRegex = new(
        "[^\\p{IsCJKUnifiedIdeographs}A-Za-z0-9_\\- ]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> StopWords =
    [
        "請", "協助", "修正", "問題", "異常", "無法", "發生", "造成", "與", "及", "和", "the", "and", "for", "bug", "jira",
    ];

    public JiraGraphRagQueries Build(
        NormalizedJiraIssue issue,
        IReadOnlyList<JiraFeatureIdentifier> identifiers)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(identifiers);

        var selected = identifiers
            .Where(x => x.IsConfirmed || x.Confidence >= 0.72)
            .OrderByDescending(x => x.Confidence)
            .ThenByDescending(x => x.OccurrenceCount)
            .Take(6)
            .ToList();

        var featureCodes = selected
            .Select(x => x.FeatureCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        var featureNames = selected
            .Select(x => x.FeatureName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Select(CleanToken)
            .Where(x => x.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        var evidenceTokens = new List<string>();
        foreach (var id in selected)
        {
            foreach (var token in SplitTokens(id.Evidence))
            {
                if (token.Length < 2 || StopWords.Contains(token))
                {
                    continue;
                }

                if (!evidenceTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    evidenceTokens.Add(token);
                }

                if (evidenceTokens.Count >= 12)
                {
                    break;
                }
            }

            if (evidenceTokens.Count >= 12)
            {
                break;
            }
        }

        var summaryTokens = SplitTokens(issue.Preview.Summary)
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var localSegments = new List<string>();
        if (featureCodes.Count > 0)
        {
            localSegments.Add(string.Join(' ', featureCodes));
        }

        if (featureNames.Count > 0)
        {
            localSegments.Add(string.Join(' ', featureNames));
        }

        if (summaryTokens.Count > 0)
        {
            localSegments.Add(string.Join(' ', summaryTokens));
        }

        if (evidenceTokens.Count > 0)
        {
            localSegments.Add(string.Join(' ', evidenceTokens.Take(6)));
        }

        localSegments.Add("ControllerAction Handles RoutesTo");

        var globalSegments = new List<string>();
        if (summaryTokens.Count > 0)
        {
            globalSegments.Add(string.Join(' ', summaryTokens));
        }

        if (featureNames.Count > 0)
        {
            globalSegments.Add(string.Join(' ', featureNames.Take(4)));
        }

        globalSegments.Add("impact root-cause dependency risk");

        return new JiraGraphRagQueries(
            LimitLength(string.Join(' ', localSegments), 220),
            LimitLength(string.Join(' ', globalSegments), 180),
            featureCodes,
            featureNames,
            evidenceTokens);
    }

    private static IEnumerable<string> SplitTokens(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return NonWordRegex
            .Replace(input, " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanToken)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string CleanToken(string token)
    {
        return token.Trim().Trim('-', '_').ToLowerInvariant();
    }

    private static string LimitLength(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = value[..maxLength];
        var lastSpace = cut.LastIndexOf(' ');
        return lastSpace > 0 ? cut[..lastSpace] : cut;
    }
}