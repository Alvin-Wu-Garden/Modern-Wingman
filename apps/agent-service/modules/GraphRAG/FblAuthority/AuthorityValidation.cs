using System.Text.RegularExpressions;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>表示一筆可供人工審閱的 Preflight 訊息。</summary>
public sealed record PreflightIssue(
    PreflightSeverity Severity,
    PreflightReasonCode ReasonCode,
    string Message,
    string? MenuId = null,
    string? FromKey = null,
    string? TargetText = null,
    string? SourceFile = null,
    int? SourceLine = null,
    IReadOnlyList<string>? Candidates = null);

/// <summary>
/// 彙整單次 GraphDocument 的驗證結果與發布資格。
/// 建構式只對本組件開放，避免呼叫端自行偽造零錯誤結果。
/// </summary>
public sealed class PreflightResult
{
    /// <summary>由正式 Validator 建立指定 run 的驗證結果。</summary>
    internal PreflightResult(string runId, IReadOnlyList<PreflightIssue> issues)
    {
        RunId = runId;
        Issues = issues;
    }

    /// <summary>取得本結果實際驗證的 GraphDocument run ID。</summary>
    public string RunId { get; }

    /// <summary>取得全部錯誤、警告與資訊訊息。</summary>
    public IReadOnlyList<PreflightIssue> Issues { get; }

    /// <summary>取得是否存在會阻擋發布的錯誤。</summary>
    public bool HasBlockingErrors => Issues.Any(issue => issue.Severity == PreflightSeverity.Error);

    /// <summary>取得阻擋錯誤數量。</summary>
    public int ErrorCount => Issues.Count(issue => issue.Severity == PreflightSeverity.Error);

    /// <summary>取得警告數量。</summary>
    public int WarningCount => Issues.Count(issue => issue.Severity == PreflightSeverity.Warning);

    /// <summary>取得資訊訊息數量。</summary>
    public int InformationCount => Issues.Count(issue => issue.Severity == PreflightSeverity.Information);
}

/// <summary>定義正式發布前不可被抽取器任意變更的核心驗證條件。</summary>
public sealed record PreflightValidatorOptions
{
    /// <summary>取得中心 SQL 預期回傳的 Menu 數量；null 表示依當次查詢實際集合驗證。</summary>
    public int? ExpectedCenterMenuCount { get; init; }

    /// <summary>取得允許發布 DatabaseObject 的資料庫名稱；空值表示不套用資料庫名稱限制。</summary>
    public string? RequiredDatabaseName { get; init; }

    /// <summary>取得允許發布 DatabaseObject 的 Provider。</summary>
    public string? RequiredProvider { get; init; }

    /// <summary>取得單一 source_text 允許保留的最大字元數。</summary>
    public int MaximumSourceTextLength { get; init; } = 500;

    /// <summary>取得是否要求 GraphDocument 已完成全功能抽取。</summary>
    public bool RequireCompleteExtraction { get; init; } = true;
}

/// <summary>以保守規則偵測不應寫入圖譜、Log 或預覽檔的機敏文字。</summary>
public static partial class SensitiveValueDetector
{
    /// <summary>判斷文字是否疑似包含密碼、Token 或完整資料庫連線字串。</summary>
    public static bool ContainsSensitiveValue(string? value)
    {
        // 空值不需掃描；其餘文字只比對格式，不保存或回傳疑似秘密內容。
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return PasswordPattern().IsMatch(value)
            || TokenPattern().IsMatch(value)
            || ConnectionStringPattern().IsMatch(value);
    }

