namespace AgentService.Application.Models;

/// <summary>使用者變更描述的主要性質。此值只描述需求，不代表已驗證的根因或修改點。</summary>
public enum ChangeKind
{
    Unknown,
    Bug,
    NewFeature,
    Enhancement,
    Refactor,
    RiskAssessment,
}

/// <summary>專案解析要執行的工作模式。</summary>
public enum ChangeAnalysisMode
{
    Unknown,
    ProblemLocation,
    ChangePlacement,
    ImpactAnalysis,
    ImplementationPlanning,
    VerificationAndRegression,
}

/// <summary>使用者提供的可定位目標種類。</summary>
public enum ChangeTargetKind
{
    NaturalLanguage,
    File,
    Symbol,
    Route,
    GitDiff,
    ErrorLog,
}

/// <summary>規則型分類的可信度；不使用 LLM 時不得宣稱高於明確訊號。</summary>
public enum ClassificationConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>證據的可信度標記，供後續 Agent 回答明確區分事實與推論。</summary>
public enum EvidenceConfidence
{
    Confirmed,
    Exact,
    Resolved,
    Heuristic,
    Inferred,
    Unknown,
}

/// <summary>索引快照可用性。P0 的 manifest 實作完成後會映射到此模型。</summary>
public enum IndexFreshness
{
    Unknown,
    Fresh,
    PendingChanges,
    Indexing,
    Partial,
    Failed,
    Stale,
}

public sealed record ChangeTarget(
    ChangeTargetKind Kind,
    string Value,
    string? Source = null,
    int? StartLine = null,
    int? EndLine = null);

/// <summary>分類結果包含規則命中的理由，以便 UI 與測試可診斷。</summary>
public sealed record ChangeIntentClassification(
    ChangeKind ChangeKind,
    ChangeAnalysisMode AnalysisMode,
    ClassificationConfidence Confidence,
    IReadOnlyList<string> Signals,
    bool IsProjectScoped);

/// <summary>
/// 不受 LLM 控制的需求中介表示。候選修改點與未知項必須在取得圖譜證據後才可升級為事實。
/// </summary>
public sealed record ChangeBrief(
    string ProjectId,
    string OriginalRequest,
    ChangeIntentClassification Classification,
    IReadOnlyList<ChangeTarget> Targets,
    string? Symptom,
    string? ExpectedBehavior,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> KnownBoundaries,
    IReadOnlyList<string> CandidateAreas,
    IReadOnlyList<string> Unknowns);

public sealed record ClarificationQuestion(
    int Priority,
    string Question,
    string DecisionImpact,
    string Category,
    bool IsBlocking);

/// <summary>IT 對澄清問題的結構化回答；Category 必須對應問題的 Category。</summary>
public sealed record ClarificationAnswer(string Category, string Answer);

public enum ChangeAnalysisSessionStatus
{
    AwaitingClarification,
    ReadyForAnalysis,
    Completed,
}

