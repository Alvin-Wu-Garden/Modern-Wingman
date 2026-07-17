using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>以需求類型與缺失欄位產生高決策價值問題；不向使用者提出無法影響設計的泛問。</summary>
public sealed class DeterministicClarificationPlanner : IClarificationPlanner
{
    public IReadOnlyList<ClarificationQuestion> Plan(ChangeBrief brief, int maxQuestions = 10)
    {
        ArgumentNullException.ThrowIfNull(brief);
        if (maxQuestions is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maxQuestions), "澄清問題數量必須介於 1 到 10。");

        var questions = new List<ClarificationQuestion>();
        var targetKinds = brief.Targets.Select(t => t.Kind).ToHashSet();
        var hasConcreteTarget = targetKinds.Any(kind => kind != ChangeTargetKind.NaturalLanguage);

        if (!hasConcreteTarget)
            Add(questions, 1, "受影響的是哪個功能流程、畫面、API、背景工作或使用者角色？", "決定要從哪些入口點與模組開始建立候選範圍。", "scope", true);

        if (brief.Classification.ChangeKind == ChangeKind.Bug)
        {
            if (string.IsNullOrWhiteSpace(brief.ExpectedBehavior))
                Add(questions, 2, "此情境的預期結果與實際結果各是什麼？", "決定是流程錯誤、資料錯誤、權限問題還是呈現問題。", "behavior", true);
            if (!targetKinds.Contains(ChangeTargetKind.ErrorLog))
                Add(questions, 3, "可否提供可重現步驟、發生頻率、時間點，以及完整錯誤訊息或 request/correlation id？", "決定是否優先追查特定執行路徑、設定或外部整合。", "reproduction", true);
            Add(questions, 4, "問題是否只發生在特定環境、租戶、帳號、資料條件或最近部署後？", "決定是否納入環境設定、feature flag、資料遷移與 Git diff 證據。", "environment", false);
        }

        if (brief.Classification.ChangeKind is ChangeKind.NewFeature or ChangeKind.Enhancement)
        {
            Add(questions, 2, "哪些使用者角色可使用此功能？需要新增、修改或保留哪些權限規則？", "決定 API、授權、UI 與驗收範圍。", "authorization", true);
            Add(questions, 3, "新規則是否會改變既有 API、事件 payload、匯入匯出或外部系統契約？", "決定是否需要相容策略、版本化或整合回歸測試。", "contract", true);
            Add(questions, 4, "是否需保存歷史資料、支援回滾、處理既有資料預設值或批次補資料？", "決定資料模型、migration 與營運風險。", "data-lifecycle", true);
            Add(questions, 5, "成功、失敗與邊界情況的可驗收條件是什麼？", "決定測試案例與手動驗收清單。", "acceptance", true);
        }

        if (brief.Classification.AnalysisMode == ChangeAnalysisMode.ImpactAnalysis && !hasConcreteTarget)
            Add(questions, 2, "這次預計修改的目標是哪些檔案、Symbol、Route、設定鍵或 Git diff？", "決定影響分析的精確起點，避免以名稱猜測。", "target", true);

        if (brief.Classification.AnalysisMode == ChangeAnalysisMode.VerificationAndRegression)
            Add(questions, 3, "本次變更最不能回歸的使用者流程與外部整合有哪些？", "決定測試優先順序及必要的手動驗證。", "regression", true);

        if (brief.Constraints.Count == 0)
            Add(questions, 7, "是否有相容版本、停機窗口、效能、安全、法遵或不可修改的既有契約限制？", "決定可行方案與風險等級。", "constraints", false);

        return questions
            .GroupBy(question => question.Question, StringComparer.Ordinal)
            .Select(group => group.OrderBy(question => question.Priority).First())
            .OrderBy(question => question.Priority)
            .ThenBy(question => question.Category, StringComparer.Ordinal)
            .Take(maxQuestions)
            .ToList();
    }

    private static void Add(ICollection<ClarificationQuestion> questions, int priority, string question, string decisionImpact, string category, bool blocking) =>
        questions.Add(new ClarificationQuestion(priority, question, decisionImpact, category, blocking));
}