    /// <summary>偵測 Password 或 Pwd 指派。</summary>
    [GeneratedRegex(@"(?i)\b(password|pwd)\s*=\s*[^;\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();

    /// <summary>偵測常見 API key、Bearer 或 access token 指派。</summary>
    [GeneratedRegex(@"(?i)\b(api[_-]?key|access[_-]?token|bearer)\b\s*[:=]\s*\S+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>偵測同時包含伺服器與資料庫欄位的完整連線字串。</summary>
    [GeneratedRegex(@"(?is)\b(data\s+source|server)\s*=.+;\s*(initial\s+catalog|database)\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPattern();
}

/// <summary>驗證人工試產確認過的20059與140078關鍵路徑，防止後續全量抽取退化。</summary>
public static class GoldenPathValidator
{
    /// <summary>回傳所有缺少的 Golden Path；完整時為空陣列。</summary>
    public static IReadOnlyList<PreflightIssue> Validate(GraphDocument document)
    {
        var issues = new List<PreflightIssue>();

        // 20059 必須能從 Menu 走到 FxRate Action、Confirm、Transform 與 tblFxRate。
        RequireNode(document, "menu:20059", "20059 Menu", issues);
        RequireNode(document, "web-action:fxratemaintain.index.Query", "20059 Query Action", issues);
        RequireRelationship(
            document,
            GraphRelationshipKind.ResolvesTo,
            "category:ManagementFee",
            "code:APEX.FileTransform.TransformV2.TransformV2_P13_FxRate",
            "20059 Category→TransformV2_P13_FxRate",
            issues);
        var databaseName = document.Metadata.DatabaseName;
        var provider = string.IsNullOrWhiteSpace(document.Metadata.Provider)
            ? "SqlServer"
            : document.Metadata.Provider;
        var fxRateKey = $"db:{provider.ToLowerInvariant()}:{databaseName}:dbo:tblFxRate";
        RequireRelationship(
            document,
            GraphRelationshipKind.MapsTo,
            "code:APEX.RiskMaster.DBDefinition.DDFxRate",
            fxRateKey,
            "20059 DDFxRate→tblFxRate",
            issues);
        RequireOutgoing(document, GraphRelationshipKind.ConfirmedBy, "menu:20059", "20059 放行 Menu", issues);

        // 140078 兩個 Action 名稱來自人工確認，大小寫差異須由 MVC 規則解析。
        RequireNode(document, "menu:140078", "140078 Menu", issues);
        RequireNode(
            document,
            "web-action:equitytransactionactualdeal.portfoliocombobox.none",
            "140078 PortfolioCombobox Action",
            issues);
        RequireNode(
            document,
            "web-action:equitytransactiontempcashincrease.gettempcashincreasetransformtoprepaymentdata.none",
            "140078 TempCashIncrease→Prepayment Action",
            issues);
        RequireOutgoing(document, GraphRelationshipKind.ConfirmedBy, "menu:140078", "140078 放行 Menu", issues);

        return issues;
    }

    /// <summary>驗證指定 stable key 節點存在。</summary>
    private static void RequireNode(
        GraphDocument document,
        string key,
        string description,
        ICollection<PreflightIssue> issues)
    {
        if (!document.Nodes.Any(node => node.Key == key))
        {
            issues.Add(CreateIssue(description, key));
        }
    }

    /// <summary>驗證指定 enum 關係與兩端 stable key 完全相符。</summary>
    private static void RequireRelationship(
        GraphDocument document,
        GraphRelationshipKind kind,
        string sourceKey,
        string targetKey,
        string description,
        ICollection<PreflightIssue> issues)
    {
        if (!document.Relationships.Any(edge =>
                edge.Kind == kind && edge.SourceKey == sourceKey && edge.TargetKey == targetKey))
        {
            issues.Add(CreateIssue(description, $"{sourceKey}→{targetKey}"));
        }
    }

    /// <summary>驗證 Menu 至少具有一條指定種類的出邊。</summary>
    private static void RequireOutgoing(
        GraphDocument document,
        GraphRelationshipKind kind,
        string sourceKey,
        string description,
        ICollection<PreflightIssue> issues)
    {
        if (!document.Relationships.Any(edge => edge.Kind == kind && edge.SourceKey == sourceKey))
        {
            issues.Add(CreateIssue(description, sourceKey));
        }
    }

    /// <summary>建立一致的阻擋問題。</summary>
    private static PreflightIssue CreateIssue(string description, string target) => new(
        PreflightSeverity.Error,
        PreflightReasonCode.RegressionExpectationMissing,
        $"Golden Path 缺少：{description}。",
        TargetText: target);
}

/// <summary>
/// 驗證 GraphDocument 是否符合 SPEC 的 enum、拓樸、中心集合及安全邊界。
/// 只有零阻擋錯誤的結果才可交給 Neo4j 與 BYOG 發布器。
/// </summary>
public sealed class GraphDocumentValidator
{
    private readonly PreflightValidatorOptions _options;

    /// <summary>建立使用指定發布條件的驗證器。</summary>
    public GraphDocumentValidator(PreflightValidatorOptions? options = null)
    {
        _options = options ?? new PreflightValidatorOptions();
    }

    /// <summary>完整驗證單次 GraphDocument 並回傳所有問題，不因第一筆錯誤提早停止。</summary>
    public PreflightResult Validate(
        GraphDocument document,
        IEnumerable<PreflightIssue>? extractionIssues = null)
    {
        // Resolver 產生的斷鏈問題與結構驗證問題必須進入同一份發布閘門。
        var issues = extractionIssues?.ToList() ?? new List<PreflightIssue>();

        // enum 映射是發布安全的第一道檢查，任何遺漏都必須阻擋。
        ValidateEnumMappings(issues);

        // 正式 Preflight 不允許把只有中心 Menu 的盤點圖誤當成完整權威圖譜。
        if (_options.RequireCompleteExtraction
            && document.Metadata.BuildStage != GraphBuildStage.CompleteExtraction)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.ExtractionIncomplete,
                $"GraphDocument 階段為 {document.Metadata.BuildStage}，尚未完成目前資料來源要求的全路徑抽取。"));
        }

        // 只有呼叫端明確提供 Provider／DatabaseName 時才套用來源邊界。
        if (_options.RequiredDatabaseName is not null &&
            !string.Equals(document.Metadata.DatabaseName, _options.RequiredDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.DatabaseScopeInvalid,
                $"GraphDocument 的資料庫為 '{document.Metadata.DatabaseName}'，預期為 '{_options.RequiredDatabaseName}'。"));
        }

        if (_options.RequiredProvider is not null &&
            !string.Equals(document.Metadata.Provider, _options.RequiredProvider, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.DatabaseScopeInvalid,
                $"GraphDocument 的 Provider 為 '{document.Metadata.Provider}'，預期為 '{_options.RequiredProvider}'。"));
        }

        // 將節點依 Key 建索引，供後續關係與孤立 Menu 驗證使用。
        var nodesByKey = BuildNodeIndex(document.Nodes, issues);
        ValidateCenterMenuCount(document.Nodes, issues);
        ValidateNodeKeysAndProperties(document.Nodes, issues);
        ValidateRelationships(document.Relationships, nodesByKey, issues);
        ValidateCenterMenuReachability(document, issues);

        return new PreflightResult(document.Metadata.RunId, issues);
    }

    /// <summary>驗證所有 enum 值均具有中央 Schema 映射。</summary>
    private static void ValidateEnumMappings(ICollection<PreflightIssue> issues)
    {
        try
        {
            GraphSchema.EnsureCompleteMappings();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.EnumMappingIncomplete,
                exception.Message));
        }
    }

    /// <summary>建立節點索引並捕捉同 Key 衝突。</summary>
    private static Dictionary<string, GraphNode> BuildNodeIndex(
        IEnumerable<GraphNode> nodes,
        ICollection<PreflightIssue> issues)
    {
        var nodesByKey = new Dictionary<string, GraphNode>(StringComparer.Ordinal);

        // 不使用 ToDictionary，才能把全部重複衝突寫進同一份人工審閱報告。
        foreach (var node in nodes)
        {
            if (!nodesByKey.TryAdd(node.Key, node))
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.DuplicateNodeConflict,
                    $"節點 Key '{node.Key}' 重複出現。",
                    FromKey: node.Key));
            }
        }

        return nodesByKey;
    }

    /// <summary>依選用的期望值驗證中心 Menu 數量；未指定時只保留實際集合。</summary>
    private void ValidateCenterMenuCount(
        IEnumerable<GraphNode> nodes,
        ICollection<PreflightIssue> issues)
    {
        var menuCount = nodes.Count(node => node.Kind == GraphNodeKind.Menu);
        if (_options.ExpectedCenterMenuCount is null)
        {
            return;
        }

        if (menuCount != _options.ExpectedCenterMenuCount.Value)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.MenuCountMismatch,
                $"中心 Menu 數量為 {menuCount}，預期必須為 {_options.ExpectedCenterMenuCount.Value}。"));
        }
    }

    /// <summary>驗證節點 Key、前綴與所有文字屬性。</summary>
    private void ValidateNodeKeysAndProperties(
        IEnumerable<GraphNode> nodes,
        ICollection<PreflightIssue> issues)
    {
        foreach (var node in nodes)
        {
            // Key 必須含 enum 對應前綴，避免不同實體族群產生碰撞。
            var expectedPrefix = GraphSchema.GetKeyPrefix(node.Kind);
            if (string.IsNullOrWhiteSpace(node.Key)
                || !node.Key.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.InvalidNodeKey,
                    $"節點 '{node.Key}' 未使用 {node.Kind} 的前綴 '{expectedPrefix}'。",
                    FromKey: node.Key));
            }

            // 所有文字屬性都需掃描，不假設只有特定欄位可能含秘密。
            foreach (var property in node.Properties)
            {
                if (property.Value is string text && SensitiveValueDetector.ContainsSensitiveValue(text))
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Error,
                        PreflightReasonCode.SensitiveValueDetected,
                        $"節點 '{node.Key}' 的屬性 '{property.Key}' 疑似包含機敏內容。",
                        FromKey: node.Key));
                }
            }
        }
    }

    /// <summary>驗證關係端點、enum 拓樸、來源片段長度與機敏內容。</summary>
    private void ValidateRelationships(
        IEnumerable<GraphRelationship> relationships,
        IReadOnlyDictionary<string, GraphNode> nodesByKey,
        ICollection<PreflightIssue> issues)
    {
        foreach (var relationship in relationships)
        {
            if (!nodesByKey.TryGetValue(relationship.SourceKey, out var sourceNode))
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.RelationshipSourceMissing,
                    $"關係 '{relationship.Id}' 的來源節點不存在。",
                    FromKey: relationship.SourceKey,
                    TargetText: relationship.TargetKey));
                continue;
            }

            if (!nodesByKey.TryGetValue(relationship.TargetKey, out var targetNode))
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.RelationshipTargetMissing,
                    $"關係 '{relationship.Id}' 的目標節點不存在。",
                    FromKey: relationship.SourceKey,
                    TargetText: relationship.TargetKey));
                continue;
            }

            // 關係方向與端點只能符合中央白名單，不能由抽取器臨時放寬。
            if (!GraphRelationshipTopology.IsAllowed(relationship.Kind, sourceNode.Kind, targetNode.Kind))
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.RelationshipTopologyInvalid,
                    $"{relationship.Kind} 不允許由 {sourceNode.Kind} 連至 {targetNode.Kind}。",
                    FromKey: relationship.SourceKey,
                    TargetText: relationship.TargetKey,
                    SourceFile: relationship.Evidence.SourceFile,
                    SourceLine: relationship.Evidence.SourceLine));
            }

            // source_text 過長會造成圖譜膨脹，也可能意外保存完整方法內容。
            if (relationship.Evidence.SourceText?.Length > _options.MaximumSourceTextLength)
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.SensitiveValueDetected,
                    $"關係 '{relationship.Id}' 的 source_text 超過 {_options.MaximumSourceTextLength} 字元。",
                    FromKey: relationship.SourceKey,
                    TargetText: relationship.TargetKey));
            }

            if (SensitiveValueDetector.ContainsSensitiveValue(relationship.Evidence.SourceText)
                || SensitiveValueDetector.ContainsSensitiveValue(relationship.Evidence.RawValue))
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.SensitiveValueDetected,
                    $"關係 '{relationship.Id}' 的來源資訊疑似包含機敏內容。",
                    FromKey: relationship.SourceKey,
                    TargetText: relationship.TargetKey));
            }
        }
    }

    /// <summary>驗證每一個中心 Menu 至少具有一條向外的必要關係。</summary>
    private static void ValidateCenterMenuReachability(
        GraphDocument document,
        ICollection<PreflightIssue> issues)
    {
        var sourceKeys = document.Relationships
            .Select(relationship => relationship.SourceKey)
            .ToHashSet(StringComparer.Ordinal);

        // 只檢查中心 Menu，不把共用資料庫節點的零出度誤判成孤立功能。
        foreach (var menu in document.Nodes.Where(node => node.Kind == GraphNodeKind.Menu))
        {
            if (!sourceKeys.Contains(menu.Key))
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.IsolatedCenterMenu,
                    $"中心 Menu '{menu.Key}' 沒有任何向外關係。",
                    MenuId: menu.Properties.GetValueOrDefault("menu_id")?.ToString(),
                    FromKey: menu.Key));
            }
        }
    }
}