/// <summary>
/// 跨多次 API 呼叫持續收斂的變更分析狀態。回答只保存使用者輸入與結構化 Brief，
/// 不保存 LLM prompt、secret 或 runtime database value。
/// </summary>
public sealed record ChangeAnalysisSession(
    string Id,
    string ProjectId,
    ChangeBrief Brief,
    IReadOnlyDictionary<string, string> ClarificationAnswers,
    IReadOnlyList<ClarificationQuestion> PendingQuestions,
    ChangeAnalysisSessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum ChangePlanStatus
{
    AwaitingClarification,
    Provisional,
    Ready,
}

public sealed record ChangePlanStep(
    int Order,
    string Target,
    string Action,
    string Rationale,
    EvidenceConfidence Confidence,
    IReadOnlyList<string> EvidenceIds);

public sealed record ChangeImpactArea(
    string Scope,
    string RiskLevel,
    string Description,
    IReadOnlyList<string> EvidenceIds);

public sealed record ChangeVerificationItem(
    string Kind,
    string Description,
    IReadOnlyList<string> RelatedTargets);

/// <summary>可交付給開發人員的 deterministic 變更計畫骨架。</summary>
public sealed record ChangeImplementationPlan(
    ChangePlanStatus Status,
    IReadOnlyList<ChangePlanStep> ModificationSteps,
    IReadOnlyList<ChangeImpactArea> ImpactAreas,
    IReadOnlyList<string> Risks,
    IReadOnlyList<ChangeVerificationItem> Tests,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Unknowns,
    string? ManifestVersion);

/// <summary>圖譜、原始碼、Git 或未來 Runtime plugin 傳入的標準化證據。</summary>
public sealed record EvidenceItem(
    string Id,
    string Kind,
    string Summary,
    EvidenceConfidence Confidence,
    string SourceKind,
    string? FilePath = null,
    int? StartLine = null,
    int? EndLine = null,
    string? Symbol = null,
    string? Relation = null,
    string? Excerpt = null,
    string? Reason = null,
    int Relevance = 0);

public sealed record EvidencePath(
    string Kind,
    IReadOnlyList<string> NodeIds,
    EvidenceConfidence Confidence,
    bool Truncated = false);

public sealed record EvidencePackRequest(
    ChangeBrief Brief,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<EvidencePath>? Paths = null,
    IndexFreshness Freshness = IndexFreshness.Unknown,
    string? ManifestVersion = null,
    int MaxItems = 40,
    int MaxExcerptCharacters = 1200,
    IReadOnlyList<string>? CapabilityGaps = null);

/// <summary>
/// 提供回答層的有界、可追溯上下文。它不含未經 redaction 的 runtime secret 或資料列。
/// </summary>
public sealed record EvidencePack(
    ChangeBrief Brief,
    IReadOnlyList<EvidenceItem> Items,
    IReadOnlyList<EvidencePath> Paths,
    IndexFreshness Freshness,
    string? ManifestVersion,
    IReadOnlyList<string> CapabilityGaps,
    bool Truncated);

/// <summary>
/// P3 Database Runtime Plugin 可宣告的唯讀能力。這是能力契約，不代表 Wingman
/// 內建任何特定資料庫的 driver 或 SQL dialect。
/// </summary>
public enum DatabaseRuntimeCapability
{
    InspectSchema,
    FindConfiguration,
    ReadConfiguration,
    ValidateQueryPlan,
    ExecuteReadOnlyQuery,
}

/// <summary>Runtime evidence 的資料可見性。任何原始設定值與資料列都不得進入此模型。</summary>
public enum RuntimeEvidenceRedaction
{
    NotApplicable,
    DerivedOnly,
    Redacted,
}

/// <summary>不攜帶原始值的設定／查詢結果狀態。</summary>
public enum RuntimeEvidenceState
{
    Unknown,
    Present,
    Absent,
    MatchesExpected,
    DoesNotMatchExpected,
    Enabled,
    Disabled,
}

/// <summary>
/// 可安全併入 Evidence Pack 的即時資料庫證據。模型刻意沒有 Value、row 或 connection string
/// 欄位；Plugin 必須在其邊界完成 redaction，只回傳衍生狀態。
/// </summary>
public sealed record RuntimeEvidence(
    string Id,
    string PluginId,
    string DatabaseIdentity,
    string Capability,
    string Subject,
    RuntimeEvidenceState State,
    RuntimeEvidenceRedaction Redaction,
    DateTimeOffset ObservedAt,
    DateTimeOffset ExpiresAt,
    int MatchedRecordCount = 0,
    DateTimeOffset? SourceUpdatedAt = null)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

/// <summary>結構化設定查詢；不提供自由 SQL，也不包含任何資料庫認證資訊。</summary>
public sealed record DatabaseConfigurationLookup(
    string? Key = null,
    string? Namespace = null,
    string? FeatureName = null,
    string? Table = null,
    string? Column = null,
    string? Environment = null,
    string? TenantScope = null,
    int MaxResults = 10)
{
    public bool HasSelector => !string.IsNullOrWhiteSpace(Key)
        || !string.IsNullOrWhiteSpace(Namespace)
        || !string.IsNullOrWhiteSpace(FeatureName)
        || (!string.IsNullOrWhiteSpace(Table) && !string.IsNullOrWhiteSpace(Column));
}

/// <summary>由 Plugin 實作的 schema metadata 查詢條件；不讀取資料列。</summary>
public sealed record DatabaseSchemaInspectionRequest(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<string> ObjectNames,
    int MaxResults = 100);

/// <summary>資料庫 fallback SQL 可引用的已核准物件與欄位。</summary>
public sealed record DatabaseQueryObjectAllowlist(
    string Schema,
    string ObjectName,
    IReadOnlySet<string> AllowedColumns);

/// <summary>只有名稱的參數宣告；實際綁定值留在 Plugin 的短生命週期執行邊界。</summary>
public sealed record DatabaseQueryParameter(string Name);

/// <summary>
/// 受限的 fallback query 計畫。實際 driver 必須在 read-only transaction 中執行並再次套用
/// object/column allowlist、timeout 與 row limit；此模型不得被持久化或寫入診斷 log。
/// </summary>
public sealed record DatabaseReadOnlyQueryPlan(
    string Statement,
    IReadOnlyList<DatabaseQueryParameter> Parameters,
    IReadOnlyList<DatabaseQueryObjectAllowlist> ObjectAllowlist,
    int RowLimit,
    TimeSpan Timeout,
    int MaxResultBytes = 65_536);

public sealed record DatabaseQueryPlanValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ReferencedObjects,
    IReadOnlyList<string> ReferencedParameters);

public sealed record DatabaseRuntimeRequestValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors);
