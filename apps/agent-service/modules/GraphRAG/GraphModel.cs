using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// GraphRAG 對外唯一允許的領域節點種類。
/// 此列舉刻意只保留「功能、入口、程式碼、資料」四個 LLM 定位修改範圍時真正需要的層級；
/// 方法、欄位、資料行等細節只能放進 <see cref="GraphEvidence"/>，不得再擴充成節點。
/// </summary>
public enum GraphNodeKind
{
    /// <summary>使用者可辨識的業務功能，例如選單、覆核、報表或排程。</summary>
    Feature,

    /// <summary>系統執行入口，例如前端頁面、Controller Action 或排程 Task。</summary>
    EntryPoint,

    /// <summary>實際修改單位，例如 C#／Java 型別或前端模組檔案。</summary>
    Code,

    /// <summary>持久化或動態設定資料，例如資料表、View、報表來源與 Enum。</summary>
    Data,
}

/// <summary>
/// GraphRAG 對外唯一允許的九種有向關係。
/// 關係名稱直接描述 LLM 需要理解的執行或資料流，不保留 Contains、DeclaredIn 等 IDE 結構邊。
/// </summary>
public enum GraphEdgeKind
{
    /// <summary>功能導向入口，或前端入口呼叫後端 HTTP 入口。</summary>
    RoutesTo,

    /// <summary>入口由某個程式碼單位實作。</summary>
    Handles,

    /// <summary>程式碼單位呼叫另一個程式碼單位；方法細節保留在 evidence。</summary>
    Calls,

    /// <summary>透過反射、Factory、Task 名稱等動態規則派送至程式碼。</summary>
    DispatchesTo,

    /// <summary>功能觸發另一個功能或入口，例如排程觸發 Task。</summary>
    Triggers,

    /// <summary>程式碼或 SQL 讀取資料。</summary>
    Reads,

    /// <summary>程式碼或 SQL 修改資料。</summary>
    Writes,

    /// <summary>兩個模型或設定具有可證明的映射關係。</summary>
    MapsTo,

    /// <summary>前八種無法準確描述、但確實會影響修改範圍的必要依賴。</summary>
    DependsOn,
}

/// <summary>描述圖譜事實是由哪一類可靠來源抽取。</summary>
public enum GraphEvidenceSource
{
    /// <summary>由編譯器語意模型解析。</summary>
    Compiler,

    /// <summary>由語法樹或正式 parser 解析。</summary>
    Ast,

    /// <summary>由 SQL Server metadata 或業務設定資料表取得。</summary>
    Sql,

    /// <summary>由 MVC、Spring、Ext.js 等框架規則解析。</summary>
    Framework,

    /// <summary>只能由命名或字串規則推測，尚未獲得語意解析確認。</summary>
    Heuristic,
}

/// <summary>描述一筆圖譜事實的可信程度，避免推測結果冒充精確事實。</summary>
public enum GraphConfidence
{
    /// <summary>直接由資料或語法取得，不需要跨符號解析。</summary>
    Exact,

    /// <summary>已由編譯器、metadata 或唯一映射完成解析。</summary>
    Resolved,

    /// <summary>符合強命名規則，但仍存在其他可能性。</summary>
    Heuristic,

    /// <summary>由多筆間接證據推導，檢索結果應降低權重。</summary>
    Inferred,
}

/// <summary>索引診斷的嚴重程度。</summary>
public enum GraphDiagnosticSeverity
{
    /// <summary>不影響正確性，只提供統計或降級資訊。</summary>
    Information,

    /// <summary>局部資料無法解析，但可保留其餘可靠圖譜。</summary>
    Warning,

    /// <summary>會使本次 canonical graph 不可信，禁止發布。</summary>
    Error,
}

