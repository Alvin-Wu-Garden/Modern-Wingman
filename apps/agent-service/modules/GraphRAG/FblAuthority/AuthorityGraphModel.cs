using System.Security.Cryptography;
using System.Text;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 定義權威圖譜允許使用的全部節點型別。
/// 抽取器只能傳入此列舉，不得自行拼接 Neo4j Label 字串。
/// </summary>
public enum GraphNodeKind
{
    /// <summary>Roslyn 實際載入的方案檔；一個索引版本可包含一個主要方案。</summary>
    Solution,

    /// <summary>方案中可編譯的 C# 專案。</summary>
    Project,

    /// <summary>實際納入 Roslyn 專案的原始碼檔案。</summary>
    SourceFile,

    /// <summary>C# 原始碼宣告或匯入的命名空間。</summary>
    Namespace,

    /// <summary>Roslyn 語意模型解析出的 class、interface、struct、record 或 enum。</summary>
    CodeType,

    /// <summary>Roslyn 語意模型解析出的方法、建構式、解構式或運算子。</summary>
    CodeMethod,

    /// <summary>型別或方法的來源位置邊界；預設只保存行號與雜湊，不保存完整程式碼。</summary>
    CodeChunk,

    /// <summary>呼叫或繼承關係指向、但不屬於目前方案的外部型別或方法。</summary>
    ExternalSymbol,

    /// <summary>使用者在專案設定中選擇並通過連線驗證的資料庫。</summary>
    Database,

    /// <summary>SQL Server 系統目錄中實際存在的資料表或檢視表欄位。</summary>
    DatabaseColumn,

    /// <summary>Stored Procedure 或 Function 的輸入／輸出參數。</summary>
    StoredProcedureParameter,

    /// <summary>tblMenuMap 中符合中心 SQL 的功能菜單。</summary>
    Menu,

    /// <summary>由 LinkAddress 正規化後得到的 Web 路由。</summary>
    Endpoint,

    /// <summary>可由路由或前端程式實際到達的 MVC Action。</summary>
    WebAction,

    /// <summary>Controller 實際回傳的 Razor、ASPX 或其他 View。</summary>
    ViewPage,

    /// <summary>View 直接載入或功能直接依賴的 JavaScript／TypeScript。</summary>
    ClientScript,

    /// <summary>具有直接來源對應的 React 或 TypeScript 入口。</summary>
    ReactEntry,

    /// <summary>Controller、BZ、Utility、Transform、QR、DAL、DD 或 ReportKernel 類別。</summary>
    CodeClass,

    /// <summary>實際參與交易或 Transform 派送的 CategoryType。</summary>
    CategoryType,

    /// <summary>由 mapping 或 enum 證實的放行來源型別。</summary>
    ConfirmSourceType,

    /// <summary>目前 SQL Server authority 資料來源中的 Table、View、Function 或 Stored Procedure。</summary>
    DatabaseObject,

    /// <summary>CustomReport 的 RT Template。</summary>
    CustomReportTemplate,

    /// <summary>CustomReport 的 DS Data Source。</summary>
    CustomReportDataSource,

    /// <summary>CustomReport 的 PD Parameter Data Source。</summary>
    CustomParameterDataSource,

    /// <summary>會影響 PD 或後端元件解析的報表欄位。</summary>
    ReportField,
}

/// <summary>
/// 定義權威圖譜允許使用的全部關係型別。
/// Neo4j 關係名稱只能透過 <see cref="GraphSchema"/> 由此列舉轉換。
/// </summary>
public enum GraphRelationshipKind
{
    /// <summary>Solution 包含可編譯的 Project。</summary>
    ContainsProject,

    /// <summary>Project 直接參考另一個 Project。</summary>
    ReferencesProject,

    /// <summary>Project 包含實際納入編譯的 SourceFile。</summary>
    ContainsFile,

    /// <summary>SourceFile 宣告 Namespace。</summary>
    DeclaresNamespace,

    /// <summary>SourceFile 以 using 匯入 Namespace。</summary>
    ImportsNamespace,

    /// <summary>SourceFile 宣告 CodeType。</summary>
    DeclaresType,

    /// <summary>Namespace 或外層 CodeType 包含 CodeType。</summary>
    ContainsType,

    /// <summary>CodeType 宣告 CodeMethod。</summary>
    DeclaresMethod,

    /// <summary>CodeMethod 以 Roslyn symbol binding 呼叫另一個方法。</summary>
    CallsMethod,

    /// <summary>CodeMethod 明確建立某個 CodeType 實例。</summary>
    Instantiates,

    /// <summary>CodeType 繼承另一個 CodeType。</summary>
    DerivesFrom,

    /// <summary>CodeType 實作介面型別。</summary>
    ImplementsType,

    /// <summary>CodeMethod 覆寫基底方法。</summary>
    OverridesMethod,

    /// <summary>CodeMethod 明確或隱含實作介面方法。</summary>
    ImplementsMethod,

    /// <summary>CodeType 或 CodeMethod 具有來源位置 CodeChunk。</summary>
    HasChunk,

    /// <summary>既有 FBL CodeClass 對應至 Roslyn CodeType。</summary>
    RepresentsType,

    /// <summary>既有 MVC WebAction 對應至 Roslyn CodeMethod。</summary>
    ImplementedByMethod,

    /// <summary>Database 包含目前使用者設定範圍內的 DatabaseObject。</summary>
    ContainsDatabaseObject,

    /// <summary>DatabaseObject 具有實際 DatabaseColumn。</summary>
    HasColumn,

    /// <summary>Stored Procedure 或 Function 具有參數。</summary>
    HasParameter,

    /// <summary>外鍵來源 DatabaseColumn 指向目標 DatabaseColumn。</summary>
    ForeignKeyTo,

    /// <summary>Menu 的中心資料列定義於 tblMenuMap。</summary>
    DefinedIn,

    /// <summary>Menu 開啟正規化後的 Endpoint。</summary>
    Opens,

    /// <summary>Endpoint 經 MVC 路由導向 WebAction。</summary>
    RoutesTo,

    /// <summary>WebAction 由實際 Controller CodeClass 實作。</summary>
    ImplementedBy,

    /// <summary>WebAction 實際回傳 ViewPage。</summary>
    Renders,

    /// <summary>ViewPage 載入 ClientScript。</summary>
    Loads,

