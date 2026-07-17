using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;

public sealed record ProjectDataEvidenceResult(IReadOnlyList<EvidenceItem> Items, IReadOnlyList<string> CapabilityGaps);

/// <summary>Adds IT-confirmed glossary and short-lived, derived runtime configuration state to an Evidence Pack.</summary>
public sealed partial class ProjectDataEvidencePlanner(
    IDomainGlossaryStore glossary,
    IDatabaseRuntimeEvidenceCoordinator runtime)
{
    public async Task<ProjectDataEvidenceResult> BuildAsync(ChangeBrief brief, CancellationToken cancellationToken = default)
    {
        var items = new List<EvidenceItem>();
        var gaps = new List<string>();
        var terms = await glossary.ListAsync(brief.ProjectId, GlossaryProposalStatus.Confirmed, cancellationToken);
        foreach (var term in terms.Where(term => Mentions(brief.OriginalRequest, term)).Take(12))
        {
            items.Add(new EvidenceItem(
                $"glossary:{term.Id}", "DomainTerm", $"{term.Term}：{term.Definition}", EvidenceConfidence.Confirmed,
                "it-confirmed", Symbol: term.Term, Reason: $"由 {term.ReviewedBy ?? "IT"} 確認；敏感度 {term.Sensitivity}", Relevance: 95));
        }

        if (!NeedsRuntimeEvidence(brief.OriginalRequest))
            return new(items, gaps);

        var lookup = BuildLookup(brief.OriginalRequest);
        if (!lookup.HasSelector)
        {
            gaps.Add("問題可能需要即時資料庫設定證據，但未提供可限定範圍的設定 key、namespace 或 feature 名稱；未執行資料庫查詢。");
            return new(items, gaps);
        }

        try
        {
            var statuses = await runtime.GetStatusAsync(brief.ProjectId, cancellationToken: cancellationToken);
            if (statuses.Count == 0)
            {
                gaps.Add("未配置具 read-only Database Runtime capability 的 Wingman Plugin；無法驗證即時設定狀態。");
                return new(items, gaps);
            }

            var exact = !string.IsNullOrWhiteSpace(lookup.Key) && WantsExactState(brief.OriginalRequest);
            var runtimeEvidence = exact
                ? await runtime.ReadConfigurationAsync(brief.ProjectId, lookup, cancellationToken)
                : await runtime.FindConfigurationAsync(brief.ProjectId, lookup, cancellationToken);
            foreach (var evidence in runtimeEvidence.Where(item => !item.IsExpired(DateTimeOffset.UtcNow)).Take(10))
            {
                items.Add(new EvidenceItem(
                    $"runtime:{evidence.PluginId}:{evidence.Id}", "RuntimeConfiguration",
                    $"{evidence.Subject}：{evidence.State}（符合 {evidence.MatchedRecordCount} 筆；值已去識別）",
                    EvidenceConfidence.Exact, "database-runtime-plugin", Symbol: evidence.Subject,
                    Reason: $"observedAt={evidence.ObservedAt:O}; expiresAt={evidence.ExpiresAt:O}; database={evidence.DatabaseIdentity}; redaction={evidence.Redaction}",
                    Relevance: 100));
            }
            if (runtimeEvidence.Count == 0) gaps.Add("Database Runtime Plugin 可用，但本次受限查詢沒有回傳可安全合併的衍生證據。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            gaps.Add("Database Runtime Plugin 查詢失敗或回應不符合安全契約；未將任何即時資料加入 Evidence Pack。");
        }
        return new(items, gaps);
    }

    private static bool Mentions(string request, DomainGlossaryEntry entry) =>
        request.Contains(entry.Term, StringComparison.OrdinalIgnoreCase)
        || entry.Aliases.Any(alias => request.Contains(alias, StringComparison.OrdinalIgnoreCase));

    private static bool NeedsRuntimeEvidence(string text) => RuntimeSignalRegex().IsMatch(text);
    private static bool WantsExactState(string text) => ExactSignalRegex().IsMatch(text);

    private static DatabaseConfigurationLookup BuildLookup(string text)
    {
        var quoted = QuotedValueRegex().Matches(text).Select(match => match.Groups["value"].Value.Trim()).FirstOrDefault(IsSafeSelector);
        var dotted = DottedKeyRegex().Matches(text).Select(match => match.Value).FirstOrDefault(IsSafeSelector);
        var feature = FeatureRegex().Match(text);
        return new DatabaseConfigurationLookup(
            Key: quoted ?? dotted,
            FeatureName: feature.Success ? feature.Groups["feature"].Value : null,
            MaxResults: 10);
    }

    private static bool IsSafeSelector(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '/');

    [GeneratedRegex("(?:目前|現在|環境|production|prod|staging|feature[ -]?flag|設定|配置|啟用|停用|enabled|disabled|runtime)", RegexOptions.IgnoreCase)] private static partial Regex RuntimeSignalRegex();
    [GeneratedRegex("(?:目前值|現在值|是否啟用|是否停用|enabled|disabled|開啟|關閉)", RegexOptions.IgnoreCase)] private static partial Regex ExactSignalRegex();
    [GeneratedRegex("[\"'`](?<value>[^\"'`]{1,200})[\"'`]")] private static partial Regex QuotedValueRegex();
    [GeneratedRegex("\\b[A-Za-z_][A-Za-z0-9_-]{1,80}(?:\\.[A-Za-z0-9_-]{1,80})+\\b")] private static partial Regex DottedKeyRegex();
    [GeneratedRegex("(?:feature|功能|旗標)\\s*[:：]?\\s*(?<feature>[A-Za-z_][A-Za-z0-9_.-]{1,100})", RegexOptions.IgnoreCase)] private static partial Regex FeatureRegex();
}