/// <summary>
/// 集中維護 node role，防止 extractor 任意拼字造成同一概念出現多種名稱。
/// role 是節點屬性而不是第五種 NodeKind。
/// </summary>
public static class GraphRoles
{
    /// <summary>由系統選單定義的業務功能。</summary>
    public const string MenuFeature = "menu-feature";
    /// <summary>維護後的覆核功能。</summary>
    public const string ApprovalFeature = "approval-feature";
    /// <summary>自訂報表模板功能。</summary>
    public const string CustomReport = "custom-report";
    /// <summary>排程功能。</summary>
    public const string Schedule = "schedule";
    /// <summary>批次報表功能。</summary>
    public const string BatchReport = "batch-report";
    /// <summary>瀏覽器可導覽的前端頁面。</summary>
    public const string FrontendPage = "frontend-page";
    /// <summary>HTTP route 入口。</summary>
    public const string WebRoute = "web-route";
    /// <summary>MVC 或 Web API Controller Action。</summary>
    public const string ControllerAction = "controller-action";
    /// <summary>排程執行的命名 Task。</summary>
    public const string ScheduledTask = "scheduled-task";
    /// <summary>訊息佇列 consumer。</summary>
    public const string MessageConsumer = "message-consumer";
    /// <summary>命令列入口。</summary>
    public const string CliCommand = "cli-command";
    /// <summary>Controller 型別。</summary>
    public const string Controller = "controller";
    /// <summary>商業邏輯服務型別。</summary>
    public const string BusinessService = "business-service";
    /// <summary>Repository 或資料存取型別。</summary>
    public const string Repository = "repository";
    /// <summary>ORM 或傳輸資料模型。</summary>
    public const string DataModel = "data-model";
    /// <summary>動態報表 plugin。</summary>
    public const string ReportPlugin = "report-plugin";
    /// <summary>未符合更具體角色的一般型別。</summary>
    public const string Type = "type";
    /// <summary>前端程式模組。</summary>
    public const string Module = "module";
    /// <summary>資料庫 migration 程式。</summary>
    public const string Migration = "migration";
    /// <summary>可由程式碼或其他 SQL 物件讀寫的資料表。</summary>
    public const string Table = "table";
    /// <summary>提供查詢投影、但不展開欄位的資料庫 View。</summary>
    public const string View = "view";
    /// <summary>保存 SQL 依賴而不執行其業務邏輯的 Stored Procedure。</summary>
    public const string Procedure = "procedure";
    /// <summary>報表模板資料。</summary>
    public const string ReportTemplate = "report-template";
    /// <summary>報表資料來源設定。</summary>
    public const string ReportDataSource = "report-data-source";
    /// <summary>報表資料來源群組。</summary>
    public const string ReportDataSourceGroup = "report-data-source-group";
    /// <summary>自訂 Enum 設定。</summary>
    public const string CustomEnum = "custom-enum";
    /// <summary>標準商品類型。</summary>
    public const string ProductType = "product-type";
    /// <summary>客製商品類型。</summary>
    public const string CustomProductType = "custom-product-type";
    /// <summary>CSV 格式設定。</summary>
    public const string CsvFormat = "csv-format";
    /// <summary>不適合更具體角色的動態設定。</summary>
    public const string Configuration = "configuration";

    private static readonly IReadOnlySet<string> KnownRoles = new HashSet<string>(
    [
        MenuFeature, ApprovalFeature, CustomReport, Schedule, BatchReport,
        FrontendPage, WebRoute, ControllerAction, ScheduledTask, MessageConsumer, CliCommand,
        Controller, BusinessService, Repository, DataModel, ReportPlugin, Type, Module, Migration,
        Table, View, Procedure, ReportTemplate, ReportDataSource, ReportDataSourceGroup,
        CustomEnum, ProductType, CustomProductType, CsvFormat, Configuration,
    ], StringComparer.Ordinal);

    /// <summary>判斷 role 是否為集中定義的合法值。</summary>
    /// <param name="role">待驗證的 role。</param>
    /// <returns>合法時為 true。</returns>
    public static bool IsKnown(string role) => KnownRoles.Contains(role);
}

/// <summary>
/// 一筆 node 或 edge 的可追溯證據。
/// Details 僅保存有界的技術細節，例如方法名稱、欄位名稱或 SQL 片段；不得保存帳密與交易資料列。
/// </summary>
/// <param name="Source">證據的抽取來源。</param>
/// <param name="Confidence">證據的可信程度。</param>
/// <param name="Artifact">相對檔案路徑或不含連線資訊的 DB logical key。</param>
/// <param name="Reason">繁體中文說明此事實如何取得。</param>
/// <param name="StartLine">原始碼起始行；DB evidence 可為 null。</param>
/// <param name="EndLine">原始碼結束行；DB evidence 可為 null。</param>
/// <param name="Details">有界且已去敏感的補充資訊。</param>
public sealed record GraphEvidence(
    GraphEvidenceSource Source,
    GraphConfidence Confidence,
    string Artifact,
    string Reason,
    int? StartLine = null,
    int? EndLine = null,
    IReadOnlyDictionary<string, string>? Details = null);