    /// <summary>ClientScript 呼叫可解析的 WebAction。</summary>
    Calls,

    /// <summary>編譯後入口可直接追溯至 ReactEntry。</summary>
    GeneratedFrom,

    /// <summary>ReactEntry 直接使用主要 ReactEntry 元件。</summary>
    UsesComponent,

    /// <summary>CodeClass 直接建構、注入或呼叫另一 CodeClass。</summary>
    Uses,

    /// <summary>CodeClass 在原始碼中實際繼承另一 CodeClass。</summary>
    Extends,

    /// <summary>CodeClass 以反射、switch 或 locator 進行派送。</summary>
    DispatchesWith,

    /// <summary>CategoryType 或 ConfirmSourceType 可解析至特定 CodeClass。</summary>
    ResolvesTo,

    /// <summary>DataBuilder 實際建立 Transform CodeClass。</summary>
    CreatesTransform,

    /// <summary>Controller 實際使用 Upload Handler。</summary>
    UsesUploadHandler,

    /// <summary>Upload Handler 實際使用 Batch Processor。</summary>
    UsesBatchProcessor,

    /// <summary>CodeClass 透過 QR 或 DAL 讀取資料。</summary>
    ReadsVia,

    /// <summary>CodeClass 透過 DAL 新增、更新或刪除資料。</summary>
    WritesVia,

    /// <summary>QR 或 DAL 使用其產生器家族對應的 DD。</summary>
    UsesDefinition,

    /// <summary>DD 以 DataTableName 或等價設定映射至 DatabaseObject。</summary>
    MapsTo,

    /// <summary>維護 Menu 由可確認的放行 Menu 處理。</summary>
    ConfirmedBy,

    /// <summary>Menu、Script、CodeClass 或 CategoryType 使用 ConfirmSourceType。</summary>
    UsesConfirmSource,

    /// <summary>放行 Menu 接受特定 ConfirmSourceType。</summary>
    AcceptsConfirmSource,

    /// <summary>放行 Menu 或 ConfirmSourceType 使用完成處理器。</summary>
    CompletesWith,

    /// <summary>View、Function 或 Stored Procedure 直接依賴 DatabaseObject。</summary>
    DependsOn,

    /// <summary>CodeClass 或報表結構直接讀取 DatabaseObject。</summary>
    ReadsData,

    /// <summary>功能啟動前要求 DatabaseObject 具有特定資料。</summary>
    RequiresData,

    /// <summary>CodeClass 明確執行 Stored Procedure 或 Function。</summary>
    Executes,

    /// <summary>Menu 以 GUID 開啟 CustomReportTemplate。</summary>
    OpensCustomReport,

    /// <summary>CustomReportTemplate 包含 CustomReportDataSource。</summary>
    ContainsDataSource,

    /// <summary>CustomReportDataSource 包含會影響解析的 ReportField。</summary>
    HasField,

    /// <summary>ReportField 或 DS 使用 CustomParameterDataSource。</summary>
    UsesParameterSource,

    /// <summary>ReportField 或 DS 使用後端 CodeClass 元件。</summary>
    UsesBackendControl,

    /// <summary>DS 或 PD 查詢目前 SQL Server authority 資料來源的 DatabaseObject。</summary>
    Queries,

    /// <summary>Menu 或 Endpoint 載入已驗證的 ReportKernel CodeClass。</summary>
    LoadsPluginReport,
}

/// <summary>定義關係可接受的直接來源，不代表證據強弱。</summary>
public enum GraphSourceKind
{
    /// <summary>C#、JavaScript、TypeScript 或 View 原始碼。</summary>
    SourceCode,
    /// <summary>目前 SQL Server authority 資料來源的實際資料列。</summary>
    DatabaseRow,
    /// <summary>SQL module definition 或相依性目錄。</summary>
    SqlDefinition,
    /// <summary>CustomReport 的 XML 定義。</summary>
    Xml,
    /// <summary>MVC 或應用程式路由規則。</summary>
    Route,
    /// <summary>由系統維護者明確確認的關係。</summary>
    ManualConfirmation,
}

/// <summary>定義資料存取關係可記錄的操作種類。</summary>
public enum GraphOperation
{
    /// <summary>讀取資料。</summary>
    Read,
    /// <summary>新增資料。</summary>
    Insert,
    /// <summary>更新資料。</summary>
    Update,
    /// <summary>刪除資料。</summary>
    Delete,
}

/// <summary>定義 CodeClass 節點可使用的受控職責，避免抽取器寫入任意角色文字。</summary>
public enum CodeClassRole
{
    /// <summary>MVC 執行期 Controller。</summary>
    Controller,
    /// <summary>提供繼承 Action 的 Controller 基底類別。</summary>
    ControllerBase,
    /// <summary>一般業務邏輯或協調類別。</summary>
    BusinessLogic,
    /// <summary>共用工具類別。</summary>
    Utility,
    /// <summary>依條件尋找實作類別的 Locator。</summary>
    Locator,
    /// <summary>建立其他處理器的 Builder。</summary>
    Builder,
    /// <summary>FileTransform 轉檔實作。</summary>
    Transform,
    /// <summary>由 QR 檔案家族證實的查詢類別。</summary>
    Query,
    /// <summary>由 DAL 檔案家族證實的資料寫入類別。</summary>
    DataAccess,
    /// <summary>由 DD 檔案家族證實的資料庫定義類別。</summary>
    DataDefinition,
    /// <summary>上傳檔案處理器。</summary>
    UploadHandler,
    /// <summary>批次資料處理器。</summary>
    BatchProcessor,
    /// <summary>PluginReport 的 ReportKernel。</summary>
    ReportKernel,
    /// <summary>目前無法再細分但有直接程式依賴的類別。</summary>
    Other,
}

/// <summary>定義 SQL Server authority 與 SQLite catalog 可發布的資料庫物件種類。</summary>
public enum DatabaseObjectKind
{
    /// <summary>實體資料表。</summary>
    Table,
    /// <summary>檢視表。</summary>
    View,
    /// <summary>純量或資料表值函數。</summary>
    Function,
    /// <summary>預存程序。</summary>
    StoredProcedure,
    /// <summary>SQL Server Synonym；名稱存在於目前資料庫但目標可由同義字指向。</summary>
    Synonym,
    /// <summary>SQLite trigger；只保存資料庫目錄事實，不推測觸發流程。</summary>
    Trigger,
    /// <summary>SQLite index；只保存資料庫目錄事實，不推測查詢效能。</summary>
    Index,
}

