using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>
/// 將 Evidence Pack 投影成穩定、可測試的交付計畫。LLM 可解釋此 artifact，
/// 但不得憑空增加已證實修改點。
/// </summary>
public sealed class ChangeImplementationPlanBuilder : IChangeImplementationPlanBuilder
{
    public ChangeImplementationPlan Build(ChangeAnalysisSession session, EvidencePack evidencePack)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(evidencePack);

        var actionable = evidencePack.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.FilePath) || !string.IsNullOrWhiteSpace(item.Symbol))
            .Take(10)
            .ToList();
        var steps = actionable.Select((item, index) => new ChangePlanStep(
            index + 1,
            FormatTarget(item),
            ActionFor(session.Brief.Classification.AnalysisMode, item),
            item.Reason ?? item.Summary,
            item.Confidence,
            [item.Id])).ToList();

        var impacts = evidencePack.Paths.Take(10).Select(path => new ChangeImpactArea(
            path.Kind,
            path.Truncated || path.Confidence is EvidenceConfidence.Heuristic or EvidenceConfidence.Inferred ? "High" : "Medium",
            path.Truncated ? "關係路徑已截斷，實際影響可能更廣。" : $"已解析 {path.NodeIds.Count} 個關聯節點。",
            path.NodeIds)).ToList();

        var risks = new List<string>();
        if (evidencePack.Freshness is not IndexFreshness.Fresh)
            risks.Add($"索引新鮮度為 {evidencePack.Freshness}，修改前必須確認工作區最新狀態。");
        risks.AddRange(evidencePack.CapabilityGaps.Select(gap => $"能力缺口：{gap}"));
        if (evidencePack.Truncated) risks.Add("證據因預算上限遭截斷，需對高風險路徑擴大查詢。");

        var testEvidence = evidencePack.Items.Where(item =>
            item.Kind.Contains("test", StringComparison.OrdinalIgnoreCase) ||
            item.Relation?.Contains("TEST", StringComparison.OrdinalIgnoreCase) == true).Take(10).ToList();
        var tests = testEvidence.Select(item => new ChangeVerificationItem(
            "Automated",
            $"執行或擴充 {item.Summary}",
            [FormatTarget(item)])).ToList();
        if (tests.Count == 0)
            tests.Add(new ChangeVerificationItem("Automated", "為主要修改路徑新增最小回歸測試；目前圖譜未找到可證實的既有測試。", steps.Take(3).Select(step => step.Target).ToList()));
        tests.Add(new ChangeVerificationItem("Manual", "依使用者描述驗證主要成功、失敗與邊界流程。", session.Brief.Targets.Select(target => target.Value).Take(5).ToList()));

        var acceptance = new List<string>
        {
            session.Brief.ExpectedBehavior is { Length: > 0 }
                ? $"預期行為成立：{session.Brief.ExpectedBehavior}"
                : "原始問題或需求的主要流程符合 IT 確認的預期行為。",
            "所有直接相依與高風險間接相依均已驗證，沒有未說明的契約破壞。",
            "自動化測試通過，且完成主要使用者流程的手動驗收。",
        };
        if (session.ClarificationAnswers.TryGetValue("acceptance", out var userAcceptance))
            acceptance.Insert(0, userAcceptance);

        var status = session.Status == ChangeAnalysisSessionStatus.AwaitingClarification
            ? ChangePlanStatus.AwaitingClarification
            : steps.Count == 0 || evidencePack.Freshness is not IndexFreshness.Fresh
                ? ChangePlanStatus.Provisional
                : ChangePlanStatus.Ready;
        return new ChangeImplementationPlan(
            status,
            steps,
            impacts,
            risks.Distinct(StringComparer.Ordinal).ToList(),
            tests,
            acceptance.Distinct(StringComparer.Ordinal).ToList(),
            session.Brief.Unknowns.Concat(session.PendingQuestions.Where(q => q.IsBlocking).Select(q => q.Question)).Distinct(StringComparer.Ordinal).ToList(),
            evidencePack.ManifestVersion);
    }

    private static string FormatTarget(EvidenceItem item) => item.FilePath is { Length: > 0 }
        ? $"{item.FilePath}{(item.StartLine is null ? string.Empty : $":{item.StartLine}")}"
        : item.Symbol ?? item.Summary;

    private static string ActionFor(ChangeAnalysisMode mode, EvidenceItem item) => mode switch
    {
        ChangeAnalysisMode.ProblemLocation => $"驗證 {item.Summary} 是否為根因，確認後修正。",
        ChangeAnalysisMode.ImpactAnalysis => $"檢查並同步調整 {item.Summary}。",
        ChangeAnalysisMode.VerificationAndRegression => $"針對 {item.Summary} 建立或執行回歸驗證。",
        _ => $"在 {item.Summary} 實作所需變更並保留既有契約。",
    };
}