/// <summary>
/// GraphRAG 領域節點。ID 在所有索引執行間必須穩定，不可包含絕對路徑、時間或 manifest version。
/// </summary>
/// <param name="Id">符合 <see cref="GraphIdentity"/> 規則的穩定 ID。</param>
/// <param name="Kind">四種領域節點之一。</param>
/// <param name="Role">由 <see cref="GraphRoles"/> 定義的細分角色。</param>
/// <param name="Name">供使用者與 LLM 閱讀的短名稱。</param>
/// <param name="SearchableText">供 BM25 搜尋的去噪文字。</param>
/// <param name="Language">business、csharp、java、frontend 或 sql。</param>
/// <param name="Technology">MVC、Spring、tblMenuMap 等來源技術。</param>
/// <param name="State">active、unresolved、shared 等狀態。</param>
/// <param name="Aliases">可搜尋的業務別名與識別碼。</param>
/// <param name="FilePath">相對專案路徑；資料庫節點為 null。</param>
/// <param name="StartLine">主要宣告起始行。</param>
/// <param name="EndLine">主要宣告結束行。</param>
/// <param name="Attributes">有界且去敏感的結構化屬性。</param>
/// <param name="Evidence">至少一筆可追溯證據。</param>
public sealed record GraphNode(
    string Id,
    GraphNodeKind Kind,
    string Role,
    string Name,
    string SearchableText,
    string Language,
    string? Technology,
    string State,
    IReadOnlyList<string> Aliases,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<GraphEvidence> Evidence);

/// <summary>
/// GraphRAG 有向關係。相同 SourceId、Kind、TargetId 只允許一條，重複證據由 assembler 合併。
/// </summary>
/// <param name="Id">由 source、kind、target 計算的 SHA-256。</param>
/// <param name="SourceId">來源節點 ID。</param>
/// <param name="Kind">九種關係之一。</param>
/// <param name="TargetId">目標節點 ID。</param>
/// <param name="Evidence">至少一筆可追溯證據。</param>
public sealed record GraphEdge(
    string Id,
    string SourceId,
    GraphEdgeKind Kind,
    string TargetId,
    IReadOnlyList<GraphEvidence> Evidence);

/// <summary>
/// 描述一個被索引 artifact 的內容身分；mtime 不參與 no-op 判斷。
/// </summary>
/// <param name="Id">artifact 穩定 ID。</param>
/// <param name="Path">正規化後的相對路徑或 DB logical key。</param>
/// <param name="Kind">csharp、java、frontend、sql 或 database。</param>
/// <param name="Length">原始 byte 長度。</param>
/// <param name="ContentHash">原始 byte SHA-256 或 DB metadata fingerprint。</param>
/// <param name="Status">indexed、ignored 或 failed。</param>
/// <param name="Reason">忽略或失敗原因。</param>
public sealed record GraphArtifact(
    string Id,
    string Path,
    string Kind,
    long Length,
    string ContentHash,
    string Status,
    string? Reason = null);

/// <summary>
/// 記錄 extractor 名稱與版本，讓 snapshot 可重現。
/// 此型別不是 profile，不接受 NodeKind filter，也不會改變 graph schema 語意。
/// </summary>
/// <param name="IndexerVersion">整體索引器版本。</param>
/// <param name="Extractors">依 ID 排序的 extractor 版本。</param>
public sealed record GraphIndexerDescriptor(
    string IndexerVersion,
    IReadOnlyDictionary<string, string> Extractors);

/// <summary>索引期間產生、可供 UI 與維護者判讀的結構化診斷。</summary>
/// <param name="Code">穩定的診斷代碼。</param>
/// <param name="Severity">嚴重程度。</param>
/// <param name="Artifact">相關 artifact。</param>
/// <param name="Message">繁體中文說明。</param>
/// <param name="Retryable">外部條件修復後是否可重試。</param>
/// <param name="AffectedId">受影響的 node／edge ID。</param>
public sealed record GraphDiagnostic(
    string Code,
    GraphDiagnosticSeverity Severity,
    string Artifact,
    string Message,
    bool Retryable,
    string? AffectedId = null);