/// <summary>表示 Menu LinkAddress 應交由哪一種解析器處理。</summary>
public enum MenuResolverKind
{
    /// <summary>一般 MVC Web 功能。</summary>
    StandardWeb,
    /// <summary>以 Base64 指向 ReportKernel 的 PluginReport。</summary>
    PluginReport,
    /// <summary>以 GUID 指向 RT／DS／PD 的 CustomReport。</summary>
    CustomReport,
}

/// <summary>標示 GraphDocument 目前是中心盤點或已完成全功能解析。</summary>
public enum GraphBuildStage
{
    /// <summary>只完成中心 Menu 與入口盤點，不具正式發布資格。</summary>
    MenuInventory,

    /// <summary>完成 Standard Web 入口解析，但其他 Resolver 尚未全部完成。</summary>
    StandardWebExtraction,

    /// <summary>全部必要路徑已解析，仍需通過 Preflight 才可發布。</summary>
    CompleteExtraction,
}

/// <summary>定義 Preflight 訊息的嚴重程度。</summary>
public enum PreflightSeverity
{
    /// <summary>不阻擋發布，但需要留下可追蹤資訊。</summary>
    Information,
    /// <summary>不阻擋發布，但需要人工留意。</summary>
    Warning,
    /// <summary>必須阻擋 Neo4j 與 BYOG 發布。</summary>
    Error,
}

/// <summary>集中定義抽取器、canonical schema、Neo4j envelope 與 community cache 版本。</summary>
public static class ProjectGraphVersions
{
    /// <summary>輸入 fingerprint 使用的抽取器版本；變更會強制所有專案重新索引。</summary>
    public const string Indexer = "project-semantic-v2";

    /// <summary>Graph manifest 使用的強型別 canonical schema 版本。</summary>
    public const string CanonicalSchema = "project-graph-25x56-v2";

    /// <summary>Neo4j ProjectGraph envelope 保存的 schema 版本。</summary>
    public const string StorageSchema = "project-graph-v2";

    /// <summary>Community deterministic template 與摘要 cache 版本。</summary>
    public const string Community = "project-community-v2";
}

/// <summary>定義 Preflight 可輸出的受控原因代碼。</summary>
public enum PreflightReasonCode
{
    /// <summary>GraphDocument 尚未完成全部功能解析。</summary>
    ExtractionIncomplete,

    /// <summary>Roslyn semantic solution 已成功抽取並併入權威圖。</summary>
    SemanticExtractionCompleted,

    /// <summary>MSBuild solution 無法完整載入，已降級至 repository syntax／semantic fallback。</summary>
    SemanticExtractionDegraded,

    /// <summary>中心 Menu 查詢結果不符合當次驗收條件。</summary>
    MenuCountMismatch,
    /// <summary>Menu 路由無法分類或解析。</summary>
    MenuRouteUnresolved,
    /// <summary>圖節點使用空白或不合法的穩定 Key。</summary>
    InvalidNodeKey,
    /// <summary>同一穩定 Key 被宣告為不同節點型別。</summary>
    DuplicateNodeConflict,
    /// <summary>關係的來源節點不存在。</summary>
    RelationshipSourceMissing,
    /// <summary>關係的目標節點不存在。</summary>
    RelationshipTargetMissing,
    /// <summary>關係型別不允許目前的來源與目標節點組合。</summary>
    RelationshipTopologyInvalid,
    /// <summary>中心 Menu 沒有任何可到達的必要路徑。</summary>
    IsolatedCenterMenu,
    /// <summary>屬性或 TextUnit 疑似包含秘密或完整連線字串。</summary>
    SensitiveValueDetected,
    /// <summary>GraphDocument 的 enum 映射不完整。</summary>
    EnumMappingIncomplete,
    /// <summary>圖文件的資料庫識別與當次設定不一致。</summary>
    DatabaseScopeInvalid,
    /// <summary>發現範圍外資料庫，記錄後停止展開。</summary>
    OutOfScopeDatabase,
    /// <summary>Controller 無法找到。</summary>
    ControllerNotFound,
    /// <summary>Action 找不到或存在多個無法唯一決定的候選。</summary>
    ActionNotFoundOrAmbiguous,
    /// <summary>View 找不到或存在多個無法唯一決定的候選。</summary>
    ViewNotFoundOrAmbiguous,
    /// <summary>View 明確載入的 JavaScript／TypeScript 原始檔找不到。</summary>
    ClientScriptNotFound,
    /// <summary>前端呼叫無法解析至 Action。</summary>
    ScriptActionUnresolved,
    /// <summary>由維護者確認為歷史 Store 的特定前端 URL 已排除。</summary>
    HistoricalScriptRouteExcluded,
    /// <summary>Action 中明確出現的業務類別無法唯一解析。</summary>
    CodeDependencyUnresolved,
    /// <summary>PluginReport 的 Base64 無法解碼。</summary>
    PluginBase64Invalid,
    /// <summary>PluginReport 類別不存在。</summary>
    PluginTypeNotFound,
    /// <summary>PluginReport 類別未納入專案編譯。</summary>
    PluginTypeNotCompiled,
    /// <summary>CustomReport 的 RT 找不到。</summary>
    CustomReportTemplateNotFound,
    /// <summary>CustomReport 的 DS 找不到。</summary>
    CustomReportDataSourceNotFound,
    /// <summary>CustomReport 的 PD 無法唯一判定。</summary>
    CustomParameterDataSourceAmbiguous,
    /// <summary>QR 與 DD 的產生器家族對應缺失。</summary>
    QueryDefinitionMappingMissing,
    /// <summary>DAL 與 DD 的產生器家族對應缺失。</summary>
    DataAccessDefinitionMappingMissing,
    /// <summary>目前資料來源中找不到預期 DatabaseObject。</summary>
    DatabaseObjectNotFound,
    /// <summary>動態 SQL 無法安全解析出明確 DatabaseObject。</summary>
    DynamicSqlObjectUnresolved,
    /// <summary>FileTransform 的 Category 派送鏈無法解析。</summary>
    TransformDispatchUnresolved,
    /// <summary>Maintain Menu 與 Confirm Menu 無法建立可確認的配對。</summary>
    ConfirmMenuPairUnresolved,
    /// <summary>20059 或 140078 的人工確認 Golden Path 缺少必要節點或關係。</summary>
    RegressionExpectationMissing,
}

