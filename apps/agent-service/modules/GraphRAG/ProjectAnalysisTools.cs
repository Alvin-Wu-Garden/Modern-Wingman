using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentService.Application.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 提供專案解析 Agent 使用的唯讀探索工具。
/// 工具只允許查詢既有 Graph、搜尋專案文字、讀取專案內檔案，以及解析 C# 語法；
/// 不包含 Shell、檔案寫入或資料庫寫入能力，避免模型在問答期間改變使用者的專案。
/// </summary>
public sealed class ProjectAnalysisTools
{
    private const int MaximumTextSearchResults = 100;
    private const int MaximumGraphSearchResults = 20;
    private const int MaximumGraphPaths = 80;
    private const int MaximumGraphPathDepth = 4;
    // backboneOnly 只沿主幹關係展開，分支天生有限，深度上限可以放寬，
    // 讓 Agent 一次呼叫就取得完整資料流程，取代逐層多次呼叫。
    private const int MaximumBackboneDepth = 8;
    private const int MaximumReadLines = 2_000;
    private const int MaximumReadStartLine = 100_000;
    private const long MaximumReadFileBytes = 16 * 1024 * 1024;
    private const int MaximumGraphNodeListResults = 100;
    private const int MaximumOutlineMembers = 300;
    private const int MaximumDatabaseObjectResults = 200;
    // 預存程序/函式定義可能長達數千行；截斷長度跟其他工具的預覽上限同一等級，避免單一工具洗版整個 Context。
    private const int MaximumDatabaseDefinitionCharacters = 8_000;
    private const int DatabaseCommandTimeoutSeconds = 15;
    // Runtime 與工具本身都採同一個四次硬上限，避免不同 Provider 的工具迴圈
    // 繞過 Prompt 指示而持續消耗 Token；快取只節省 I/O，不繞過模型呼叫次數。
    private const int MaximumToolCalls = 4;
    private const int MaximumGraphSearchCalls = 4;
    private const int MaximumGraphPathCalls = 6;
    private const int MaximumTextSearchCalls = 8;
    private const int MaximumSymbolSearchCalls = 6;
    private const int MaximumFileReadCalls = 8;
    private const int MaximumGraphNodeListCalls = 4;
    private const int MaximumOutlineCalls = 6;
    private const int MaximumDatabaseSchemaCalls = 6;
    // 直接取 ParallelExtractor 的原始節點標籤列舉，避免工具清單殘留已移除的
    // Wingman 相容名稱（例如 CodeClass、Endpoint、Menu）。
    private static readonly string[] SupportedGraphNodeKinds =
        Enum.GetNames<ExtractedGraph.GraphNodeKind>();
    private static readonly HashSet<string> SearchableExtensions = new(
        [
            ".cs", ".csproj", ".sln",
            ".js", ".jsx", ".ts", ".tsx",
            ".aspx", ".ascx", ".master",
            ".sql", ".json", ".xml", ".config",
            ".md", ".txt", ".yaml", ".yml",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly string _projectId;
    private readonly string _rootPath;
    private readonly string _rootPrefix;
    private readonly IGraphStore _graphStore;
    private readonly IProjectIndexManifestStore? _manifests;
    private readonly string? _graphVersion;
    private readonly IReadOnlyList<GraphDatabaseSource> _databases;
    private readonly AgentActivityReporter? _activity;
    private readonly ILogger<ProjectAnalysisTools>? _logger;
    private readonly object _toolCacheGate = new();
    private readonly Dictionary<string, object> _toolCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<object>> _toolInFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<ToolCategory, int> _toolCounts = new();
    private int _totalToolCalls;

    /// <summary>本輪目前為止已成功接受的工具呼叫總次數；用於串流結束後記錄診斷摘要 log，協助定位多輪呼叫的問題。</summary>
    public int TotalToolCallCount => _totalToolCalls;

    /// <summary>本輪各類別工具的呼叫次數快照；用於串流結束後記錄診斷摘要 log。</summary>
    public IReadOnlyDictionary<ToolCategory, int> ToolCallCountsByCategory => _toolCounts;

    /// <summary>
    /// 建立綁定單一專案的唯讀工具集合。
    /// </summary>
    /// <param name="projectId">Graph 儲存使用的 Modern Wingman 專案識別碼。</param>
    /// <param name="rootPath">專案原始碼根目錄；所有檔案操作都限制在此目錄內。</param>
    /// <param name="graphStore">目前專案 Graph 的唯讀查詢來源。</param>
    /// <param name="activity">目前問答要求的進度事件回報器；可為 null。</param>
    /// <param name="logger">工具內部失敗時的診斷紀錄器；可為 null。</param>
    /// <param name="database">單一資料庫的舊呼叫入口；新程式碼應使用 <paramref name="databases"/>。</param>
    /// <param name="graphVersion">本輪問答固定使用的 immutable Graph 版本。</param>
    /// <param name="manifests">用來取得固定版本檔案雜湊的 manifest store。</param>
    /// <param name="databases">專案已設定的 SQL Server 與 SQLite 唯讀連線來源。</param>
    /// <exception cref="ArgumentException"><paramref name="projectId"/> 或 <paramref name="rootPath"/> 為空白。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="graphStore"/> 為 null。</exception>
    public ProjectAnalysisTools(
        string projectId,
        string rootPath,
        IGraphStore graphStore,
        AgentActivityReporter? activity = null,
        ILogger<ProjectAnalysisTools>? logger = null,
        GraphDatabaseSource? database = null,
        string? graphVersion = null,
        IProjectIndexManifestStore? manifests = null,
        IReadOnlyList<GraphDatabaseSource>? databases = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(graphStore);

        _projectId = projectId;
        _rootPath = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _rootPrefix = _rootPath + Path.DirectorySeparatorChar;
        _graphStore = graphStore;
        _graphVersion = string.IsNullOrWhiteSpace(graphVersion) ? null : graphVersion;
        _manifests = manifests;
        _databases = databases ?? (database is null ? [] : [database]);
        _activity = activity;
        _logger = logger;
    }

    #region Agent 工具註冊與執行控制

    /// <summary>
    /// 建立本輪 Agent 可呼叫的唯讀 Function Tools。
    /// 每一輪都使用綁定當前 projectId 與 rootPath 的實例，模型無法自行切換到其他專案。
    /// 工具包裝器只回報開始、完成與失敗狀態，不會把完整工具輸出直接送到前端。
    /// </summary>
    /// <param name="includeGraphTools">是否加入需要 Neo4j snapshot 的三個 Graph 工具。</param>
    /// <returns>只包含查詢能力的 MAF 工具集合。</returns>
    public IReadOnlyList<AIFunction> CreateTools(bool includeGraphTools = true)
    {
        var tools = new List<AIFunction>(includeGraphTools ? 10 : 7);
        if (includeGraphTools)
        {
            tools.Add(AIFunctionFactory.Create(
            (Func<string, int, CancellationToken, Task<GraphSearchToolResult>>)SearchGraphWithActivityAsync,
            "search_project_graph",
            "以自然語言、業務名稱、程式符號或資料表名稱搜尋目前專案的知識圖譜。先用此工具取得精確 nodeId，再視需要追蹤鏈路。" +
            "適合不確定節點名稱/需要相關性排序的模糊搜尋；若已知節點種類並想列出該種類全部成員，請改用 list_project_graph_nodes。"));
            tools.Add(AIFunctionFactory.Create(
            (Func<string, int, int, bool, CancellationToken, Task<GraphPathToolResult>>)TraceGraphPathsWithActivityAsync,
            "trace_project_graph_paths",
            "從已知 nodeId 逐層查詢目前專案 Graph 的上下游關係，回傳有界的最短鏈路。必須使用 search_project_graph 實際回傳的 nodeId。" +
            "若只需要主要可執行/資料鏈（Menu→Endpoint→Controller→Service→DAL→資料庫這類主流程，忽略描述性/次要關係），" +
            $"可將 backboneOnly 設為 true，深度上限會提高到 {MaximumBackboneDepth}，一次呼叫就能取得完整資料流程，不必逐層多次呼叫。"));
            tools.Add(AIFunctionFactory.Create(
                (Func<string, string?, int, CancellationToken, Task<GraphNodeListToolResult>>)ListGraphNodesWithActivityAsync,
                "list_project_graph_nodes",
                "列出目前知識圖譜中指定種類（kind）的所有節點，可選用 nameFilter 用名稱關鍵字篩選。" +
                $"kind 只接受：{string.Join('、', SupportedGraphNodeKinds)}。" +
                "適合列舉型問題；不確定 kind 時先使用 search_project_graph。"));
        }

        tools.Add(AIFunctionFactory.Create(
            (Func<string, string?, int, CancellationToken, Task<TextSearchToolResult>>)SearchTextWithActivityAsync,
            "search_project_text",
            "在目前專案的 C#、JavaScript、TypeScript、ASPX、SQL 與設定文件中執行不分大小寫的純文字搜尋。適合找錯誤訊息、URL、欄位或動態呼叫；" +
            "不支援正規表示式，且只能搜尋可讀文字檔案，不能搜尋二進制或已建置產出物。"));
        tools.Add(AIFunctionFactory.Create(
            (Func<string, int, int, CancellationToken, Task<FileRangeToolResult>>)ReadFileRangeWithActivityAsync,
            "read_project_file_range",
            "讀取目前專案內指定檔案的一段內容並附行號。filePath 可使用搜尋結果的相對路徑；每次最多讀取 2000 行。" +
            "若是要先確認大型 C# 檔案裡某個 class/method 在哪幾行，建議先用 outline_csharp_file 取得行號區間，避免盲目分段多次讀取。"));
        tools.Add(AIFunctionFactory.Create(
            (Func<string, bool, int, CancellationToken, Task<CSharpSymbolToolResult>>)FindCSharpSymbolWithActivityAsync,
            "find_csharp_symbol",
            "以不需要成功建置專案的 C# 語法解析，尋找 class、method、property、field 等定義及 identifier 引用。適合從符號名稱定位候選檔案。" +
            "若只想知道已知檔案裡有哪些 class/method（不需要跨檔案搜索），請改用 outline_csharp_file 更快。"));
        tools.Add(AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<CSharpFileOutlineToolResult>>)OutlineCSharpFileWithActivityAsync,
            "outline_csharp_file",
            "讀取單一 .cs 檔案的結構大綱（namespace/class/method/property/field 定義與行號範圍），不包含完整程式碼內容。" +
            "適合大型檔案在呼叫 read_project_file_range 前先確認要讀哪個行號區間，比盲目分段讀取更精確。filePath 規則與 read_project_file_range 相同。"));
        tools.Add(AIFunctionFactory.Create(
            (Func<string?, string?, int, string?, CancellationToken, Task<DatabaseObjectListToolResult>>)ListDatabaseObjectsWithActivityAsync,
            "list_database_objects",
            "列出目前專案設定資料庫中實際存在的資料表、檢視表、預存程序或函式，可用名稱與種類篩選。" +
            "同時設定 SQL Server 與 SQLite 時可用 provider 指定來源；資料庫是這些物件的權威來源。"));
        tools.Add(AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DatabaseTableDescriptionToolResult>>)DescribeDatabaseTableWithActivityAsync,
            "describe_database_table",
            "查詢目前專案設定資料庫中指定資料表或檢視表實際部署的欄位結構（型別、可否為 Null、主鍵）與外鍵關聯。" +
            "先用 list_database_objects 確認正確名稱；查到的結構反映目前實際部署狀態，可能與簽入版控的 Schema 檔案不同步。"));
        tools.Add(AIFunctionFactory.Create(
            (Func<string, string?, CancellationToken, Task<DatabaseObjectDefinitionToolResult>>)GetDatabaseObjectDefinitionWithActivityAsync,
            "get_database_object_definition",
            "讀取目前專案設定資料庫中檢視表、預存程序或函式實際部署的 SQL 定義文字。" +
            "正式環境可能已直接修改過而未同步簽入版控，這個工具取得的才是目前真正在執行的邏輯；純資料表沒有可讀取的定義。"));
        return tools;
    }

    private Task<GraphSearchToolResult> SearchGraphWithActivityAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var safeQuery = (query ?? string.Empty).Trim();
        return RunToolWithActivityAsync(
            "search_project_graph",
            "搜尋知識圖譜",
            cancellationToken,
            ToolCategory.GraphSearch,
            $"{safeQuery}|{Math.Clamp(limit, 1, MaximumGraphSearchResults)}",
            () => SearchGraphAsync(query!, limit, cancellationToken),
            result => $"找到 {result.Hits.Count} 個節點",
            budget => new GraphSearchToolResult(safeQuery, [], budget.NextAction, budget));
    }

    private Task<GraphPathToolResult> TraceGraphPathsWithActivityAsync(
        string nodeId,
        int maxDepth,
        int maxPaths,
        bool backboneOnly,
        CancellationToken cancellationToken)
    {
        var safeNodeId = (nodeId ?? string.Empty).Trim();
        var effectiveMaxDepth = Math.Clamp(maxDepth, 1, backboneOnly ? MaximumBackboneDepth : MaximumGraphPathDepth);
        return RunToolWithActivityAsync(
            "trace_project_graph_paths",
            "追蹤圖譜鏈路",
            cancellationToken,
            ToolCategory.GraphPath,
            $"{safeNodeId}|{effectiveMaxDepth}|{Math.Clamp(maxPaths, 1, MaximumGraphPaths)}|{backboneOnly}",
            () => TraceGraphPathsAsync(nodeId!, maxDepth, maxPaths, backboneOnly, cancellationToken),
            result => result.WasTruncated
                ? $"找到 {result.Paths.Count} 條鏈路（已達上限，可能還有更多分支）"
                : $"找到 {result.Paths.Count} 條鏈路",
            budget => new GraphPathToolResult(safeNodeId, [], false, budget.Message, budget));
    }

    private Task<TextSearchToolResult> SearchTextWithActivityAsync(
        string query,
        string? extension,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var safeQuery = (query ?? string.Empty).Trim();
        string? normalizedExtension;
        try
        {
            normalizedExtension = NormalizeExtension(extension);
        }
        catch (ArgumentException)
        {
            // 副檔名是模型可能猜錯的一般輸入，回傳正常結果讓模型換個副檔名或拿掉篩選重試，
            // 不要讓輸入驗證失敗變成例外往外拋。
            return Task.FromResult(new TextSearchToolResult(
                safeQuery, [], 0, 0, false,
                $"不支援搜尋副檔名：{extension}；支援的格式有 {string.Join('、', SearchableExtensions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}，或不指定副檔名搜尋全部支援格式。"));
        }

        return RunToolWithActivityAsync(
            "search_project_text",
            "搜尋專案原始碼",
            cancellationToken,
            ToolCategory.TextSearch,
            $"{safeQuery}|{normalizedExtension ?? "*"}|{Math.Clamp(maxResults, 1, MaximumTextSearchResults)}",
            () => SearchTextAsync(query!, extension, maxResults, cancellationToken),
            result => result.WasTruncated
                ? $"找到 {result.Matches.Count} 筆文字命中（已達上限，還有更多未列出）"
                : $"找到 {result.Matches.Count} 筆文字命中（已掃描 {result.FilesScanned} 個檔案）",
            budget => new TextSearchToolResult(
                safeQuery, [], 0, 0, false, budget.NextAction, budget));
    }

    private Task<FileRangeToolResult> ReadFileRangeWithActivityAsync(
        string filePath,
        int startLine,
        int lineCount,
        CancellationToken cancellationToken)
    {
        var safeFilePath = (filePath ?? string.Empty).Trim();
        var safeStartLine = Math.Max(1, startLine);
        return RunToolWithActivityAsync(
            "read_project_file_range",
            "讀取原始碼區段",
            cancellationToken,
            ToolCategory.FileRead,
            $"{safeFilePath}|{safeStartLine}|{Math.Clamp(lineCount, 1, MaximumReadLines)}",
            () => ReadFileRangeAsync(filePath!, startLine, lineCount, cancellationToken),
            result => result.Lines.Count == 0
                ? (result.Notice ?? "找不到內容")
                : $"讀取第 {result.Lines[0].Line}-{result.Lines[^1].Line} 行" +
                  (result.HasMore ? "（檔案後面還有更多內容）" : "（已到檔案結尾）"),
            budget => new FileRangeToolResult(
                safeFilePath, safeStartLine, [], false, budget.NextAction, budget));
    }

    private Task<CSharpSymbolToolResult> FindCSharpSymbolWithActivityAsync(
        string symbol,
        bool includeReferences,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var safeSymbol = (symbol ?? string.Empty).Trim();
        return RunToolWithActivityAsync(
            "find_csharp_symbol",
            "尋找 C# 符號",
            cancellationToken,
            ToolCategory.SymbolSearch,
            $"{safeSymbol}|{includeReferences}|{Math.Clamp(maxResults, 1, 100)}",
            () => FindCSharpSymbolAsync(symbol!, includeReferences, maxResults, cancellationToken),
            result =>
            {
                var kindLabel = includeReferences ? "筆定義／引用" : "筆定義";
                return result.WasTruncated
                    ? $"找到 {result.Matches.Count} {kindLabel}（已達上限，還有更多未列出）"
                    : $"找到 {result.Matches.Count} {kindLabel}";
            },
            budget => new CSharpSymbolToolResult(
                safeSymbol, [], 0, false, budget.NextAction, budget));
    }

    private Task<GraphNodeListToolResult> ListGraphNodesWithActivityAsync(
        string kind,
        string? nameFilter,
        int limit,
        CancellationToken cancellationToken)
    {
        var safeKind = (kind ?? string.Empty).Trim();
        var safeNameFilter = string.IsNullOrWhiteSpace(nameFilter) ? null : nameFilter.Trim();
        if (!SupportedGraphNodeKinds.Contains(safeKind, StringComparer.Ordinal))
        {
            // kind 是模型可能猜錯或大小寫不符的輸入，回傳正常結果讓模型改用正確 kind 重試，
            // 不要讓輸入驗證失敗變成例外往外拋。
            return Task.FromResult(new GraphNodeListToolResult(
                safeKind, [],
                $"不支援的節點種類：{kind}；支援的種類有 {string.Join('、', SupportedGraphNodeKinds)}。"));
        }

        return RunToolWithActivityAsync(
            "list_project_graph_nodes",
            "列出圖譜節點",
            cancellationToken,
            ToolCategory.GraphNodeList,
            $"{safeKind}|{safeNameFilter}|{Math.Clamp(limit, 1, MaximumGraphNodeListResults)}",
            () => ListGraphNodesAsync(kind!, nameFilter, limit, cancellationToken),
            result => $"找到 {result.Nodes.Count} 個 {safeKind} 節點",
            budget => new GraphNodeListToolResult(safeKind, [], budget.NextAction, budget));
    }

    private Task<CSharpFileOutlineToolResult> OutlineCSharpFileWithActivityAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var safeFilePath = (filePath ?? string.Empty).Trim();
        return RunToolWithActivityAsync(
            "outline_csharp_file",
            "解析檔案結構大綱",
            cancellationToken,
            ToolCategory.Outline,
            safeFilePath,
            () => OutlineCSharpFileAsync(filePath!, cancellationToken),
            result => result.Members.Count == 0
                ? (result.Notice ?? "沒有找到任何定義")
                : $"找到 {result.Members.Count} 個定義",
            budget => new CSharpFileOutlineToolResult(
                safeFilePath, [], budget.NextAction, budget));
    }

    private const string NoDatabaseConfiguredNotice =
        "此專案尚未設定資料庫連線；如需資料庫結構線索，請改用 search_project_text 搜尋簽入的 .sql 檔案。";

    private Task<DatabaseObjectListToolResult> ListDatabaseObjectsWithActivityAsync(
        string? nameFilter,
        string? kind,
        int maxResults,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        var safeNameFilter = string.IsNullOrWhiteSpace(nameFilter) ? null : nameFilter.Trim();
        var safeKind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
        if (_databases.Count == 0)
        {
            return Task.FromResult(new DatabaseObjectListToolResult(
                safeNameFilter, safeKind, [], false, NoDatabaseConfiguredNotice));
        }

        return RunToolWithActivityAsync(
            "list_database_objects",
            "查詢資料庫物件清單",
            cancellationToken,
            ToolCategory.DatabaseSchema,
            $"{safeNameFilter}|{safeKind}|{provider}|{Math.Clamp(maxResults, 1, MaximumDatabaseObjectResults)}",
            () => ListDatabaseObjectsAsync(nameFilter, kind, provider, maxResults, cancellationToken),
            result => result.WasTruncated
                ? $"找到 {result.Objects.Count} 個物件（已達上限，還有更多未列出）"
                : $"找到 {result.Objects.Count} 個物件",
            budget => new DatabaseObjectListToolResult(
                safeNameFilter, safeKind, [], false, budget.NextAction, budget));
    }

    private Task<DatabaseTableDescriptionToolResult> DescribeDatabaseTableWithActivityAsync(
        string tableName,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        var safeTableName = (tableName ?? string.Empty).Trim();
        if (_databases.Count == 0)
        {
            return Task.FromResult(new DatabaseTableDescriptionToolResult(
                safeTableName, [], [], NoDatabaseConfiguredNotice));
        }
        if (safeTableName.Length == 0)
        {
            return Task.FromResult(new DatabaseTableDescriptionToolResult(
                safeTableName, [], [], "請提供要查詢的資料表或檢視表名稱。"));
        }

        return RunToolWithActivityAsync(
            "describe_database_table",
            "查詢資料表結構",
            cancellationToken,
            ToolCategory.DatabaseSchema,
            $"{safeTableName}|{provider}",
            () => DescribeDatabaseTableAsync(safeTableName, provider, cancellationToken),
            result => result.Columns.Count == 0
                ? (result.Notice ?? "找不到欄位")
                : $"找到 {result.Columns.Count} 個欄位",
            budget => new DatabaseTableDescriptionToolResult(
                safeTableName, [], [], budget.NextAction, budget));
    }

    private Task<DatabaseObjectDefinitionToolResult> GetDatabaseObjectDefinitionWithActivityAsync(
        string objectName,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        var safeObjectName = (objectName ?? string.Empty).Trim();
        if (_databases.Count == 0)
        {
            return Task.FromResult(new DatabaseObjectDefinitionToolResult(
                safeObjectName, null, NoDatabaseConfiguredNotice));
        }
        if (safeObjectName.Length == 0)
        {
            return Task.FromResult(new DatabaseObjectDefinitionToolResult(
                safeObjectName, null, "請提供要查詢的物件名稱。"));
        }

        return RunToolWithActivityAsync(
            "get_database_object_definition",
            "查詢資料庫物件定義",
            cancellationToken,
            ToolCategory.DatabaseSchema,
            $"{safeObjectName}|{provider}",
            () => GetDatabaseObjectDefinitionAsync(safeObjectName, provider, cancellationToken),
            result => result.Definition is null
                ? (result.Notice ?? "找不到定義")
                : $"取得定義，共 {result.Definition.Length} 字元",
            budget => new DatabaseObjectDefinitionToolResult(
                safeObjectName, null, budget.NextAction, budget));
    }

    /// <summary>
    /// 執行單一唯讀工具並回報其生命週期；工具本身的輸出仍只回傳給 Agent。
    /// </summary>
    /// <remarks>
    /// 呼叫上限故意不用例外表達：無論底層是 MAF 的 <c>FunctionInvokingChatClient</c>
    /// 還是 GitHub Copilot SDK 自己的工具迴圈，兩者對「函式擲出例外」的容忍度與轉達方式
    /// 都不受我們控制（例如 <c>FunctionInvokingChatClient</c> 預設連續 3 次例外就會把例外
    /// 直接往外拋，整個串流回應因此中斷）。改成回傳一般結果，才能保證模型一定會看到
    /// 「已達上限、請直接作答」的提示，也不會讓單一次 Neo4j 或磁碟暫時性錯誤演變成整輪對話失敗。
    /// </remarks>
    private async Task<T> RunToolWithActivityAsync<T>(
        string tool,
        string label,
        CancellationToken cancellationToken,
        ToolCategory category,
        string cacheKeySuffix,
        Func<Task<T>> operation,
        Func<T, string> completedDetail,
        Func<ToolBudgetStatus, T> onBudgetExceeded)
    {
        var cacheKey = $"{tool}|{cacheKeySuffix}";
        Task<object>? sharedTask = null;
        TaskCompletionSource<object>? ownerCompletion = null;
        ToolBudgetStatus? exhaustedBudget = null;
        lock (_toolCacheGate)
        {
            var categoryLimit = category switch
            {
                ToolCategory.GraphSearch => MaximumGraphSearchCalls,
                ToolCategory.GraphPath => MaximumGraphPathCalls,
                ToolCategory.TextSearch => MaximumTextSearchCalls,
                ToolCategory.SymbolSearch => MaximumSymbolSearchCalls,
                ToolCategory.FileRead => MaximumFileReadCalls,
                ToolCategory.GraphNodeList => MaximumGraphNodeListCalls,
                ToolCategory.Outline => MaximumOutlineCalls,
                ToolCategory.DatabaseSchema => MaximumDatabaseSchemaCalls,
                _ => MaximumToolCalls,
            };
            if (_totalToolCalls >= MaximumToolCalls ||
                _toolCounts.GetValueOrDefault(category) >= categoryLimit)
            {
                var categoryUsed = _toolCounts.GetValueOrDefault(category);
                var scope = _totalToolCalls >= MaximumToolCalls
                    ? "total"
                    : "category";
                exhaustedBudget = new ToolBudgetStatus(
                    "budget_exhausted",
                    true,
                    scope,
                    _totalToolCalls,
                    MaximumToolCalls,
                    categoryUsed,
                    categoryLimit,
                    $"本輪已達 {tool} 唯讀工具上限。",
                    "不要再呼叫工具；請整理目前證據、標示合理推論與尚未確認的資訊缺口，然後直接回答使用者。");
            }
            else
            {
                // Runtime 會計算每一次模型工具請求，因此快取與進行中工作也必須計次，
                // 才不會在診斷 Log 顯示比實際 Provider 工具迴圈更少的數字。
                _totalToolCalls++;
                _toolCounts[category] = _toolCounts.GetValueOrDefault(category) + 1;
                if (_toolCache.TryGetValue(cacheKey, out var cached))
                {
                    _logger?.LogInformation("{Tool} 呼叫命中快取，cacheKey={CacheKey}", tool, cacheKey);
                    return (T)cached;
                }
                if (_toolInFlight.TryGetValue(cacheKey, out sharedTask))
                {
                    // 完全相同參數正在執行時，共用同一個工作，避免 Agent 重複觸發磁碟或 Neo4j I/O。
                    _logger?.LogInformation("{Tool} 共用進行中的相同呼叫，cacheKey={CacheKey}", tool, cacheKey);
                }
                else
                {
                    ownerCompletion = new TaskCompletionSource<object>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    sharedTask = ownerCompletion.Task;
                    // 沒有重複呼叫者時仍需觀察失敗 Task，避免取消／例外在背景造成未觀察例外。
                    _ = sharedTask.ContinueWith(
                        task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    _toolInFlight[cacheKey] = sharedTask;
                }
            }
        }

        if (exhaustedBudget is not null)
        {
            if (_activity is not null)
            {
                var budgetActivityId = await _activity.StartAsync(
                    "tool.started", label, tool, "已達本輪工具呼叫上限");
                await _activity.CompleteAsync(budgetActivityId, exhaustedBudget.Message);
            }
            return onBudgetExceeded(exhaustedBudget);
        }

        if (ownerCompletion is null)
            return (T)(await sharedTask!.WaitAsync(cancellationToken).ConfigureAwait(false));

        var activityId = _activity is null
            ? null
            : await _activity.StartAsync(
                "tool.started",
                label,
                tool,
                "正在執行唯讀專案分析工具");
        _logger?.LogInformation("{Tool} 開始執行，cacheKey={CacheKey}", tool, cacheKey);
        var toolStopwatch = Stopwatch.StartNew();
        try
        {
            var result = await operation();
            toolStopwatch.Stop();
            lock (_toolCacheGate)
            {
                _toolCache[cacheKey] = result!;
                _toolInFlight.Remove(cacheKey);
            }
            ownerCompletion.TrySetResult(result!);
            var detail = completedDetail(result);
            _logger?.LogInformation(
                "{Tool} 執行完成，耗時={ElapsedMs}ms，結果={Detail}",
                tool,
                toolStopwatch.ElapsedMilliseconds,
                detail);
            if (activityId is not null)
                await _activity!.CompleteAsync(activityId, detail);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            toolStopwatch.Stop();
            lock (_toolCacheGate)
                _toolInFlight.Remove(cacheKey);
            ownerCompletion.TrySetCanceled(cancellationToken);
            _logger?.LogInformation(
                "{Tool} 執行已取消，耗時={ElapsedMs}ms",
                tool,
                toolStopwatch.ElapsedMilliseconds);
            if (activityId is not null)
                await _activity!.FailAsync(
                    activityId,
                    "工具執行已取消");
            throw;
        }
        catch (Exception exception)
        {
            toolStopwatch.Stop();
            lock (_toolCacheGate)
                _toolInFlight.Remove(cacheKey);
            // 真正的失敗原因（Neo4j 連線、磁碟 I/O 等）只留在伺服器端記錄；
            // 往外一律拋出同一個經過消毒的例外，避免原始呼叫者拿到與其他等待者不一致、
            // 且可能包含內部細節的例外內容。
            _logger?.LogError(
                exception,
                "{Tool} 工具執行失敗，耗時={ElapsedMs}ms，cacheKey={CacheKey}",
                tool,
                toolStopwatch.ElapsedMilliseconds,
                cacheKey);
            var sanitized = new InvalidOperationException($"{tool} 工具執行失敗。");
            ownerCompletion.TrySetException(sanitized);
            if (activityId is not null)
                await _activity!.FailAsync(
                    activityId,
                    "工具執行失敗");
            throw sanitized;
        }
    }

    #endregion

    #region Graph 查詢工具

    /// <summary>
    /// 搜尋目前 active Graph 的節點，供模型從業務語言解析成實際 nodeId。
    /// </summary>
    [Description("搜尋目前專案知識圖譜中的節點。")]
    public async Task<GraphSearchToolResult> SearchGraphAsync(
        [Description("自然語言、功能名稱、程式符號、URL、資料表或欄位名稱。")]
        string query,
        [Description("最多回傳幾個節點，範圍 1 到 20。")]
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        limit = Math.Clamp(limit, 1, MaximumGraphSearchResults);
        IReadOnlyList<GraphSearchHit> hits;
        try
        {
            // 多取一小批候選後在工具邊界做精準名稱排序，避免泛用的高 BM25
            // 節點蓋過使用者明確指定的 Controller、Service 或資料表名稱。
            var candidateLimit = Math.Clamp(
                limit * 4,
                limit,
                MaximumGraphSearchResults * 4);
            var luceneQuery = GraphRetrievalService.BuildViewerLuceneQuery(query);
            hits = await _graphStore.SearchAsync(
                _projectId,
                luceneQuery,
                candidateLimit,
                _graphVersion,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return new GraphSearchToolResult(
                query.Trim(),
                [],
                "搜尋文字沒有可用的識別碼；請改用具體的類別、方法、路由、資料表或欄位名稱。");
        }
        catch (GraphStoreException exception)
        {
            return new GraphSearchToolResult(
                query.Trim(),
                [],
                DescribeGraphStoreFailure(exception));
        }

        var orderedHits = hits
            .OrderByDescending(hit => ExactGraphNameBoost(hit, query))
            .ThenByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Node.Key, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        return new GraphSearchToolResult(
            query.Trim(),
            orderedHits.Select(hit => new GraphNodeToolItem(
                    hit.Node.Key,
                    hit.Node.Kind.ToString(),
                    GetNodeText(hit.Node, "role") ?? hit.Node.Kind.ToString(),
                    GetNodeText(hit.Node, "name") ?? hit.Node.Key,
                    GetNodeFilePath(hit.Node),
                    GetNodeLine(hit.Node),
                    GetNodeLine(hit.Node),
                    hit.Score))
                .ToList(),
            orderedHits.Length == 0
                ? "Graph 沒有命中；請改用 search_project_text 尋找原始字串或符號。"
                : "如需上下游關係，請將實際 nodeId 傳給 trace_project_graph_paths。"
        );
    }

    /// <summary>
    /// 列出目前 active Graph 中指定種類的所有節點，可用名稱關鍵字篩選。
    /// 用於列舉型問題（例如「有哪些資料表／選單」），避免對同一分類重複用不同關鍵字呼叫 search_project_graph。
    /// </summary>
    [Description("列出目前知識圖譜中指定種類（kind）的節點清單。")]
    public async Task<GraphNodeListToolResult> ListGraphNodesAsync(
        [Description("節點種類，例如 DatabaseObject、Menu、CodeClass、Endpoint。")]
        string kind,
        [Description("可選名稱關鍵字篩選；不填時回傳該種類全部節點（受 limit 限制）。")]
        string? nameFilter = null,
        [Description("最多回傳幾個節點，範圍 1 到 100。")]
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        limit = Math.Clamp(limit, 1, MaximumGraphNodeListResults);
        var hits = await _graphStore.ListNodesByKindAsync(
            _projectId,
            kind.Trim(),
            string.IsNullOrWhiteSpace(nameFilter) ? null : nameFilter.Trim(),
            limit,
            _graphVersion,
            cancellationToken);

        return new GraphNodeListToolResult(
            kind.Trim(),
            hits.Select(hit => new GraphNodeToolItem(
                    hit.Node.Key,
                    hit.Node.Kind.ToString(),
                    GetNodeText(hit.Node, "role") ?? hit.Node.Kind.ToString(),
                    GetNodeText(hit.Node, "name") ?? hit.Node.Key,
                    GetNodeFilePath(hit.Node),
                    GetNodeLine(hit.Node),
                    GetNodeLine(hit.Node),
                    hit.Score))
                .ToList(),
            hits.Count == 0
                ? "此種類沒有命中節點；請確認 kind 是否正確，或改用 search_project_graph 全文搜尋。"
                : "如需上下游關係，請將實際 nodeId 傳給 trace_project_graph_paths。");
    }

    /// <summary>
    /// 以 BFS 從已知 Graph 節點找出上下游最短路徑。
    /// 只讀取既有 evidence-backed 關係，並透過深度與結果上限避免高 degree 節點造成圖形爆炸。
    /// </summary>
    [Description("從已知 Graph nodeId 追蹤有界的上下游鏈路。")]
    public async Task<GraphPathToolResult> TraceGraphPathsAsync(
        [Description("search_project_graph 實際回傳的完整 nodeId。")]
        string nodeId,
        [Description("最大關係深度；一般模式範圍 1 到 4，backboneOnly 模式範圍 1 到 8。")]
        int maxDepth = 3,
        [Description("最多回傳幾條最短路徑，範圍 1 到 80。")]
        int maxPaths = 30,
        [Description("true 時只沿主要可執行/資料鏈關係展開（忽略描述性/次要關係），可安全用更大深度一次取得完整資料流程。")]
        bool backboneOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        maxDepth = Math.Clamp(maxDepth, 1, backboneOnly ? MaximumBackboneDepth : MaximumGraphPathDepth);
        maxPaths = Math.Clamp(maxPaths, 1, MaximumGraphPaths);

        var paths = new List<GraphPathToolItem>();
        var queue = new Queue<GraphTraversalState>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        queue.Enqueue(new GraphTraversalState(nodeId, [], 0));
        var wasTruncated = false;

        while (queue.Count > 0 && paths.Count < maxPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var depth = queue.Peek().Depth;
            var layer = new List<GraphTraversalState>();
            while (queue.Count > 0 &&
                   queue.Peek().Depth == depth &&
                   layer.Count < maxPaths)
            {
                var state = queue.Dequeue();
                if (state.Depth < maxDepth)
                    layer.Add(state);
            }
            if (layer.Count == 0)
                continue;

            // 同一 BFS 層一次查詢全部 frontier，正式 Neo4j store 會以 UNWIND 批次讀取；
            // 這保留原本的 visited、排序與路徑上限，但避免每個節點各發一個 round trip。
            IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbor>> neighborMap;
            try
            {
                neighborMap = await _graphStore.GetNeighborsBatchAsync(
                    _projectId,
                    layer.Select(state => state.NodeId).ToArray(),
                    40,
                    _graphVersion,
                    cancellationToken);
            }
            catch (GraphStoreException exception)
            {
                return new GraphPathToolResult(
                    nodeId,
                    paths,
                    wasTruncated,
                    DescribeGraphStoreFailure(exception));
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("沒有設定 Graph Store 方法", StringComparison.Ordinal))
            {
                // 舊的測試 double 尚未覆寫批次介面時保留相容 fallback；正式 Neo4j store
                // 會走單一 UNWIND 查詢，不會進入此路徑。
                var values = await Task.WhenAll(layer.Select(async state =>
                    new
                    {
                        state.NodeId,
                        Neighbors = await _graphStore.GetNeighborsAsync(
                            _projectId,
                            state.NodeId,
                            40,
                            _graphVersion,
                            cancellationToken),
                    }));
                neighborMap = values.ToDictionary(
                    value => value.NodeId,
                    value => value.Neighbors,
                    StringComparer.Ordinal);
            }
            foreach (var current in layer)
            {
                if (!neighborMap.TryGetValue(current.NodeId, out var neighbors))
                    continue;
                var candidates = backboneOnly
                    ? neighbors.Where(neighbor => GraphRetrievalService.RelationshipWeight(neighbor.Relationship.Kind) == 100)
                    : neighbors;
                foreach (var neighbor in candidates)
                {
                    var nextNodeId = neighbor.Node.Key;
                    if (!visited.Add(nextNodeId))
                        continue;

                    var relationship = neighbor.Relationship;
                    var evidenceSource = GetRelationshipText(relationship, "sourceKind")
                        ?? GetRelationshipText(relationship, "provider")
                        ?? "ParallelExtractor";
                    var evidenceFile = GetRelationshipText(relationship, "sourceFile")
                        ?? GetRelationshipText(relationship, "filePath");
                    var evidenceLine = GetRelationshipInt(relationship, "sourceLine")
                        ?? GetRelationshipInt(relationship, "line");
                    var step = new GraphPathStepToolItem(
                        relationship.SourceKey,
                        relationship.Kind.ToString(),
                        relationship.TargetKey,
                        neighbor.Direction,
                        evidenceSource,
                        evidenceFile,
                        evidenceLine);
                    var nextSteps = current.Steps.Append(step).ToList();
                    paths.Add(new GraphPathToolItem(
                        nodeId,
                        nextNodeId,
                        neighbor.Node.Kind.ToString(),
                        GetNodeText(neighbor.Node, "role") ?? neighbor.Node.Kind.ToString(),
                        GetNodeText(neighbor.Node, "name") ?? neighbor.Node.Key,
                        GetNodeFilePath(neighbor.Node) ?? evidenceFile,
                        GetNodeLine(neighbor.Node) ?? evidenceLine,
                        nextSteps));

                    if (current.Depth + 1 < maxDepth)
                        queue.Enqueue(new GraphTraversalState(
                            nextNodeId,
                            nextSteps,
                            current.Depth + 1));
                    if (paths.Count >= maxPaths)
                    {
                        wasTruncated = queue.Count > 0 || neighbors.Count > 1;
                        break;
                    }
                }
                if (paths.Count >= maxPaths)
                    break;
            }
        }

        return new GraphPathToolResult(
            nodeId,
            paths,
            wasTruncated,
            paths.Count == 0
                ? "此 nodeId 沒有可見關係，或不是目前 active Graph 的節點。"
                : "路徑是索引證據；重要程式邏輯仍應用 read_project_file_range 確認。"
        );
    }

    #endregion

    #region 原始碼查詢工具

    /// <summary>
    /// 在目前專案執行純文字搜尋；不接受正規表示式，避免昂貴或惡意 pattern 影響服務。
    /// </summary>
    [Description("搜尋目前專案內可閱讀的原始碼與設定文件。")]
    public async Task<TextSearchToolResult> SearchTextAsync(
        [Description("要搜尋的完整文字；使用不分大小寫的 literal match。")]
        string query,
        [Description("可選副檔名，例如 .cs、.js、.tsx、.aspx、.sql；不填時搜尋所有支援格式。")]
        string? extension = null,
        [Description("最多回傳幾筆，範圍 1 到 100。")]
        int maxResults = 40,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var expectedFiles = await GetVerifiedSnapshotAsync(cancellationToken);
        query = query.Trim();
        maxResults = Math.Clamp(maxResults, 1, MaximumTextSearchResults);
        var normalizedExtension = NormalizeExtension(extension);
        var index = await ProjectSourceIndex.GetOrCreateAsync(
                _rootPath,
                expectedFiles,
                cancellationToken)
            .ConfigureAwait(false);
        var search = await index.SearchTextAsync(
            query,
            normalizedExtension,
            maxResults,
            cancellationToken).ConfigureAwait(false);
        var matches = search.Matches
            .Select(match => new TextSearchToolItem(
                ToRelativePath(match.FilePath),
                match.Line,
                match.Column,
                match.Preview))
            .ToList();
        return new TextSearchToolResult(
            query,
            matches,
            search.FilesScanned,
            search.FilesSkipped,
            search.WasTruncated);
    }

    /// <summary>
    /// 讀取專案內指定檔案區段，並在輸出加入一基行號。
    /// 絕對路徑與相對路徑都會先正規化及檢查，無法利用 .. 跳出專案根目錄。
    /// </summary>
    [Description("讀取目前專案內指定檔案的一段內容。")]
    public async Task<FileRangeToolResult> ReadFileRangeAsync(
        [Description("專案根目錄下的相對路徑，或 search_project_text 回傳的路徑。")]
        string filePath,
        [Description("開始行號，從 1 起算。")]
        int startLine = 1,
        [Description("讀取行數，範圍 1 到 2000。")]
        int lineCount = 160,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveProjectFile(filePath);
        await VerifyFileAgainstSnapshotAsync(fullPath, cancellationToken);
        startLine = Math.Max(1, startLine);
        lineCount = Math.Clamp(lineCount, 1, MaximumReadLines);

        if (startLine > MaximumReadStartLine)
            throw new ArgumentOutOfRangeException(
                nameof(startLine),
                $"startLine 不得超過 {MaximumReadStartLine}，請先用搜尋工具定位較小的行號。");

        var info = new FileInfo(fullPath);
        if (info.Length > MaximumReadFileBytes)
            throw new InvalidDataException(
                $"檔案大小超過 {MaximumReadFileBytes / (1024 * 1024)} MB 的讀取上限。");

        // 檔案區段只讀取使用者指定的實體檔案，不保留另一份內容快取；來源碼搜尋
        // 索引只負責候選產生，避免兩套快取對同一檔案回傳不同版本。
        var lines = new List<FileLineToolItem>(lineCount);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (lineNumber < startLine)
                continue;
            lines.Add(new FileLineToolItem(lineNumber, line));
            if (lines.Count >= lineCount)
                break;
        }

        var hasMore = lines.Count == lineCount &&
                      await reader.ReadLineAsync(cancellationToken) is not null;
        return new FileRangeToolResult(
            ToRelativePath(fullPath),
            startLine,
            lines,
            hasMore);
    }

    /// <summary>
    /// 使用 Roslyn 語法樹尋找 C# 定義與 identifier 引用。
    /// 此工具刻意不要求 Solution 能完整建置，因此適合舊版 .NET Framework 專案；
    /// 結果是語法層候選，overload 與跨專案型別綁定仍需搭配 Graph 或原始碼確認。
    /// </summary>
    [Description("尋找 C# 符號定義與語法層引用。")]
    public async Task<CSharpSymbolToolResult> FindCSharpSymbolAsync(
        [Description("C# identifier 或 qualified name；例如 BondTradeService 或 BondTradeService.Save。")]
        string symbol,
        [Description("是否同時回傳 identifier 引用；false 時只找定義。")]
        bool includeReferences = true,
        [Description("最多回傳幾筆，範圍 1 到 100。")]
        int maxResults = 60,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var expectedFiles = await GetVerifiedSnapshotAsync(cancellationToken);
        symbol = symbol.Trim();
        maxResults = Math.Clamp(maxResults, 1, 100);
        var identifier = symbol.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(identifier) || !SyntaxFacts.IsValidIdentifier(identifier))
            throw new ArgumentException("symbol 必須包含有效的 C# identifier。", nameof(symbol));
        var index = await ProjectSourceIndex.GetOrCreateAsync(
                _rootPath,
                expectedFiles,
                cancellationToken)
            .ConfigureAwait(false);
        var search = await index.SearchSymbolsAsync(
            symbol,
            includeReferences,
            maxResults,
            cancellationToken).ConfigureAwait(false);
        var results = search.Matches
            .Select(match => new CSharpSymbolToolItem(
                ToRelativePath(match.FilePath),
                match.Line,
                match.Column,
                match.Classification,
                match.Container,
                match.Preview))
            .ToList();

        return new CSharpSymbolToolResult(
            symbol,
            results,
            search.FilesScanned,
            search.WasTruncated,
            "此結果為 Roslyn 語法層候選；重要呼叫關係請再查 Graph 路徑或讀取原始碼。"
        );
    }

    /// <summary>
    /// 讀取單一 .cs 檔案的結構大綱（namespace/class/method/property/field/event 定義與行號範圍），
    /// 不含完整程式碼內容。用於大型檔案在呼叫 read_project_file_range 前，先快速判斷要讀哪個行號區間。
    /// </summary>
    [Description("讀取單一 C# 檔案的結構大綱，不含完整程式碼內容。")]
    public async Task<CSharpFileOutlineToolResult> OutlineCSharpFileAsync(
        [Description("專案根目錄下的相對路徑，或搜尋結果回傳的路徑；必須是 .cs 檔案。")]
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveProjectFile(filePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".cs", StringComparison.OrdinalIgnoreCase))
            return new CSharpFileOutlineToolResult(
                ToRelativePath(fullPath), [],
                "outline_csharp_file 只支援 .cs 檔案；其他格式請改用 read_project_file_range 直接讀取。");

        if (!File.Exists(fullPath))
            return new CSharpFileOutlineToolResult(
                ToRelativePath(fullPath), [],
                $"找不到指定的專案檔案：{ToRelativePath(fullPath)}；請改用 search_project_text 或 search_project_graph 確認正確路徑。");

        // 不維護檔案 watcher 或修改時間快取；每次大綱查詢只讀取指定實體檔案，
        // 避免另一套來源快取與 projectId + graphVersion 搜尋索引產生版本分歧。
        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(
            content,
            cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);
        var members = new List<CSharpFileOutlineItem>();
        foreach (var node in root.DescendantNodes().Where(node =>
                     node is BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or
                         MethodDeclarationSyntax or ConstructorDeclarationSyntax or
                         PropertyDeclarationSyntax or FieldDeclarationSyntax or EventDeclarationSyntax))
        {
            var span = tree.GetLineSpan(node.Span);
            var name = node switch
            {
                BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                ConstructorDeclarationSyntax ctor => ctor.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                EventDeclarationSyntax @event => @event.Identifier.ValueText,
                FieldDeclarationSyntax field => string.Join(
                    ", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
                _ => "?",
            };
            members.Add(new CSharpFileOutlineItem(
                span.StartLinePosition.Line + 1,
                span.EndLinePosition.Line + 1,
                DescribeOutlineNodeKind(node),
                GetContainerName(node.Parent),
                name,
                Truncate(ExtractSignatureLine(node), 300)));
            if (members.Count >= MaximumOutlineMembers)
                break;
        }

        return new CSharpFileOutlineToolResult(
            ToRelativePath(fullPath),
            members,
            members.Count == 0
                ? "此檔案沒有找到任何 class/method/property/field 定義。"
                : "只是結構大綱；實際邏輯仍需用 read_project_file_range 讀取指定行號區間確認。");
    }

    /// <summary>將大綱節點分類為易讀的中英文標籤，供 Agent 快速辨識定義種類。</summary>
    private static string DescribeOutlineNodeKind(SyntaxNode node) => node switch
    {
        BaseNamespaceDeclarationSyntax => "namespace",
        RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
            ? "record struct"
            : "record",
        ClassDeclarationSyntax => "class",
        InterfaceDeclarationSyntax => "interface",
        StructDeclarationSyntax => "struct",
        EnumDeclarationSyntax => "enum",
        MethodDeclarationSyntax => "method",
        ConstructorDeclarationSyntax => "constructor",
        PropertyDeclarationSyntax => "property",
        FieldDeclarationSyntax => "field",
        EventDeclarationSyntax => "event",
        _ => "member",
    };

    /// <summary>取節點本文到第一個 body 起始符號（{ 或 ;）為止，當作不含實作內容的簽章預覽。</summary>
    private static string ExtractSignatureLine(SyntaxNode node)
    {
        var text = node.ToString();
        var braceIndex = text.IndexOf('{');
        var semicolonIndex = text.IndexOf(';');
        var cut = braceIndex >= 0 && (semicolonIndex < 0 || braceIndex < semicolonIndex)
            ? braceIndex
            : semicolonIndex >= 0 ? semicolonIndex : text.Length;
        return text[..cut].ReplaceLineEndings(" ").Trim();
    }

    #endregion

    #region 專案資料庫唯讀查詢工具

    private static readonly string[] DatabaseObjectKindFilters = ["Table", "View", "Procedure", "Function"];

    /// <summary>
    /// 列出目前專案設定資料庫中實際存在的資料表、檢視表、預存程序或函式。
    /// 資料庫本身才是這些物件是否存在的權威來源；比對簽入版控的 .sql 檔案全文搜尋更準確也更快。
    /// </summary>
    /// <param name="nameFilter">不分大小寫的局部名稱篩選；為 null 時不篩選名稱。</param>
    /// <param name="kind">物件種類：Table、View、Procedure 或 Function。</param>
    /// <param name="provider">資料庫來源 SqlServer 或 Sqlite；未指定時合併所有已設定來源。</param>
    /// <param name="maxResults">最多回傳筆數，實際數值會限制在 1 至 200。</param>
    /// <param name="cancellationToken">取消資料庫查詢的 token。</param>
    /// <returns>依 Provider、資料庫、schema 與物件名穩定排序的結構化結果。</returns>
    public async Task<DatabaseObjectListToolResult> ListDatabaseObjectsAsync(
        [Description("可選名稱篩選（不分大小寫、局部比對）；不填時列出全部。")]
        string? nameFilter = null,
        [Description("可選物件種類篩選：Table、View、Procedure、Function；不填或無法辨識時列出全部種類。")]
        string? kind = null,
        [Description("可選資料庫來源：SqlServer 或 Sqlite；不填時合併所有已設定來源。")]
        string? provider = null,
        [Description("最多回傳幾筆，範圍 1 到 200。")]
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        if (_databases.Count == 0)
            return new DatabaseObjectListToolResult(nameFilter, kind, [], false, NoDatabaseConfiguredNotice);

        maxResults = Math.Clamp(maxResults, 1, MaximumDatabaseObjectResults);
        var normalizedKind = NormalizeDatabaseObjectKindFilter(kind);
        var trimmedNameFilter = string.IsNullOrWhiteSpace(nameFilter) ? null : nameFilter.Trim();
        var selectedDatabases = SelectDatabases(provider, out var selectionNotice);
        if (selectedDatabases.Count == 0)
            return new DatabaseObjectListToolResult(nameFilter, kind, [], false, selectionNotice);

        var objects = new List<DatabaseObjectSummary>();
        foreach (var database in selectedDatabases)
        {
            var sourceObjects = database.Provider == ProjectDatabaseProvider.SqlServer
                ? await ListSqlServerObjectsAsync(
                    database, trimmedNameFilter, normalizedKind, maxResults + 1, cancellationToken)
                : await ListSqliteObjectsAsync(
                    database, trimmedNameFilter, normalizedKind, maxResults + 1, cancellationToken);
            objects.AddRange(sourceObjects);
        }
        var ordered = objects
            .OrderBy(item => item.Provider, StringComparer.Ordinal)
            .ThenBy(item => item.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DatabaseObjectListToolResult(
            nameFilter,
            kind,
            ordered.Take(maxResults).ToArray(),
            ordered.Length > maxResults);
    }

    /// <summary>依模型指定的 Provider 篩選本專案唯讀資料庫來源。</summary>
    private IReadOnlyList<GraphDatabaseSource> SelectDatabases(
        string? provider,
        out string? notice)
    {
        notice = null;
        if (string.IsNullOrWhiteSpace(provider))
            return _databases;
        if (!Enum.TryParse<ProjectDatabaseProvider>(provider.Trim(), true, out var parsed))
        {
            notice = $"不支援的資料庫 Provider：{provider}；只接受 SqlServer 或 Sqlite。";
            return [];
        }
        var selected = _databases.Where(source => source.Provider == parsed).ToArray();
        if (selected.Length == 0)
            notice = $"此專案尚未設定 {parsed} 連線。";
        return selected;
    }

    /// <summary>把模型可能猜錯或大小寫不符的種類名稱正規化；辨識不出來時視同不篩選，不當成錯誤輸入。</summary>
    private static string? NormalizeDatabaseObjectKindFilter(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return null;
        var trimmed = kind.Trim();
        return DatabaseObjectKindFilters.FirstOrDefault(
            candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 查詢 SQL Server 系統目錄取得物件清單。type 篩選只從固定字串常數組成，
    /// 不會把 nameFilter 這類外部輸入拼進 SQL 文字；nameFilter 一律以參數化 LIKE 傳遞。
    /// </summary>
    private async Task<IReadOnlyList<DatabaseObjectSummary>> ListSqlServerObjectsAsync(
        GraphDatabaseSource database,
        string? nameFilter,
        string? kind,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var typeFilter = kind switch
        {
            "Table" => "'U'",
            "View" => "'V'",
            "Procedure" => "'P'",
            "Function" => "'FN','IF','TF'",
            _ => "'U','V','P','FN','IF','TF'",
        };
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP (@maxResults) s.name AS SchemaName, o.name AS ObjectName, o.type AS ObjectType
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ({typeFilter})
              AND (@nameFilter IS NULL OR o.name LIKE '%' + @nameFilter + '%')
            ORDER BY s.name, o.name;
            """;
        command.CommandTimeout = DatabaseCommandTimeoutSeconds;
        command.Parameters.AddWithValue("@maxResults", maxResults);
        command.Parameters.AddWithValue("@nameFilter", (object?)nameFilter ?? DBNull.Value);
        var results = new List<DatabaseObjectSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DatabaseObjectSummary(
                reader.GetString(0),
                reader.GetString(1),
                DescribeSqlServerObjectType(reader.GetString(2).Trim()),
                database.Provider.ToString(),
                database.DatabaseName));
        }
        return results;
    }

    private static string DescribeSqlServerObjectType(string type) => type switch
    {
        "U" => "Table",
        "V" => "View",
        "P" => "Procedure",
        "FN" or "IF" or "TF" => "Function",
        _ => type,
    };

    /// <summary>查詢 SQLite schema catalog 取得物件清單；SQLite 沒有預存程序/函式物件。</summary>
    private async Task<IReadOnlyList<DatabaseObjectSummary>> ListSqliteObjectsAsync(
        GraphDatabaseSource database,
        string? nameFilter,
        string? kind,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (kind is "Procedure" or "Function")
            return [];

        var typeFilter = kind switch
        {
            "Table" => "'table'",
            "View" => "'view'",
            _ => "'table','view'",
        };
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT type, name
            FROM sqlite_schema
            WHERE type IN ({typeFilter})
              AND name NOT LIKE 'sqlite_%'
              AND (@nameFilter IS NULL OR name LIKE '%' || @nameFilter || '%')
            ORDER BY type, name
            LIMIT @maxResults;
            """;
        command.Parameters.AddWithValue("@nameFilter", (object?)nameFilter ?? DBNull.Value);
        command.Parameters.AddWithValue("@maxResults", maxResults);
        var results = new List<DatabaseObjectSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DatabaseObjectSummary(
                "main",
                reader.GetString(1),
                reader.GetString(0) == "table" ? "Table" : "View",
                database.Provider.ToString(),
                database.DatabaseName));
        }
        return results;
    }

    /// <summary>
    /// 查詢目前專案設定資料庫中指定資料表或檢視表實際部署的欄位結構與外鍵；
    /// 反映的是目前真正的部署狀態，可能與簽入版控的 Schema 檔案不同步。
    /// </summary>
    /// <param name="tableName">資料表或檢視表名稱，SQL Server 可包含 schema 前綴。</param>
    /// <param name="provider">資料庫來源 SqlServer 或 Sqlite；同名物件跨來源時必須指定。</param>
    /// <param name="cancellationToken">取消資料庫查詢的 token。</param>
    /// <returns>欄位、主鍵、外鍵及資料庫來源資訊。</returns>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> 為空白。</exception>
    public async Task<DatabaseTableDescriptionToolResult> DescribeDatabaseTableAsync(
        [Description("資料表或檢視表名稱，可加 schema 前綴，例如 dbo.tblInvestmentCategory。")]
        string tableName,
        [Description("可選資料庫來源：SqlServer 或 Sqlite；同時設定兩種來源且物件同名時必須指定。")]
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        if (_databases.Count == 0)
            return new DatabaseTableDescriptionToolResult(tableName, [], [], NoDatabaseConfiguredNotice);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        var selectedDatabases = SelectDatabases(provider, out var selectionNotice);
        if (selectedDatabases.Count == 0)
            return new DatabaseTableDescriptionToolResult(tableName, [], [], selectionNotice);

        var matches = new List<DatabaseTableDescriptionToolResult>();
        foreach (var database in selectedDatabases)
        {
            var result = database.Provider == ProjectDatabaseProvider.SqlServer
                ? await DescribeSqlServerTableAsync(database, tableName, cancellationToken)
                : await DescribeSqliteTableAsync(database, tableName, cancellationToken);
            if (result.Columns.Count > 0)
                matches.Add(result);
        }
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count > 1)
        {
            return new DatabaseTableDescriptionToolResult(
                tableName,
                [],
                [],
                "多個資料庫來源存在同名物件；請指定 provider 為 SqlServer 或 Sqlite 後重試。");
        }
        return new DatabaseTableDescriptionToolResult(
            tableName,
            [],
            [],
            $"找不到資料表或檢視表：{tableName}；請先用 list_database_objects 確認正確名稱與來源。");
    }

    /// <summary>把可能包含 schema 前綴（例如 dbo.Table 或 [dbo].[Table]）的名稱拆開，供參數化查詢使用。</summary>
    private static (string? Schema, string Name) SplitSchemaQualifiedName(string qualifiedName)
    {
        var trimmed = qualifiedName.Trim();
        var parts = trimmed.Split('.', 2);
        return parts.Length == 2
            ? (parts[0].Trim('[', ']'), parts[1].Trim('[', ']'))
            : (null, trimmed.Trim('[', ']'));
    }

    private async Task<DatabaseTableDescriptionToolResult> DescribeSqlServerTableAsync(
        GraphDatabaseSource database,
        string tableName,
        CancellationToken cancellationToken)
    {
        var (schema, name) = SplitSchemaQualifiedName(tableName);
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var columns = new List<DatabaseColumnSummary>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE,
                       CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IsPrimaryKey
                FROM INFORMATION_SCHEMA.COLUMNS c
                LEFT JOIN (
                    SELECT ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.COLUMN_NAME
                    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                      ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = ku.TABLE_SCHEMA
                    WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                ) pk ON pk.TABLE_SCHEMA = c.TABLE_SCHEMA AND pk.TABLE_NAME = c.TABLE_NAME
                    AND pk.COLUMN_NAME = c.COLUMN_NAME
                WHERE c.TABLE_NAME = @tableName
                  AND (@schema IS NULL OR c.TABLE_SCHEMA = @schema)
                ORDER BY c.ORDINAL_POSITION;
                """;
            command.CommandTimeout = DatabaseCommandTimeoutSeconds;
            command.Parameters.AddWithValue("@tableName", name);
            command.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new DatabaseColumnSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                    reader.GetInt32(3) == 1));
            }
        }

        if (columns.Count == 0)
        {
            return new DatabaseTableDescriptionToolResult(
                tableName, [], [],
                $"找不到資料表或檢視表：{tableName}；請先用 list_database_objects 確認正確名稱。");
        }

        var foreignKeys = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT fk.name, OBJECT_NAME(fk.parent_object_id), OBJECT_NAME(fk.referenced_object_id)
                FROM sys.foreign_keys fk
                WHERE fk.parent_object_id = OBJECT_ID(@qualifiedName);
                """;
            command.CommandTimeout = DatabaseCommandTimeoutSeconds;
            command.Parameters.AddWithValue("@qualifiedName", schema is null ? name : $"{schema}.{name}");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                foreignKeys.Add($"{reader.GetString(0)}：{reader.GetString(1)} → {reader.GetString(2)}");
            }
        }

        return new DatabaseTableDescriptionToolResult(
            tableName,
            columns,
            foreignKeys,
            Provider: database.Provider.ToString(),
            DatabaseName: database.DatabaseName);
    }

    private async Task<DatabaseTableDescriptionToolResult> DescribeSqliteTableAsync(
        GraphDatabaseSource database,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // PRAGMA 語法不支援參數化表名；先用參數化查詢在 schema catalog 核對確實存在的名稱，
        // 避免把未經驗證的模型輸入直接嵌入 PRAGMA 陳述式。
        string? canonicalName;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = """
                SELECT name FROM sqlite_schema
                WHERE type IN ('table','view') AND name = @name COLLATE NOCASE
                LIMIT 1;
                """;
            lookup.Parameters.AddWithValue("@name", tableName.Trim());
            canonicalName = (string?)await lookup.ExecuteScalarAsync(cancellationToken);
        }

        if (canonicalName is null)
        {
            return new DatabaseTableDescriptionToolResult(
                tableName, [], [],
                $"找不到資料表或檢視表：{tableName}；請先用 list_database_objects 確認正確名稱。");
        }

        var quotedName = QuoteSqliteIdentifier(canonicalName);
        var columns = new List<DatabaseColumnSummary>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({quotedName});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new DatabaseColumnSummary(
                    reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 0, reader.GetInt32(5) > 0));
            }
        }

        var foreignKeys = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA foreign_key_list({quotedName});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                foreignKeys.Add($"{reader.GetString(3)} → {reader.GetString(2)}.{reader.GetString(4)}");
            }
        }

        return new DatabaseTableDescriptionToolResult(
            canonicalName,
            columns,
            foreignKeys,
            Provider: database.Provider.ToString(),
            DatabaseName: database.DatabaseName);
    }

    private static string QuoteSqliteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// 讀取目前專案設定資料庫中檢視表、預存程序或函式實際部署的 SQL 定義文字。
    /// 正式環境可能已被直接修改過而未同步簽入版控，這裡取得的才是目前真正在執行的邏輯。
    /// </summary>
    /// <param name="objectName">物件名稱，SQL Server 可包含 schema 前綴。</param>
    /// <param name="provider">資料庫來源 SqlServer 或 Sqlite；同名物件跨來源時必須指定。</param>
    /// <param name="cancellationToken">取消資料庫查詢的 token。</param>
    /// <returns>限制在安全長度內的 SQL 定義與資料庫來源資訊。</returns>
    /// <exception cref="ArgumentException"><paramref name="objectName"/> 為空白。</exception>
    public async Task<DatabaseObjectDefinitionToolResult> GetDatabaseObjectDefinitionAsync(
        [Description("物件名稱，可加 schema 前綴，例如 dbo.Apex_UDF_Stage3TransactionReport。")]
        string objectName,
        [Description("可選資料庫來源：SqlServer 或 Sqlite；同時設定兩種來源且物件同名時必須指定。")]
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        if (_databases.Count == 0)
            return new DatabaseObjectDefinitionToolResult(objectName, null, NoDatabaseConfiguredNotice);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        var selectedDatabases = SelectDatabases(provider, out var selectionNotice);
        if (selectedDatabases.Count == 0)
            return new DatabaseObjectDefinitionToolResult(objectName, null, selectionNotice);

        var matches = new List<DatabaseObjectDefinitionToolResult>();
        foreach (var database in selectedDatabases)
        {
            var result = database.Provider == ProjectDatabaseProvider.SqlServer
                ? await GetSqlServerObjectDefinitionAsync(database, objectName, cancellationToken)
                : await GetSqliteObjectDefinitionAsync(database, objectName, cancellationToken);
            if (result.Definition is not null)
                matches.Add(result);
        }
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count > 1)
        {
            return new DatabaseObjectDefinitionToolResult(
                objectName,
                null,
                "多個資料庫來源存在同名物件；請指定 provider 為 SqlServer 或 Sqlite 後重試。");
        }
        return new DatabaseObjectDefinitionToolResult(
            objectName,
            null,
            $"找不到物件或物件沒有可讀取的 SQL 定義：{objectName}；請先用 list_database_objects 確認正確名稱與來源。");
    }

    private static async Task<DatabaseObjectDefinitionToolResult> GetSqlServerObjectDefinitionAsync(
        GraphDatabaseSource database,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(@objectName));";
        command.CommandTimeout = DatabaseCommandTimeoutSeconds;
        command.Parameters.AddWithValue("@objectName", objectName.Trim());
        var definition = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return new DatabaseObjectDefinitionToolResult(
            objectName,
            definition is null ? null : Truncate(definition, MaximumDatabaseDefinitionCharacters),
            Provider: database.Provider.ToString(),
            DatabaseName: database.DatabaseName);
    }

    private static async Task<DatabaseObjectDefinitionToolResult> GetSqliteObjectDefinitionAsync(
        GraphDatabaseSource database,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using var sqlite = new SqliteConnection(database.ConnectionString);
        await sqlite.OpenAsync(cancellationToken);
        await using var sqliteCommand = sqlite.CreateCommand();
        sqliteCommand.CommandText = """
            SELECT sql FROM sqlite_schema WHERE name = @name COLLATE NOCASE LIMIT 1;
            """;
        sqliteCommand.Parameters.AddWithValue("@name", objectName.Trim());
        var sqliteDefinition = (string?)await sqliteCommand.ExecuteScalarAsync(cancellationToken);
        return new DatabaseObjectDefinitionToolResult(
            objectName,
            sqliteDefinition is null
                ? null
                : Truncate(sqliteDefinition, MaximumDatabaseDefinitionCharacters),
            Provider: database.Provider.ToString(),
            DatabaseName: database.DatabaseName);
    }

    #endregion

    #region 版本驗證與結構化轉換

    private string ResolveProjectFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(
            Path.IsPathFullyQualified(filePath)
                ? filePath
                : Path.Combine(_rootPath, filePath));
        if (!fullPath.Equals(_rootPath, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("只能讀取目前專案根目錄內的檔案。");
        return fullPath;
    }

    /// <summary>取得本輪固定圖譜版本的檔案快照；未提供 manifest 時維持單檔工具可用。</summary>
    private async Task<IReadOnlyDictionary<string, string>?> GetVerifiedSnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (_manifests is null || string.IsNullOrWhiteSpace(_graphVersion))
            return null;
        var files = await _manifests.GetFileSnapshotAsync(
            _projectId,
            _graphVersion,
            cancellationToken);
        if (files.Count == 0)
            throw new InvalidOperationException("目前圖譜版本沒有檔案清單，請先重新索引。");
        return files.ToDictionary(
            file => file.RelativePath.Replace('\\', '/'),
            file => file.ContentHash,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>防止 Graph 與本機檔案在同一輪回答中引用不同版本。</summary>
    private async Task VerifyFileAgainstSnapshotAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (_manifests is null || string.IsNullOrWhiteSpace(_graphVersion))
            return;
        var relativePath = ToRelativePath(fullPath);
        var files = await _manifests.GetFileSnapshotAsync(
            _projectId,
            _graphVersion,
            cancellationToken);
        var expected = files.FirstOrDefault(file =>
            file.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        if (expected is null)
            throw new InvalidOperationException("檔案不屬於目前圖譜版本，請重新索引。");

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"檔案已在索引後變更：{relativePath}；請重新索引。");
    }

    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;
        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;
        if (!SearchableExtensions.Contains(normalized))
            throw new ArgumentException($"不支援搜尋副檔名：{normalized}", nameof(extension));
        return normalized;
    }

    /// <summary>把圖譜基礎設施失敗轉成模型可採取行動的提示，避免反覆重試相同查詢。</summary>
    private static string DescribeGraphStoreFailure(GraphStoreException exception) =>
        exception.FailureKind switch
        {
            GraphStoreFailureKind.Unavailable =>
                "Graph 目前無法連線；請改用 search_project_text、find_csharp_symbol 與 read_project_file_range。",
            GraphStoreFailureKind.SchemaNotReady =>
                "Graph 索引尚未準備完成；請改用原始碼工具，不要重試相同 Graph 查詢。",
            GraphStoreFailureKind.SnapshotNotFound =>
                "本輪 Graph 快照不存在；請改用原始碼工具。",
            _ => "Graph 查詢失敗；請改用原始碼工具，不要重試相同 Graph 查詢。",
        };

    /// <summary>精準符號名稱優先於泛用高分全文命中。</summary>
    private static int ExactGraphNameBoost(GraphSearchHit hit, string query)
    {
        var requested = query.Trim();
        var normalizedRequested = NormalizeGraphName(requested);
        var names = new[]
        {
            GetNodeText(hit.Node, "name"),
            GetNodeText(hit.Node, "display_name"),
            hit.Node.Key,
        }.Where(value => !string.IsNullOrWhiteSpace(value));

        var boost = 0;
        foreach (var name in names)
        {
            if (name!.Equals(requested, StringComparison.OrdinalIgnoreCase))
                boost = Math.Max(boost, 1_000);
            else if (NormalizeGraphName(name).Equals(
                         normalizedRequested,
                         StringComparison.OrdinalIgnoreCase))
                boost = Math.Max(boost, 900);
            else if (name.Contains(requested, StringComparison.OrdinalIgnoreCase))
                boost = Math.Max(boost, 500);
        }
        return boost;
    }

    private static string NormalizeGraphName(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit));

    private static string? GetContainerName(SyntaxNode? node)
    {
        var names = node?.AncestorsAndSelf()
            .Where(item => item is BaseTypeDeclarationSyntax or MethodDeclarationSyntax)
            .Select(item => item switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                _ => string.Empty,
            })
            .Where(value => value.Length > 0)
            .Reverse()
            .ToList();
        return names is { Count: > 0 } ? string.Join('.', names) : null;
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

    /// <summary>唯讀工具呼叫的預算分類；用於分別限制不同種類工具的單輪呼叫次數，並在診斷摘要 log 中標示明細。</summary>
    public enum ToolCategory
    {
        GraphSearch,
        GraphPath,
        TextSearch,
        SymbolSearch,
        FileRead,
        GraphNodeList,
        Outline,
        DatabaseSchema,
    }

    /// <summary>
    /// 從 ParallelExtractor 節點的描述屬性安全取得單一文字值；缺少或空白時回傳 null。
    /// </summary>
    private static string? GetNodeText(
        ExtractedGraph.GraphNode node,
        string propertyName)
    {
        if (!node.Properties.TryGetValue(propertyName, out var value) || value is null)
            return null;
        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// 取得權威節點可引用的第一個 Repository 相對路徑。
    /// 單一路徑優先使用 path；CodeClass 的 source_files 會安全取第一筆。
    /// </summary>
    private static string? GetNodeFilePath(ExtractedGraph.GraphNode node)
    {
        var path = GetNodeText(node, "path");
        if (!string.IsNullOrWhiteSpace(path))
            return path;
        if (!node.Properties.TryGetValue("source_files", out var sourceFiles) ||
            sourceFiles is null)
            return null;

        return sourceFiles switch
        {
            string value when !string.IsNullOrWhiteSpace(value) => value,
            IEnumerable<string> values => values.FirstOrDefault(
                value => !string.IsNullOrWhiteSpace(value)),
            IEnumerable<object?> values => values
                .Select(value => value?.ToString())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            JsonElement element => GetFirstJsonText(element),
            _ => null,
        };
    }

    /// <summary>從 JSON 字串或字串陣列安全取得第一個非空白值。</summary>
    private static string? GetFirstJsonText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();
        if (element.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    /// <summary>
    /// 從節點描述屬性讀取一基行號；關係鏈另會優先補上直接 Evidence 的來源行號。
    /// </summary>
    private static int? GetNodeLine(ExtractedGraph.GraphNode node)
    {
        foreach (var key in new[] { "source_line", "start_line", "line" })
        {
            if (!node.Properties.TryGetValue(key, out var value) || value is null)
                continue;
            if (value is int line)
                return line;
            if (int.TryParse(value.ToString(), out line))
                return line;
        }
        return null;
    }

    private static string? GetRelationshipText(
        ExtractedGraph.GraphRelationship relationship,
        string key) =>
        relationship.Properties.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;

    private static int? GetRelationshipInt(
        ExtractedGraph.GraphRelationship relationship,
        string key) =>
        relationship.Properties.TryGetValue(key, out var value) &&
        value is not null &&
        int.TryParse(value.ToString(), out var parsed)
            ? parsed
            : null;

    private sealed record GraphTraversalState(
        string NodeId,
        IReadOnlyList<GraphPathStepToolItem> Steps,
        int Depth);

    #endregion

    #region 工具結構化輸出

    /// <summary>Graph 搜尋工具的結構化輸出。</summary>
    public sealed record GraphSearchToolResult(
        string Query,
        IReadOnlyList<GraphNodeToolItem> Hits,
        string NextAction,
        ToolBudgetStatus? Budget = null);

    /// <summary>Graph 搜尋命中的精簡節點，避免把整個 node metadata 塞進模型。</summary>
    public sealed record GraphNodeToolItem(
        string Id,
        string Kind,
        string Role,
        string Name,
        string? FilePath,
        int? StartLine,
        int? EndLine,
        double Score);

    /// <summary>Graph 路徑工具的結構化輸出。</summary>
    public sealed record GraphPathToolResult(
        string StartNodeId,
        IReadOnlyList<GraphPathToolItem> Paths,
        bool WasTruncated,
        string Note,
        ToolBudgetStatus? Budget = null);

    /// <summary>從起點到一個受影響節點的最短路徑。</summary>
    public sealed record GraphPathToolItem(
        string StartNodeId,
        string EndNodeId,
        string EndKind,
        string EndRole,
        string EndName,
        string? FilePath,
        int? SourceLine,
        IReadOnlyList<GraphPathStepToolItem> Steps);

    /// <summary>一條具備方向與信心資訊的 Graph 關係。</summary>
    public sealed record GraphPathStepToolItem(
        string SourceId,
        string Relation,
        string TargetId,
        string TraversalDirection,
        string EvidenceSource,
        string? SourceFile,
        int? SourceLine);

    /// <summary>專案文字搜尋工具的結構化輸出。</summary>
    public sealed record TextSearchToolResult(
        string Query,
        IReadOnlyList<TextSearchToolItem> Matches,
        int FilesScanned,
        int FilesSkipped,
        bool WasTruncated,
        string? Notice = null,
        ToolBudgetStatus? Budget = null);

    /// <summary>一筆帶有位置的專案文字命中。</summary>
    public sealed record TextSearchToolItem(
        string FilePath,
        int Line,
        int Column,
        string Preview);

    /// <summary>專案檔案區段工具的結構化輸出。</summary>
    public sealed record FileRangeToolResult(
        string FilePath,
        int RequestedStartLine,
        IReadOnlyList<FileLineToolItem> Lines,
        bool HasMore,
        string? Notice = null,
        ToolBudgetStatus? Budget = null);

    /// <summary>帶有一基行號的原始碼文字。</summary>
    public sealed record FileLineToolItem(int Line, string Text);

    /// <summary>C# 符號搜尋工具的結構化輸出。</summary>
    public sealed record CSharpSymbolToolResult(
        string Symbol,
        IReadOnlyList<CSharpSymbolToolItem> Matches,
        int FilesScanned,
        bool WasTruncated,
        string Note,
        ToolBudgetStatus? Budget = null);

    /// <summary>一筆 C# 定義或語法層引用。</summary>
    public sealed record CSharpSymbolToolItem(
        string FilePath,
        int Line,
        int Column,
        string Classification,
        string? Container,
        string Preview);

    /// <summary>依種類列出圖譜節點工具的結構化輸出。</summary>
    public sealed record GraphNodeListToolResult(
        string Kind,
        IReadOnlyList<GraphNodeToolItem> Nodes,
        string Note,
        ToolBudgetStatus? Budget = null);

    /// <summary>C# 檔案結構大綱工具的結構化輸出。</summary>
    public sealed record CSharpFileOutlineToolResult(
        string FilePath,
        IReadOnlyList<CSharpFileOutlineItem> Members,
        string? Notice = null,
        ToolBudgetStatus? Budget = null);

    /// <summary>一筆檔案結構大綱項目（不含完整程式碼內容）。</summary>
    public sealed record CSharpFileOutlineItem(
        int StartLine,
        int EndLine,
        string Kind,
        string? Container,
        string Name,
        string Signature);

    /// <summary>一筆資料庫物件摘要（資料表、檢視表、預存程序或函式）。</summary>
    public sealed record DatabaseObjectSummary(
        string SchemaName,
        string ObjectName,
        string Kind,
        string Provider = "",
        string DatabaseName = "");

    /// <summary>資料庫物件清單工具的結構化輸出。</summary>
    public sealed record DatabaseObjectListToolResult(
        string? NameFilter,
        string? Kind,
        IReadOnlyList<DatabaseObjectSummary> Objects,
        bool WasTruncated,
        string? Notice = null,
        ToolBudgetStatus? Budget = null);

    /// <summary>一個資料表／檢視表欄位的結構摘要。</summary>
    public sealed record DatabaseColumnSummary(
        string ColumnName,
        string DataType,
        bool IsNullable,
        bool IsPrimaryKey);

    /// <summary>資料表結構查詢工具的結構化輸出。</summary>
    public sealed record DatabaseTableDescriptionToolResult(
        string TableName,
        IReadOnlyList<DatabaseColumnSummary> Columns,
        IReadOnlyList<string> ForeignKeys,
        string? Notice = null,
        ToolBudgetStatus? Budget = null,
        string? Provider = null,
        string? DatabaseName = null);

    /// <summary>資料庫物件定義查詢工具的結構化輸出。</summary>
    public sealed record DatabaseObjectDefinitionToolResult(
        string ObjectName,
        string? Definition,
        string? Notice = null,
        ToolBudgetStatus? Budget = null,
        string? Provider = null,
        string? DatabaseName = null);

    /// <summary>工具預算耗盡時回傳給模型的可機器判讀狀態。</summary>
    public sealed record ToolBudgetStatus(
        string Status,
        bool Exhausted,
        string Scope,
        int TotalUsed,
        int TotalLimit,
        int CategoryUsed,
        int CategoryLimit,
        string Message,
        string NextAction);

    #endregion
}
