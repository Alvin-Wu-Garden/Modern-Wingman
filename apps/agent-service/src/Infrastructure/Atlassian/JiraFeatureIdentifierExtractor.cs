using System.Collections.Generic;
using System.Text.RegularExpressions;
using AgentService.Application.Atlassian;

namespace AgentService.Infrastructure.Atlassian;

public sealed class JiraFeatureIdentifierExtractor
{
    private static readonly Regex CodeNameRegex = new(
        "(?<![A-Za-z0-9])(?<code>\\d{4,6})\\s*[-_－—]\\s*(?<name>[\\p{IsCJKUnifiedIdeographs}A-Za-z0-9()/_\\-]{2,24})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CodeOnlyRegex = new(
        "(?<![A-Za-z0-9])(?<code>\\d{4,6})(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JiraKeyRegex = new(
        "\\b[A-Z][A-Z0-9]+-\\d+\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] FeatureKeywords =
    [
        "功能", "作業", "頁", "頁面", "交易", "流程", "維護", "查詢", "報表", "上傳", "下載", "放行", "核准", "申請", "覆核", "轉檔", "批次", "menu", "feature",
    ];

    private static readonly string[] NegativeKeywords =
    [
        "jira", "issue", "ticket", "版本", "version", "build", "hotfix", "需求單", "page id", "頁碼", "頁次", "release", "sprint",
    ];

    public IReadOnlyList<JiraFeatureIdentifier> Extract(NormalizedJiraIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        var candidates = new List<Candidate>();

        AddTextCandidates(candidates, issue.Preview.Summary, JiraFeatureSourceType.Summary, "summary", 0.95);
        AddTextCandidates(candidates, issue.DescriptionMarkdown, JiraFeatureSourceType.Description, "description", 0.85);

        foreach (var kvp in issue.ClassifiedFields)
        {
            var fieldWeight = IsPriorityClassifiedField(kvp.Key) ? 0.82 : 0.72;
            AddTextCandidates(candidates, kvp.Value, JiraFeatureSourceType.ClassifiedField, $"field:{kvp.Key}", fieldWeight);
        }

        foreach (var component in issue.Components)
        {
            AddTextCandidates(candidates, component, JiraFeatureSourceType.Component, "component", 0.70);
        }

        foreach (var linkedIssue in issue.LinkedIssues)
        {
            AddTextCandidates(candidates, linkedIssue.Summary, JiraFeatureSourceType.LinkedIssue, "linkedIssue", 0.62);
        }

        foreach (var comment in issue.Comments)
        {
            AddTextCandidates(candidates, comment.Body, JiraFeatureSourceType.Comment, "comment", 0.55);
        }

        foreach (var attachment in issue.Attachments)
        {
            AddTextCandidates(candidates, attachment.Filename, JiraFeatureSourceType.Attachment, "attachment", 0.40);
        }

        return MergeCandidates(candidates);
    }

    private static bool IsPriorityClassifiedField(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim().ToLowerInvariant();
        return normalized.Contains("分析", StringComparison.Ordinal)
            || normalized.Contains("步驟", StringComparison.Ordinal)
            || normalized.Contains("scenario", StringComparison.Ordinal)
            || normalized.Contains("test", StringComparison.Ordinal)
            || normalized.Contains("case", StringComparison.Ordinal);
    }

    private static void AddTextCandidates(
        List<Candidate> sink,
        string? text,
        JiraFeatureSourceType sourceType,
        string sourceReference,
        double baseConfidence)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var content = text.Trim();
        var codeNameRanges = new List<(int Start, int End)>();

        foreach (Match match in CodeNameRegex.Matches(content))
        {
            var code = match.Groups["code"].Value;
            var name = CleanupName(match.Groups["name"].Value);
            if (!IsValidCode(code) || IsExcluded(content, match.Index, code) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var confidence = Math.Clamp(baseConfidence + 0.15, 0.0, 0.99);
            sink.Add(new Candidate(code, name, sourceType, sourceReference, confidence, match.Value));
            codeNameRanges.Add((match.Index, match.Index + match.Length));
        }

        foreach (Match match in CodeOnlyRegex.Matches(content))
        {
            var code = match.Groups["code"].Value;
            if (codeNameRanges.Any(range => match.Index >= range.Start && match.Index < range.End))
            {
                continue;
            }

            if (!IsValidCode(code) || IsExcluded(content, match.Index, code))
            {
                continue;
            }

            var context = GetContext(content, match.Index, match.Length);
            if (!ContainsAnyKeyword(context, FeatureKeywords) || ContainsAnyKeyword(context, NegativeKeywords))
            {
                continue;
            }

            var confidence = Math.Clamp(baseConfidence - 0.08, 0.0, 0.85);
            sink.Add(new Candidate(code, null, sourceType, sourceReference, confidence, context));
        }
    }

    private static bool IsValidCode(string code)
    {
        if (!int.TryParse(code, out var number))
        {
            return false;
        }

        if (number is >= 1900 and <= 2100)
        {
            return false;
        }

        return number is >= 1000 and <= 999999;
    }

    private static bool IsExcluded(string content, int index, string code)
    {
        if (JiraKeyRegex.IsMatch(content))
        {
            var keyMatch = JiraKeyRegex.Matches(content)
                .Cast<Match>()
                .FirstOrDefault(m => index >= m.Index && index < m.Index + m.Length);
            if (keyMatch is not null)
            {
                return true;
            }
        }

        var context = GetContext(content, index, code.Length).ToLowerInvariant();
        if (ContainsAnyKeyword(context, NegativeKeywords))
        {
            return true;
        }

        // 常見日期片段，例如 2024-12-31。
        if (Regex.IsMatch(context, "\\b\\d{4}[-/]\\d{1,2}[-/]\\d{1,2}\\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return false;
    }

    private static string CleanupName(string raw)
    {
        var name = raw.Trim().Trim('-', '_', '.', ',', '，', '。', ')', '(', '[', ']');
        return name.Length > 40 ? name[..40] : name;
    }

    private static bool ContainsAnyKeyword(string text, IEnumerable<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetContext(string text, int index, int length)
    {
        var start = Math.Max(0, index - 28);
        var end = Math.Min(text.Length, index + length + 28);
        return text[start..end];
    }

    private static IReadOnlyList<JiraFeatureIdentifier> MergeCandidates(List<Candidate> candidates)
    {
        return candidates
            .GroupBy(c => BuildKey(c.FeatureCode, c.FeatureName), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var best = group
                    .OrderByDescending(x => x.Confidence)
                    .ThenBy(x => SourcePriority(x.SourceType))
                    .First();

                var occurrence = group.Count();
                var confidence = Math.Clamp(best.Confidence + (occurrence - 1) * 0.03, 0.0, 0.99);
                var evidence = string.Join(" | ", group.Select(x => x.Evidence).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
                var isConfirmed = confidence >= 0.78 || occurrence >= 2;

                return new JiraFeatureIdentifier(
                    best.FeatureCode,
                    best.FeatureName,
                    best.SourceType,
                    best.SourceReference,
                    confidence,
                    evidence,
                    occurrence,
                    isConfirmed);
            })
            .OrderByDescending(x => x.Confidence)
            .ThenByDescending(x => x.OccurrenceCount)
            .ToList();
    }

    private static string BuildKey(string? code, string? name)
    {
        var codePart = string.IsNullOrWhiteSpace(code) ? "" : code.Trim();
        var namePart = string.IsNullOrWhiteSpace(name) ? "" : name.Trim().ToLowerInvariant();
        return $"{codePart}|{namePart}";
    }

    private static int SourcePriority(JiraFeatureSourceType sourceType) => sourceType switch
    {
        JiraFeatureSourceType.Summary => 0,
        JiraFeatureSourceType.Description => 1,
        JiraFeatureSourceType.ClassifiedField => 2,
        JiraFeatureSourceType.Component => 3,
        JiraFeatureSourceType.LinkedIssue => 4,
        JiraFeatureSourceType.Comment => 5,
        JiraFeatureSourceType.Attachment => 6,
        _ => 99,
    };

    private sealed record Candidate(
        string? FeatureCode,
        string? FeatureName,
        JiraFeatureSourceType SourceType,
        string SourceReference,
        double Confidence,
        string Evidence);
}