/// <summary>
/// 將強型別 enum 映射為外部儲存格式。
/// 所有 Neo4j Label 與關係名稱只能在此處定義，抽取器不得自行寫字串。
/// </summary>
public static class GraphSchema
{
    /// <summary>取得 Neo4j 使用的節點 Label。</summary>
    public static string GetNodeLabel(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Solution => "Solution",
        GraphNodeKind.Project => "Project",
        GraphNodeKind.SourceFile => "SourceFile",
        GraphNodeKind.Namespace => "Namespace",
        GraphNodeKind.CodeType => "CodeType",
        GraphNodeKind.CodeMethod => "CodeMethod",
        GraphNodeKind.CodeChunk => "CodeChunk",
        GraphNodeKind.ExternalSymbol => "ExternalSymbol",
        GraphNodeKind.Database => "Database",
        GraphNodeKind.DatabaseColumn => "DatabaseColumn",
        GraphNodeKind.StoredProcedureParameter => "StoredProcedureParameter",
        GraphNodeKind.Menu => "Menu",
        GraphNodeKind.Endpoint => "Endpoint",
        GraphNodeKind.WebAction => "WebAction",
        GraphNodeKind.ViewPage => "ViewPage",
        GraphNodeKind.ClientScript => "ClientScript",
        GraphNodeKind.ReactEntry => "ReactEntry",
        GraphNodeKind.CodeClass => "CodeClass",
        GraphNodeKind.CategoryType => "CategoryType",
        GraphNodeKind.ConfirmSourceType => "ConfirmSourceType",
        GraphNodeKind.DatabaseObject => "DatabaseObject",
        GraphNodeKind.CustomReportTemplate => "CustomReportTemplate",
        GraphNodeKind.CustomReportDataSource => "CustomReportDataSource",
        GraphNodeKind.CustomParameterDataSource => "CustomParameterDataSource",
        GraphNodeKind.ReportField => "ReportField",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "節點型別尚未建立儲存映射。"),
    };

    /// <summary>取得穩定 Key 應使用的固定前綴。</summary>
    public static string GetKeyPrefix(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Solution => "solution:",
        GraphNodeKind.Project => "project:",
        GraphNodeKind.SourceFile => "source-file:",
        GraphNodeKind.Namespace => "namespace:",
        GraphNodeKind.CodeType => "code-type:",
        GraphNodeKind.CodeMethod => "code-method:",
        GraphNodeKind.CodeChunk => "code-chunk:",
        GraphNodeKind.ExternalSymbol => "external-symbol:",
        GraphNodeKind.Database => "database:",
        GraphNodeKind.DatabaseColumn => "database-column:",
        GraphNodeKind.StoredProcedureParameter => "stored-procedure-parameter:",
        GraphNodeKind.Menu => "menu:",
        GraphNodeKind.Endpoint => "endpoint:",
        GraphNodeKind.WebAction => "web-action:",
        GraphNodeKind.ViewPage => "view:",
        GraphNodeKind.ClientScript => "client-script:",
        GraphNodeKind.ReactEntry => "react-entry:",
        GraphNodeKind.CodeClass => "code:",
        GraphNodeKind.CategoryType => "category:",
        GraphNodeKind.ConfirmSourceType => "confirm-source:",
        GraphNodeKind.DatabaseObject => "db:",
        GraphNodeKind.CustomReportTemplate => "custom-report-template:",
        GraphNodeKind.CustomReportDataSource => "custom-report-ds:",
        GraphNodeKind.CustomParameterDataSource => "custom-parameter-ds:",
        GraphNodeKind.ReportField => "report-field:",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "節點型別尚未建立 Key 前綴。"),
    };

    /// <summary>取得 Neo4j 使用的大寫底線關係名稱。</summary>
    public static string GetRelationshipType(GraphRelationshipKind kind) => kind switch
    {
        GraphRelationshipKind.ContainsProject => "CONTAINS_PROJECT",
        GraphRelationshipKind.ReferencesProject => "REFERENCES_PROJECT",
        GraphRelationshipKind.ContainsFile => "CONTAINS_FILE",
        GraphRelationshipKind.DeclaresNamespace => "DECLARES_NAMESPACE",
        GraphRelationshipKind.ImportsNamespace => "IMPORTS_NAMESPACE",
        GraphRelationshipKind.DeclaresType => "DECLARES_TYPE",
        GraphRelationshipKind.ContainsType => "CONTAINS_TYPE",
        GraphRelationshipKind.DeclaresMethod => "DECLARES_METHOD",
        GraphRelationshipKind.CallsMethod => "CALLS_METHOD",
        GraphRelationshipKind.Instantiates => "INSTANTIATES",
        GraphRelationshipKind.DerivesFrom => "DERIVES_FROM",
        GraphRelationshipKind.ImplementsType => "IMPLEMENTS_TYPE",
        GraphRelationshipKind.OverridesMethod => "OVERRIDES_METHOD",
        GraphRelationshipKind.ImplementsMethod => "IMPLEMENTS_METHOD",
        GraphRelationshipKind.HasChunk => "HAS_CHUNK",
        GraphRelationshipKind.RepresentsType => "REPRESENTS_TYPE",
        GraphRelationshipKind.ImplementedByMethod => "IMPLEMENTED_BY_METHOD",
        GraphRelationshipKind.ContainsDatabaseObject => "CONTAINS_DATABASE_OBJECT",
        GraphRelationshipKind.HasColumn => "HAS_COLUMN",
        GraphRelationshipKind.HasParameter => "HAS_PARAMETER",
        GraphRelationshipKind.ForeignKeyTo => "FOREIGN_KEY_TO",
        GraphRelationshipKind.DefinedIn => "DEFINED_IN",
        GraphRelationshipKind.Opens => "OPENS",
        GraphRelationshipKind.RoutesTo => "ROUTES_TO",
        GraphRelationshipKind.ImplementedBy => "IMPLEMENTED_BY",
        GraphRelationshipKind.Renders => "RENDERS",
        GraphRelationshipKind.Loads => "LOADS",
        GraphRelationshipKind.Calls => "CALLS",
        GraphRelationshipKind.GeneratedFrom => "GENERATED_FROM",
        GraphRelationshipKind.UsesComponent => "USES_COMPONENT",
        GraphRelationshipKind.Uses => "USES",
        GraphRelationshipKind.Extends => "EXTENDS",
        GraphRelationshipKind.DispatchesWith => "DISPATCHES_WITH",
        GraphRelationshipKind.ResolvesTo => "RESOLVES_TO",
        GraphRelationshipKind.CreatesTransform => "CREATES_TRANSFORM",
        GraphRelationshipKind.UsesUploadHandler => "USES_UPLOAD_HANDLER",
        GraphRelationshipKind.UsesBatchProcessor => "USES_BATCH_PROCESSOR",
        GraphRelationshipKind.ReadsVia => "READS_VIA",
        GraphRelationshipKind.WritesVia => "WRITES_VIA",
        GraphRelationshipKind.UsesDefinition => "USES_DEFINITION",
        GraphRelationshipKind.MapsTo => "MAPS_TO",
        GraphRelationshipKind.ConfirmedBy => "CONFIRMED_BY",
        GraphRelationshipKind.UsesConfirmSource => "USES_CONFIRM_SOURCE",
        GraphRelationshipKind.AcceptsConfirmSource => "ACCEPTS_CONFIRM_SOURCE",
        GraphRelationshipKind.CompletesWith => "COMPLETES_WITH",
        GraphRelationshipKind.DependsOn => "DEPENDS_ON",
        GraphRelationshipKind.ReadsData => "READS_DATA",
        GraphRelationshipKind.RequiresData => "REQUIRES_DATA",
        GraphRelationshipKind.Executes => "EXECUTES",
        GraphRelationshipKind.OpensCustomReport => "OPENS_CUSTOM_REPORT",
        GraphRelationshipKind.ContainsDataSource => "CONTAINS_DATA_SOURCE",
        GraphRelationshipKind.HasField => "HAS_FIELD",
        GraphRelationshipKind.UsesParameterSource => "USES_PARAMETER_SOURCE",
        GraphRelationshipKind.UsesBackendControl => "USES_BACKEND_CONTROL",
        GraphRelationshipKind.Queries => "QUERIES",
        GraphRelationshipKind.LoadsPluginReport => "LOADS_PLUGIN_REPORT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "關係型別尚未建立儲存映射。"),
    };

    /// <summary>
    /// 驗證每個 enum 值都能映射，避免新增 enum 後忘記更新外部 Schema。
    /// </summary>
    public static void EnsureCompleteMappings()
    {
        // 逐一執行節點映射，任何遺漏都會由 switch 的例外立即揭露。
        foreach (var nodeKind in Enum.GetValues<GraphNodeKind>())
        {
            _ = GetNodeLabel(nodeKind);
            _ = GetKeyPrefix(nodeKind);
        }

        // 逐一執行關係映射，確保發布器永遠只會看到白名單名稱。
        foreach (var relationshipKind in Enum.GetValues<GraphRelationshipKind>())
        {
            _ = GetRelationshipType(relationshipKind);
        }
    }
}

