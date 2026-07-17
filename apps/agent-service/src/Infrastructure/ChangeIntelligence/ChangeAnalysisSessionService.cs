using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>
/// 將多次使用者回答收斂到同一份 Change Brief。這個服務只做 deterministic merge，
/// 不讓 LLM 修改已確認的限制或將推論升級成事實。
/// </summary>
public sealed class ChangeAnalysisSessionService(
    IChangeAnalysisSessionStore store,
    IChangeBriefBuilder briefBuilder,
    IClarificationPlanner clarificationPlanner) : IChangeAnalysisSessionService
{
    public async Task<ChangeAnalysisSession> StartOrContinueAsync(
        string projectId,
        string request,
        IReadOnlyList<ChangeTarget>? targets,
        string? sessionId,
        IReadOnlyList<ClarificationAnswer>? answers,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        if (request.Length > 200_000)
            throw new ArgumentException("分析描述不得超過 200,000 字元。", nameof(request));
        if (sessionId is { Length: > 80 })
            throw new ArgumentException("分析 session id 無效。", nameof(sessionId));
        if ((targets?.Count ?? 0) > 20)
            throw new ArgumentException("單次分析最多提供 20 個目標。", nameof(targets));
        if ((answers?.Count ?? 0) > 10 || (answers?.Any(answer => answer.Answer.Length > 20_000) ?? false))
            throw new ArgumentException("單次最多提交 10 個澄清答案，且每個答案不得超過 20,000 字元。", nameof(answers));

        var existing = string.IsNullOrWhiteSpace(sessionId) ? null : await store.GetAsync(sessionId, ct);
        if (existing is not null && !string.Equals(existing.ProjectId, projectId, StringComparison.Ordinal))
            throw new InvalidOperationException("此分析 session 不屬於指定專案。");

        var now = DateTimeOffset.UtcNow;
        var id = existing?.Id ?? Guid.NewGuid().ToString("N");
        var originalRequest = existing?.Brief.OriginalRequest ?? request.Trim();
        var mergedTargets = MergeTargets(existing?.Brief.Targets, targets);
        var normalizedAnswers = MergeAnswers(existing?.ClarificationAnswers, answers);

        // target 類答案也要進入 target detector，讓自由文字中的 file/route/symbol/error log
        // 轉成結構化目標，而不是只保存成普通備註。
        if (normalizedAnswers.TryGetValue("target", out var targetAnswer))
            mergedTargets = MergeTargets(mergedTargets, briefBuilder.Build(projectId, targetAnswer, mergedTargets).Targets);

        var brief = briefBuilder.Build(projectId, originalRequest, mergedTargets);
        brief = ApplyAnswers(brief, normalizedAnswers);
        var pending = clarificationPlanner.Plan(brief)
            .Where(question => !normalizedAnswers.ContainsKey(question.Category))
            .ToList();
        var status = pending.Any(question => question.IsBlocking)
            ? ChangeAnalysisSessionStatus.AwaitingClarification
            : ChangeAnalysisSessionStatus.ReadyForAnalysis;

        var session = new ChangeAnalysisSession(
            id,
            projectId,
            brief,
            normalizedAnswers,
            pending,
            status,
            existing?.CreatedAt ?? now,
            now);
        await store.SaveAsync(session, ct);
        return session;
    }

    public async Task CompleteAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = await store.GetAsync(sessionId, ct);
        if (session is null) return;
        await store.SaveAsync(session with
        {
            Status = ChangeAnalysisSessionStatus.Completed,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    private static List<ChangeTarget> MergeTargets(
        IReadOnlyList<ChangeTarget>? first,
        IReadOnlyList<ChangeTarget>? second) =>
        (first ?? []).Concat(second ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target.Value))
            .Select(target => ValidateTarget(target))
            .GroupBy(target => $"{target.Kind}:{target.Value.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { Value = group.First().Value.Trim() })
            .ToList();

    private static ChangeTarget ValidateTarget(ChangeTarget target)
    {
        var limit = target.Kind is ChangeTargetKind.GitDiff or ChangeTargetKind.ErrorLog ? 200_000 : 8_000;
        if (target.Value.Length > limit)
            throw new ArgumentException($"{target.Kind} 分析目標超過 {limit:N0} 字元上限。", nameof(target));
        if ((target.StartLine is null) != (target.EndLine is null)
            || target.StartLine is < 1
            || target.EndLine is < 1
            || target.StartLine > target.EndLine)
            throw new ArgumentException("分析目標行號範圍無效。", nameof(target));
        return target;
    }

    private static Dictionary<string, string> MergeAnswers(
        IReadOnlyDictionary<string, string>? existing,
        IReadOnlyList<ClarificationAnswer>? supplied)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in existing ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                result[pair.Key.Trim()] = pair.Value.Trim();
        foreach (var answer in supplied ?? [])
            if (!string.IsNullOrWhiteSpace(answer.Category) && !string.IsNullOrWhiteSpace(answer.Answer))
                result[answer.Category.Trim()] = answer.Answer.Trim();
        return result;
    }

    private static ChangeBrief ApplyAnswers(ChangeBrief brief, IReadOnlyDictionary<string, string> answers)
    {
        var expected = answers.GetValueOrDefault("behavior") ?? brief.ExpectedBehavior;
        var constraints = brief.Constraints.Concat(GetAnswers(answers, "constraints", "contract", "data-lifecycle"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var boundaries = brief.KnownBoundaries.Concat(GetAnswers(answers, "scope", "authorization", "environment", "regression", "reproduction"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidateAreas = brief.CandidateAreas.Concat(
                brief.Targets.Where(target => target.Kind is not ChangeTargetKind.NaturalLanguage and not ChangeTargetKind.ErrorLog)
                    .Select(target => target.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var unknowns = brief.Unknowns
            .Where(unknown => expected is null || !unknown.Contains("預期行為", StringComparison.Ordinal))
            .Where(unknown => candidateAreas.Count == 0 || !unknown.Contains("尚未提供可定位", StringComparison.Ordinal))
            .ToList();

        return brief with
        {
            ExpectedBehavior = expected,
            Constraints = constraints,
            KnownBoundaries = boundaries,
            CandidateAreas = candidateAreas,
            Unknowns = unknowns,
        };
    }

    private static IEnumerable<string> GetAnswers(IReadOnlyDictionary<string, string> answers, params string[] categories) =>
        categories.Where(answers.ContainsKey).Select(category => $"{category}: {answers[category]}");
}