/// <summary>
/// Canonical GraphRAG V3 snapshot。Nodes、Edges 與 Artifacts 必須已穩定排序，
/// CanonicalDigest 則證明內容未在發布途中被改寫。
/// </summary>
/// <param name="SchemaVersion">固定為 3.0。</param>
/// <param name="ProjectId">Modern Wingman 專案 ID。</param>
/// <param name="ManifestVersion">本次發布版本。</param>
/// <param name="CreatedAt">建立時間；不參與 canonical digest。</param>
/// <param name="Indexer">索引器及 extractor 版本。</param>
/// <param name="WorkingTreeFingerprint">artifact manifest 的工作樹指紋。</param>
/// <param name="Mode">full、no-op 或 body-delta。</param>
/// <param name="Artifacts">完整 artifact manifest。</param>
/// <param name="Nodes">canonical nodes。</param>
/// <param name="Edges">canonical edges。</param>
/// <param name="Diagnostics">結構化診斷。</param>
/// <param name="CapabilityGaps">目前無法可靠抽取的能力缺口。</param>
/// <param name="CanonicalDigest">排除 runtime 欄位後的 SHA-256。</param>
public sealed record GraphSnapshot(
    string SchemaVersion,
    string ProjectId,
    string ManifestVersion,
    DateTimeOffset CreatedAt,
    GraphIndexerDescriptor Indexer,
    string WorkingTreeFingerprint,
    string Mode,
    IReadOnlyList<GraphArtifact> Artifacts,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<GraphDiagnostic> Diagnostics,
    IReadOnlyList<string> CapabilityGaps,
    string CanonicalDigest);

/// <summary>單一 extractor 產生、尚未 canonical merge 的圖譜片段。</summary>
public sealed class GraphFragment
{
    /// <summary>此片段觀察到的節點；允許不同 extractor 重複觀察相同 ID。</summary>
    public List<GraphNode> Nodes { get; } = [];

    /// <summary>此片段觀察到的關係；相同關係的 evidence 會在 assembler 合併。</summary>
    public List<GraphEdge> Edges { get; } = [];

    /// <summary>抽取期間產生的非致命或致命診斷。</summary>
    public List<GraphDiagnostic> Diagnostics { get; } = [];

    /// <summary>無法可靠解析且需要顯式呈現的能力缺口。</summary>
    public List<string> CapabilityGaps { get; } = [];
}

/// <summary>
/// 所有語言與資料來源 extractor 的最小共同契約。
/// 每個 extractor 只負責產生可追溯的 fragment，不得自行寫入 Neo4j 或修改 canonical snapshot。
/// </summary>
public interface IGraphExtractor
{
    /// <summary>寫入 snapshot descriptor 的穩定 extractor ID。</summary>
    string Id { get; }

    /// <summary>抽取規則版本；規則變更時必須遞增，使 no-op 正確失效。</summary>
    string Version { get; }