/// <summary>
/// 定義每一種 enum 關係允許連接的節點型別。
/// 這個白名單可在 Preflight 阻止方向顛倒或誤接實體。
/// </summary>
public static class GraphRelationshipTopology
{
    /// <summary>判斷指定來源與目標型別是否符合 SPEC。</summary>
    public static bool IsAllowed(
        GraphRelationshipKind relationshipKind,
        GraphNodeKind sourceKind,
        GraphNodeKind targetKind) => relationshipKind switch
        {
            GraphRelationshipKind.ContainsProject => sourceKind == GraphNodeKind.Solution && targetKind == GraphNodeKind.Project,
            GraphRelationshipKind.ReferencesProject => sourceKind == GraphNodeKind.Project && targetKind == GraphNodeKind.Project,
            GraphRelationshipKind.ContainsFile => sourceKind == GraphNodeKind.Project && targetKind == GraphNodeKind.SourceFile,
            GraphRelationshipKind.DeclaresNamespace => sourceKind == GraphNodeKind.SourceFile && targetKind == GraphNodeKind.Namespace,
            GraphRelationshipKind.ImportsNamespace => sourceKind == GraphNodeKind.SourceFile && targetKind == GraphNodeKind.Namespace,
            GraphRelationshipKind.DeclaresType => sourceKind == GraphNodeKind.SourceFile && targetKind == GraphNodeKind.CodeType,
            GraphRelationshipKind.ContainsType => IsTypeContainer(sourceKind) && targetKind == GraphNodeKind.CodeType,
            GraphRelationshipKind.DeclaresMethod => sourceKind == GraphNodeKind.CodeType && targetKind == GraphNodeKind.CodeMethod,
            GraphRelationshipKind.CallsMethod => sourceKind == GraphNodeKind.CodeMethod && IsMethodTarget(targetKind),
            GraphRelationshipKind.Instantiates => sourceKind == GraphNodeKind.CodeMethod && IsTypeTarget(targetKind),
            GraphRelationshipKind.DerivesFrom => sourceKind == GraphNodeKind.CodeType && IsTypeTarget(targetKind),
            GraphRelationshipKind.ImplementsType => sourceKind == GraphNodeKind.CodeType && IsTypeTarget(targetKind),
            GraphRelationshipKind.OverridesMethod => sourceKind == GraphNodeKind.CodeMethod && IsMethodTarget(targetKind),
            GraphRelationshipKind.ImplementsMethod => sourceKind == GraphNodeKind.CodeMethod && IsMethodTarget(targetKind),
            GraphRelationshipKind.HasChunk => IsChunkOwner(sourceKind) && targetKind == GraphNodeKind.CodeChunk,
            GraphRelationshipKind.RepresentsType => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeType,
            GraphRelationshipKind.ImplementedByMethod => sourceKind == GraphNodeKind.WebAction && targetKind == GraphNodeKind.CodeMethod,
            GraphRelationshipKind.ContainsDatabaseObject => sourceKind == GraphNodeKind.Database && targetKind == GraphNodeKind.DatabaseObject,
            GraphRelationshipKind.HasColumn => sourceKind == GraphNodeKind.DatabaseObject && targetKind == GraphNodeKind.DatabaseColumn,
            GraphRelationshipKind.HasParameter => sourceKind == GraphNodeKind.DatabaseObject && targetKind == GraphNodeKind.StoredProcedureParameter,
            GraphRelationshipKind.ForeignKeyTo => sourceKind == GraphNodeKind.DatabaseColumn && targetKind == GraphNodeKind.DatabaseColumn,
            GraphRelationshipKind.DefinedIn => sourceKind == GraphNodeKind.Menu && targetKind == GraphNodeKind.DatabaseObject,
            GraphRelationshipKind.Opens => sourceKind == GraphNodeKind.Menu && targetKind == GraphNodeKind.Endpoint,
            GraphRelationshipKind.RoutesTo => sourceKind == GraphNodeKind.Endpoint && targetKind == GraphNodeKind.WebAction,
            GraphRelationshipKind.ImplementedBy => sourceKind == GraphNodeKind.WebAction && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.Renders => sourceKind == GraphNodeKind.WebAction && targetKind == GraphNodeKind.ViewPage,
            GraphRelationshipKind.Loads => sourceKind == GraphNodeKind.ViewPage && targetKind == GraphNodeKind.ClientScript,
            GraphRelationshipKind.Calls => sourceKind == GraphNodeKind.ClientScript && targetKind == GraphNodeKind.WebAction,
            GraphRelationshipKind.GeneratedFrom => sourceKind == GraphNodeKind.ClientScript && targetKind == GraphNodeKind.ReactEntry,
            GraphRelationshipKind.UsesComponent => sourceKind == GraphNodeKind.ReactEntry && targetKind == GraphNodeKind.ReactEntry,
            GraphRelationshipKind.Uses => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.Extends => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.DispatchesWith => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.ResolvesTo => IsCategoryOrConfirmSource(sourceKind) && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.CreatesTransform => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.UsesUploadHandler => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.UsesBatchProcessor => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.ReadsVia => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.WritesVia => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.UsesDefinition => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.MapsTo => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.DatabaseObject,
            GraphRelationshipKind.ConfirmedBy => sourceKind == GraphNodeKind.Menu && targetKind == GraphNodeKind.Menu,
            GraphRelationshipKind.UsesConfirmSource => IsConfirmSourceOwner(sourceKind) && targetKind == GraphNodeKind.ConfirmSourceType,
            GraphRelationshipKind.AcceptsConfirmSource => sourceKind == GraphNodeKind.Menu && targetKind == GraphNodeKind.ConfirmSourceType,
            GraphRelationshipKind.CompletesWith => IsCompletionOwner(sourceKind) && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.DependsOn => sourceKind == GraphNodeKind.DatabaseObject && targetKind == GraphNodeKind.DatabaseObject,
            GraphRelationshipKind.ReadsData => IsDatabaseReader(sourceKind) && targetKind == GraphNodeKind.DatabaseObject,
            GraphRelationshipKind.RequiresData => IsDataRequirementOwner(sourceKind) && targetKind == GraphNodeKind.DatabaseObject,
            GraphRelationshipKind.Executes => sourceKind == GraphNodeKind.CodeClass && targetKind == GraphNodeKind.DatabaseObject,
            GraphRelationshipKind.OpensCustomReport => sourceKind == GraphNodeKind.Menu && targetKind == GraphNodeKind.CustomReportTemplate,
            GraphRelationshipKind.ContainsDataSource => sourceKind == GraphNodeKind.CustomReportTemplate && targetKind == GraphNodeKind.CustomReportDataSource,
            GraphRelationshipKind.HasField => sourceKind == GraphNodeKind.CustomReportDataSource && targetKind == GraphNodeKind.ReportField,
            GraphRelationshipKind.UsesParameterSource => IsParameterSourceOwner(sourceKind) && targetKind == GraphNodeKind.CustomParameterDataSource,
            GraphRelationshipKind.UsesBackendControl => IsBackendControlOwner(sourceKind) && targetKind == GraphNodeKind.CodeClass,
            GraphRelationshipKind.Queries => IsCustomReportQueryOwner(sourceKind) && targetKind == GraphNodeKind.DatabaseObject,
            GraphRelationshipKind.LoadsPluginReport => IsPluginReportOwner(sourceKind) && targetKind == GraphNodeKind.CodeClass,
            _ => false,
        };

