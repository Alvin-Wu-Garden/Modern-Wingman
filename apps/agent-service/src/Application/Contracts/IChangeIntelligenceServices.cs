using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

/// <summary>以可重現規則將專案問題路由至分析模式，不執行 LLM 呼叫。</summary>
public interface IChangeIntentClassifier
{
    ChangeIntentClassification Classify(string request, IReadOnlyList<ChangeTarget>? suppliedTargets = null);
}

/// <summary>把原始輸入與明確目標正規化成 Change Brief。</summary>
public interface IChangeBriefBuilder
{
    ChangeBrief Build(string projectId, string request, IReadOnlyList<ChangeTarget>? suppliedTargets = null);
}

/// <summary>依已知資訊挑選最多十個會改變設計決策的澄清問題。</summary>
public interface IClarificationPlanner
{
    IReadOnlyList<ClarificationQuestion> Plan(ChangeBrief brief, int maxQuestions = 10);
}

/// <summary>把各 evidence provider 的結果壓縮成可安全傳入 Agent 的結構化上下文。</summary>
public interface IEvidencePackBuilder
{
    EvidencePack Build(EvidencePackRequest request);
}

public interface IChangeAnalysisSessionStore
{
    Task<ChangeAnalysisSession?> GetAsync(string sessionId, CancellationToken ct = default);
    Task SaveAsync(ChangeAnalysisSession session, CancellationToken ct = default);
}

public interface IChangeAnalysisSessionService
{
    Task<ChangeAnalysisSession> StartOrContinueAsync(
        string projectId,
        string request,
        IReadOnlyList<ChangeTarget>? targets,
        string? sessionId,
        IReadOnlyList<ClarificationAnswer>? answers,
        CancellationToken ct = default);

    Task CompleteAsync(string sessionId, CancellationToken ct = default);
}

public interface IChangeImplementationPlanBuilder
{
    ChangeImplementationPlan Build(
        ChangeAnalysisSession session,
        EvidencePack evidencePack);
}