    /// <summary>
    /// 從專案根目錄抽取圖譜事實。實作必須尊重 cancellation，且不得掃描根目錄以外的檔案。
    /// </summary>
    /// <param name="projectRoot">專案絕對根目錄。</param>
    /// <param name="files">由 artifact scanner 篩選後的絕對檔案清單。</param>
    /// <param name="cancellationToken">取消索引工作的 token。</param>
    /// <returns>尚未 canonical merge 的圖譜片段。</returns>
    Task<GraphFragment> ExtractAsync(
        string projectRoot,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 集中產生穩定 node／edge identity。
/// 正規化只處理格式差異，不移除可區分兩個業務實體的語意字元。
/// </summary>
public static partial class GraphIdentity
{
    private static readonly Regex InvalidTokenCharacters = InvalidTokenCharactersRegex();
    private static readonly Regex RepeatedSeparators = RepeatedSeparatorsRegex();

    /// <summary>建立 Menu Feature ID。</summary>
    /// <param name="menuId">tblMenuMap 的穩定 Menu ID。</param>
    /// <returns>例如 feature:menu:149009。</returns>
    public static string MenuFeature(string menuId) =>
        $"feature:menu:{NormalizeRequiredToken(menuId, nameof(menuId))}";

    /// <summary>建立排程 Feature ID。</summary>
    /// <param name="scheduleId">排程主檔 ID。</param>
    /// <returns>穩定排程 ID。</returns>
    public static string ScheduleFeature(string scheduleId) =>
        $"feature:schedule:{NormalizeRequiredToken(scheduleId, nameof(scheduleId))}";

    /// <summary>建立批次報表 Feature ID。</summary>
    /// <param name="reportId">批次報表主檔 ID。</param>
    /// <returns>穩定批次報表 ID。</returns>
    public static string BatchReportFeature(string reportId) =>
        $"feature:batch-report:{NormalizeRequiredToken(reportId, nameof(reportId))}";

    /// <summary>建立自訂報表 Feature ID。</summary>
    /// <param name="templateId">自訂報表 Template ID。</param>
    /// <returns>穩定自訂報表 ID。</returns>
    public static string CustomReportFeature(string templateId) =>
        $"feature:custom-report:{NormalizeRequiredToken(templateId, nameof(templateId))}";

    /// <summary>建立 MVC／Web API 入口 ID。</summary>
    /// <param name="controller">不含 Controller suffix 的控制器名稱。</param>
    /// <param name="action">Action 名稱。</param>
    /// <returns>例如 entry:web:order/save。</returns>
    public static string WebEntry(string controller, string action) =>
        $"entry:web:{NormalizeRequiredToken(RemoveControllerSuffix(controller), nameof(controller))}/" +
        NormalizeRequiredToken(action, nameof(action));

    /// <summary>建立前端入口 ID。</summary>
    /// <param name="relativePath">相對專案路徑。</param>
    /// <returns>穩定且使用斜線的前端入口 ID。</returns>
    public static string FrontendEntry(string relativePath) =>
        $"entry:frontend:{NormalizePath(relativePath)}";

    /// <summary>建立排程 Task 入口 ID。</summary>
    /// <param name="taskName">共享 Task 名稱。</param>
    /// <returns>穩定 Task ID。</returns>
    public static string TaskEntry(string taskName) =>
        $"entry:task:{NormalizeRequiredToken(taskName, nameof(taskName))}";

    /// <summary>建立 C# type-level Code ID。</summary>
    /// <param name="fullyQualifiedType">包含 namespace 的完整型別名稱。</param>
    /// <returns>穩定 C# Code ID。</returns>
    public static string CSharpCode(string fullyQualifiedType) =>
        $"code:csharp:{NormalizeQualifiedName(fullyQualifiedType)}";

    /// <summary>建立 Java type-level Code ID。</summary>
    /// <param name="fullyQualifiedType">包含 package 的完整型別名稱。</param>
    /// <returns>穩定 Java Code ID。</returns>
    public static string JavaCode(string fullyQualifiedType) =>
        $"code:java:{NormalizeQualifiedName(fullyQualifiedType)}";

    /// <summary>建立前端模組 Code ID。</summary>
    /// <param name="relativePath">相對專案路徑。</param>
    /// <returns>穩定前端 Code ID。</returns>
    public static string FrontendCode(string relativePath) =>
        $"code:frontend:{NormalizePath(relativePath)}";

    /// <summary>建立靜態 SQL script 的 Code ID；SQL 檔案是可修改單位而不是資料表節點。</summary>
    /// <param name="relativePath">相對專案路徑。</param>
    /// <returns>穩定 SQL Code ID。</returns>
    public static string SqlCode(string relativePath) =>
        $"code:sql:{NormalizePath(relativePath)}";

    /// <summary>建立 SQL object Data ID。</summary>
    /// <param name="database">資料庫 logical name，不可傳 connection string。</param>
    /// <param name="schema">schema 名稱。</param>
    /// <param name="objectType">table、view 或 procedure。</param>
    /// <param name="name">object 名稱。</param>
    /// <returns>穩定 SQL Data ID。</returns>
    public static string SqlData(string database, string schema, string objectType, string name) =>
        $"data:sql:{NormalizeRequiredToken(database, nameof(database))}/" +
        $"{NormalizeRequiredToken(schema, nameof(schema))}/" +
        $"{NormalizeRequiredToken(objectType, nameof(objectType))}/" +
        NormalizeRequiredToken(name, nameof(name));

    /// <summary>建立 CustomEnum Data ID；Enum 必須以 EnumName 而不是不穩定 DataID 聚合。</summary>
    /// <param name="enumName">CustomEnum 的邏輯名稱。</param>
    /// <returns>穩定 Enum ID。</returns>
    public static string CustomEnumData(string enumName) =>
        $"data:enum:{NormalizeRequiredToken(enumName, nameof(enumName))}";

    /// <summary>建立非 SQL 的動態業務 Data ID，例如 CSV Format、ProductType 或報表來源。</summary>
    /// <param name="category">集中定義的資料分類 token。</param>
    /// <param name="identity">該分類內的穩定 logical key。</param>
    /// <returns>格式為 data:{category}:{identity} 的穩定 ID。</returns>
    public static string BusinessData(string category, string identity) =>
        $"data:{NormalizeRequiredToken(category, nameof(category))}:" +
        NormalizeRequiredToken(identity, nameof(identity));

    /// <summary>建立九種關係共用的 deterministic SHA-256 ID。</summary>
    /// <param name="sourceId">來源節點 ID。</param>
    /// <param name="kind">關係種類。</param>
    /// <param name="targetId">目標節點 ID。</param>
    /// <returns>小寫十六進位 SHA-256。</returns>
    public static string Edge(string sourceId, GraphEdgeKind kind, string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return Sha256($"{sourceId}\0{kind}\0{targetId}");
    }

    /// <summary>正規化相對路徑，拒絕絕對路徑與向上跳脫。</summary>
    /// <param name="path">相對專案路徑。</param>
    /// <returns>小寫、斜線分隔且無開頭斜線的路徑。</returns>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part == ".."))
            throw new ArgumentException("Graph identity 不得包含絕對路徑或向上跳脫。", nameof(path));

        normalized = string.Join('/', normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePathSegment));
        if (normalized.Length == 0)
            throw new ArgumentException("Graph identity 路徑正規化後不可為空。", nameof(path));
        return normalized;
    }