    /// <summary>Namespace 或外層型別可包含型別。</summary>
    private static bool IsTypeContainer(GraphNodeKind kind) =>
        kind is GraphNodeKind.Namespace or GraphNodeKind.CodeType;

    /// <summary>方法關係可指向目前方案的方法或外部方法。</summary>
    private static bool IsMethodTarget(GraphNodeKind kind) =>
        kind is GraphNodeKind.CodeMethod or GraphNodeKind.ExternalSymbol;

    /// <summary>型別關係可指向目前方案型別或外部型別。</summary>
    private static bool IsTypeTarget(GraphNodeKind kind) =>
        kind is GraphNodeKind.CodeType or GraphNodeKind.ExternalSymbol;

    /// <summary>只有型別與方法具有可檢索的來源邊界。</summary>
    private static bool IsChunkOwner(GraphNodeKind kind) =>
        kind is GraphNodeKind.CodeType or GraphNodeKind.CodeMethod;

    /// <summary>判斷是否為 Category 或 ConfirmSource 節點。</summary>
    private static bool IsCategoryOrConfirmSource(GraphNodeKind kind) =>
        kind is GraphNodeKind.CategoryType or GraphNodeKind.ConfirmSourceType;

    /// <summary>判斷節點是否可宣告使用 ConfirmSourceType。</summary>
    private static bool IsConfirmSourceOwner(GraphNodeKind kind) =>
        kind is GraphNodeKind.Menu or GraphNodeKind.ClientScript or GraphNodeKind.CodeClass or GraphNodeKind.CategoryType;

    /// <summary>判斷節點是否可連接放行完成處理器。</summary>
    private static bool IsCompletionOwner(GraphNodeKind kind) =>
        kind is GraphNodeKind.Menu or GraphNodeKind.ConfirmSourceType;

    /// <summary>判斷節點是否可直接讀取資料庫物件。</summary>
    private static bool IsDatabaseReader(GraphNodeKind kind) =>
        kind is GraphNodeKind.CodeClass
            or GraphNodeKind.CustomReportTemplate
            or GraphNodeKind.CustomReportDataSource
            or GraphNodeKind.CustomParameterDataSource
            or GraphNodeKind.ReportField;

    /// <summary>判斷節點是否可宣告功能啟動所需資料。</summary>
    private static bool IsDataRequirementOwner(GraphNodeKind kind) =>
        kind is GraphNodeKind.Menu or GraphNodeKind.CodeClass;

    /// <summary>判斷節點是否可使用 PD。</summary>
    private static bool IsParameterSourceOwner(GraphNodeKind kind) =>
        kind is GraphNodeKind.ReportField or GraphNodeKind.CustomReportDataSource;

    /// <summary>判斷節點是否可使用 CustomReport 後端元件。</summary>
    private static bool IsBackendControlOwner(GraphNodeKind kind) =>
        kind is GraphNodeKind.ReportField or GraphNodeKind.CustomReportDataSource;

    /// <summary>判斷節點是否可執行 CustomReport 查詢。</summary>
    private static bool IsCustomReportQueryOwner(GraphNodeKind kind) =>
        kind is GraphNodeKind.CustomReportDataSource or GraphNodeKind.CustomParameterDataSource;

    /// <summary>判斷節點是否可載入 ReportKernel。</summary>
    private static bool IsPluginReportOwner(GraphNodeKind kind) =>
        kind is GraphNodeKind.Menu or GraphNodeKind.Endpoint;
}

/// <summary>
/// 保存關係的直接來源定位資訊。
/// 此物件不包含證據等級，因為專案只判斷關係是否有直接事實來源。
/// </summary>
public sealed record GraphEvidence
{
    /// <summary>取得關係來源種類。</summary>
    public required GraphSourceKind SourceKind { get; init; }

    /// <summary>取得 Repository 相對路徑；資料庫來源可為空。</summary>
    public string? SourceFile { get; init; }

    /// <summary>取得 1-based 原始碼行號。</summary>
    public int? SourceLine { get; init; }

    /// <summary>取得受長度限制的必要來源片段。</summary>
    public string? SourceText { get; init; }

    /// <summary>取得作為關係來源的資料庫物件名稱。</summary>
    public string? DatabaseObject { get; init; }

    /// <summary>取得作為關係來源的資料庫欄位名稱。</summary>
    public string? DatabaseColumn { get; init; }

    /// <summary>取得 Menu ID、TemplateID 或 SerialID 等資料列識別。</summary>
    public string? RowKey { get; init; }

    /// <summary>取得 RT／DS／PD 的 XML 定位路徑。</summary>
    public string? XmlPath { get; init; }

    /// <summary>取得 LinkAddress、Base64 或 Category 的必要原始值。</summary>
    public string? RawValue { get; init; }

    /// <summary>取得資料存取關係實際執行的操作。</summary>
    public IReadOnlyList<GraphOperation> Operations { get; init; } = Array.Empty<GraphOperation>();

    /// <summary>取得功能正常運作所需資料的查詢條件。</summary>
    public string? Predicate { get; init; }