    /// <summary>計算小寫十六進位 SHA-256。</summary>
    /// <param name="value">待雜湊 UTF-8 文字。</param>
    /// <returns>64 字元小寫雜湊。</returns>
    public static string Sha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    internal static string NormalizeRequiredToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = InvalidTokenCharacters.Replace(value.Trim().ToLowerInvariant(), "-");
        normalized = RepeatedSeparators.Replace(normalized, "-").Trim('-', '.', '_');
        if (normalized.Length == 0)
            throw new ArgumentException("Graph identity token 正規化後不可為空。", parameterName);
        return normalized;
    }

    internal static string NormalizeQualifiedName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var segments = value.Trim().Replace('+', '.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment => NormalizeRequiredToken(segment, nameof(value)));
        var normalized = string.Join('.', segments);
        if (normalized.Length == 0)
            throw new ArgumentException("完整型別名稱正規化後不可為空。", nameof(value));
        return normalized;
    }

    private static string NormalizePathSegment(string value)
    {
        var extension = Path.GetExtension(value);
        if (extension.Length == 0)
            return NormalizeRequiredToken(value, nameof(value));

        var stem = value[..^extension.Length];
        return $"{NormalizeRequiredToken(stem, nameof(value))}{extension.ToLowerInvariant()}";
    }

    private static string RemoveControllerSuffix(string value) =>
        value.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? value[..^"Controller".Length]
            : value;

    [GeneratedRegex(@"[^\p{L}\p{N}._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidTokenCharactersRegex();

    [GeneratedRegex(@"[-_.]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedSeparatorsRegex();
}

/// <summary>提供建立唯讀集合的共用方法，避免 extractor 回傳後仍被外部修改。</summary>
internal static class GraphCollections
{
    internal static IReadOnlyDictionary<string, string> EmptyAttributes { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    internal static IReadOnlyDictionary<string, string> ReadOnly(
        IEnumerable<KeyValuePair<string, string>> source) =>
        new ReadOnlyDictionary<string, string>(
            source.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
}