    /// <summary>取得穩定且必要的資料值。</summary>
    public IReadOnlyList<string> RequiredValues { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 表示一個具有穩定 Key 與 enum 型別的圖節點。
/// Properties 只能保存描述資訊，不能用來取代 <see cref="Kind"/>。
/// </summary>
public sealed record GraphNode(
    string Key,
    GraphNodeKind Kind,
    IReadOnlyDictionary<string, object?> Properties)
{
    /// <summary>建立節點並複製屬性，避免抽取器在加入後改寫內容。</summary>
    public static GraphNode Create(
        GraphNodeKind kind,
        string key,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        // 屬性採獨立 Dictionary，確保 GraphDocument 建立後內容保持穩定。
        var copiedProperties = properties is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(properties, StringComparer.Ordinal);

        return new GraphNode(key, kind, copiedProperties);
    }
}

/// <summary>表示兩個既有節點之間，由 enum 定義的單一確定性關係。</summary>
public sealed record GraphRelationship(
    string Id,
    string SourceKey,
    string TargetKey,
    GraphRelationshipKind Kind,
    GraphEvidence Evidence,
    IReadOnlyDictionary<string, object?> Properties)
{
    /// <summary>建立關係並以端點及 enum 型別產生穩定識別碼。</summary>
    public static GraphRelationship Create(
        GraphRelationshipKind kind,
        string sourceKey,
        string targetKey,
        GraphEvidence evidence,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        // 關係 ID 不使用執行順序，確保相同事實在重新索引後維持相同識別。
        var identity = $"{sourceKey}|{GraphSchema.GetRelationshipType(kind)}|{targetKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var relationshipId = Convert.ToHexString(hash).ToLowerInvariant();

        // 額外屬性同樣先複製，防止呼叫端稍後修改既有圖內容。
        var copiedProperties = properties is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(properties, StringComparer.Ordinal);

        return new GraphRelationship(
            relationshipId,
            sourceKey,
            targetKey,
            kind,
            evidence,
            copiedProperties);
    }
}

/// <summary>保存單次確定性抽取的執行身分與來源快照。</summary>
public sealed record GraphRunMetadata(
    string RunId,
    DateTimeOffset GeneratedAt,
    string SourceRoot,
    string DatabaseName,
    GraphBuildStage BuildStage,
    string? SourceCommit,
    string? DatabaseSnapshotIdentity,
    string Provider = "SqlServer");

/// <summary>
/// 表示 Preflight、Neo4j 與 BYOG 共用的唯一圖資料契約。
/// 發布器只能接收此物件，避免兩個儲存層各自重新推測關係。
/// </summary>
public sealed record GraphDocument(
    GraphRunMetadata Metadata,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphRelationship> Relationships);

/// <summary>
/// 以穩定 Key 去重並組合 GraphDocument。
/// 抽取器必須透過此 Builder 加入 enum 節點與 enum 關係。
/// </summary>
public sealed class GraphDocumentBuilder
{
    private readonly GraphRunMetadata _metadata;
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphRelationship> _relationships = new(StringComparer.Ordinal);

    /// <summary>建立指定 run 的空白圖文件。</summary>
    public GraphDocumentBuilder(GraphRunMetadata metadata)
    {
        _metadata = metadata;
    }

    /// <summary>從既有 GraphDocument 建立下一階段 Builder，完整保留節點與關係。</summary>
    public static GraphDocumentBuilder FromDocument(
        GraphDocument document,
        GraphBuildStage nextStage)
    {
        var builder = new GraphDocumentBuilder(document.Metadata with { BuildStage = nextStage });

        // 先複製節點，再複製關係，維持相同 stable key 與 deterministic relationship ID。
        foreach (var node in document.Nodes)
        {
            builder.AddNode(node.Kind, node.Key, node.Properties);
        }

        foreach (var relationship in document.Relationships)
        {
            builder.AddRelationship(
                relationship.Kind,
                relationship.SourceKey,
                relationship.TargetKey,
                relationship.Evidence,
                relationship.Properties);
        }

        return builder;
    }

    /// <summary>新增節點；完全相同的重複節點會被安全忽略。</summary>
    public GraphNode AddNode(
        GraphNodeKind kind,
        string key,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        // Builder 先阻止同一 Key 被誤用成另一種實體，詳細合法性仍交由 Preflight 驗證。
        // partial type、跨專案 symbol stub 與後續 FBL overlay 會重複補充同一節點，
        // 因此必須確定性合併屬性，不能像舊版一樣直接丟棄後續資訊。
        if (_nodes.TryGetValue(key, out var existingNode))
        {
            if (existingNode.Kind != kind)
            {
                throw new InvalidOperationException(
                    $"節點 Key '{key}' 已定義為 {existingNode.Kind}，不能再定義為 {kind}。");
            }

            var mergedProperties = MergeProperties(existingNode.Properties, properties);
            var mergedNode = existingNode with { Properties = mergedProperties };
            _nodes[key] = mergedNode;
            return mergedNode;
        }

        var node = GraphNode.Create(kind, key, properties);
        _nodes.Add(key, node);
        return node;
    }

    /// <summary>新增關係；相同端點與 enum 型別只保留一條。</summary>
    public GraphRelationship AddRelationship(
        GraphRelationshipKind kind,
        string sourceKey,
        string targetKey,
        GraphEvidence evidence,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        // 關係先由 enum 轉換並產生 deterministic ID，抽取器無法注入任意關係名稱。
        var relationship = GraphRelationship.Create(kind, sourceKey, targetKey, evidence, properties);
        if (_relationships.TryGetValue(relationship.Id, out var existingRelationship))
        {
            // CALLS_METHOD 等語意關係可能在多個 call site 重複出現；保留單一穩定 edge，
            // 並累加次數及最多二十個來源位置，避免 Neo4j 邊數無限制膨脹。
            var mergedProperties = MergeRelationshipProperties(
                existingRelationship.Properties,
                properties,
                existingRelationship.Evidence,
                evidence);
            var mergedRelationship = existingRelationship with { Properties = mergedProperties };
            _relationships[relationship.Id] = mergedRelationship;
            return mergedRelationship;
        }

        _relationships.Add(relationship.Id, relationship);
        return relationship;
    }

    /// <summary>合併同一節點的 partial declaration 與後續 domain overlay 屬性。</summary>
    private static IReadOnlyDictionary<string, object?> MergeProperties(
        IReadOnlyDictionary<string, object?> existing,
        IReadOnlyDictionary<string, object?>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
        {
            return existing;
        }

        var merged = new Dictionary<string, object?>(existing, StringComparer.Ordinal);
        foreach (var (key, value) in incoming)
        {
            if (value is null)
            {
                continue;
            }

            if (!merged.TryGetValue(key, out var current) || current is null ||
                current is string currentText && string.IsNullOrWhiteSpace(currentText))
            {
                merged[key] = value;
                continue;
            }

            if (current is IEnumerable<string> currentValues && value is IEnumerable<string> incomingValues)
            {
                merged[key] = currentValues
                    .Concat(incomingValues)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                continue;
            }

            // Other 只是語意抽取器的預設角色；FBL overlay 的具體角色必須能覆寫它。
            if (string.Equals(key, "role", StringComparison.Ordinal) &&
                string.Equals(current.ToString(), CodeClassRole.Other.ToString(), StringComparison.Ordinal) &&
                !string.Equals(value.ToString(), CodeClassRole.Other.ToString(), StringComparison.Ordinal))
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    /// <summary>合併重複 edge 的 occurrence count 與有界來源位置。</summary>
    private static IReadOnlyDictionary<string, object?> MergeRelationshipProperties(
        IReadOnlyDictionary<string, object?> existing,
        IReadOnlyDictionary<string, object?>? incoming,
        GraphEvidence existingEvidence,
        GraphEvidence incomingEvidence)
    {
        var merged = new Dictionary<string, object?>(
            MergeProperties(existing, incoming),
            StringComparer.Ordinal);
        var existingCount = ReadPositiveInt(existing.GetValueOrDefault("occurrence_count")) ?? 1;
        var incomingCount = ReadPositiveInt(incoming?.GetValueOrDefault("occurrence_count")) ?? 1;
        merged["occurrence_count"] = checked(existingCount + incomingCount);

        var locations = ReadLocations(existing.GetValueOrDefault("locations"))
            .Concat(ReadLocations(incoming?.GetValueOrDefault("locations")))
            .Concat(FormatEvidenceLocation(existingEvidence))
            .Concat(FormatEvidenceLocation(incomingEvidence))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        if (locations.Length > 0)
        {
            merged["locations"] = locations;
        }

        return merged;
    }

    /// <summary>將可轉換的正整數屬性讀成 occurrence count。</summary>
    private static int? ReadPositiveInt(object? value) =>
        value is not null && int.TryParse(value.ToString(), out var number) && number > 0
            ? number
            : null;

    /// <summary>將位置屬性正規化為字串集合。</summary>
    private static IEnumerable<string> ReadLocations(object? value) => value switch
    {
        string text when !string.IsNullOrWhiteSpace(text) => [text],
        IEnumerable<string> values => values,
        _ => Array.Empty<string>(),
    };

    /// <summary>由主要 evidence 產生可合併的 repository-relative 位置。</summary>
    private static IEnumerable<string> FormatEvidenceLocation(GraphEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.SourceFile))
        {
            return Array.Empty<string>();
        }

        return
        [
            evidence.SourceLine is > 0
                ? $"{evidence.SourceFile}:{evidence.SourceLine.Value}"
                : evidence.SourceFile,
        ];
    }

    /// <summary>依穩定 Key 排序並建立不可變的單次發布文件。</summary>
    public GraphDocument Build()
    {
        // 固定排序可讓 JSON、測試與 Neo4j 批次寫入具有可重現結果。
        var nodes = _nodes.Values.OrderBy(node => node.Key, StringComparer.Ordinal).ToArray();
        var relationships = _relationships.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
        return new GraphDocument(_metadata, nodes, relationships);
    }
}

