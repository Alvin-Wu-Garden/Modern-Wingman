using System.ComponentModel;
using System.Text.Json;
using AgentService.Application.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.AI;

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
    private const int MaximumReadLines = 2_000;
    private const int MaximumReadStartLine = 100_000;
    private const long MaximumReadFileBytes = 16 * 1024 * 1024;
    // 必須與 ProjectConversationPreparationService 的 Agent 指令一致。
    // 相同參數的快取命中不計次；只有真正執行 I/O 的工具呼叫才會消耗預算。
    private const int MaximumToolCalls = 8;
    private const int MaximumGraphSearchCalls = 4;
    private const int MaximumGraphPathCalls = 6;
    private const int MaximumTextSearchCalls = 8;
    private const int MaximumSymbolSearchCalls = 6;
    private const int MaximumFileReadCalls = 16;
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
    // 每個專案解析回合固定使用同一個 immutable snapshot，避免索引發布中途
    // 讓 search 與 trace 分別讀到不同 Graph 版本；一般舊呼叫可保留 null，
    // 由 Graph Store 退回 active 版本。
    private readonly string? _graphVersion;
    private readonly AgentActivityReporter? _activity;
    private readonly object _toolCacheGate = new();
    private readonly Dictionary<string, object> _toolCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<object>> _toolInFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<ToolCategory, int> _toolCounts = new();
    private int _totalToolCalls;

    /// <summary>
    /// 建立綁定單一專案的唯讀工具集合。
    /// </summary>
    /// <param name="projectId">Graph 儲存使用的 Modern Wingman 專案識別碼。</param>
    /// <param name="rootPath">專案原始碼根目錄；所有檔案操作都限制在此目錄內。</param>
    /// <param name="graphStore">目前專案 Graph 的唯讀查詢來源。</param>
    /// <param name="activity">目前問答要求的進度事件回報器；可為 null。</param>
    public ProjectAnalysisTools(
        string projectId,
        string rootPath,
        IGraphStore graphStore,
        AgentActivityReporter? activity = null,
        string? graphVersion = null)
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
        _activity = activity;
    }

    /// <summary>
    /// 由檔案 watcher 通知工具快取某一個檔案已失效。
    /// 只移除受影響的目錄清單與檔案內容，不觸發任何全專案重新掃描。
    /// </summary>
    public static void InvalidateFileCatalog(string changedPath)
    {
        // watcher 事件可能同時影響目錄清單、文字倒排索引及 Roslyn 符號索引；
        // 統一移除該專案的 immutable snapshot，下一次查詢會重新建立一致版本。
        ProjectSourceIndex.InvalidateForPath(changedPath);
    }

    /// <summary>
    /// 建立本輪 Agent 可呼叫的五個唯讀 Function Tools。
    /// 每一輪都使用綁定當前 projectId 與 rootPath 的實例，模型無法自行切換到其他專案。
    /// 工具包裝器只回報開始、完成與失敗狀態，不會把完整工具輸出直接送到前端。
    /// </summary>
    /// <returns>只包含查詢能力的 MAF 工具集合。</returns>
    public IReadOnlyList<AIFunction> CreateTools(bool includeGraphTools = true)
    {
        var tools = new List<AIFunction>(includeGraphTools ? 5 : 3);
        if (includeGraphTools)
        {
            tools.Add(AIFunctionFactory.Create(
                (Func<string, int, CancellationToken, Task<GraphSearchToolResult>>)SearchGraphWithActivityAsync,
                "search_project_graph",
                "以自然語言、業務名稱、程式符號或資料表名稱搜尋目前專案的知識圖譜。先用此工具取得精確 nodeId，再視需要追蹤鏈路。"));
            tools.Add(AIFunctionFactory.Create(
                (Func<string, int, int, CancellationToken, Task<GraphPathToolResult>>)TraceGraphPathsWithActivityAsync,
                "trace_project_graph_paths",
                "從已知 nodeId 逐層查詢目前專案 Graph 的上下游關係，回傳有界的最短鏈路。必須使用 search_project_graph 實際回傳的 nodeId。"));
        }

        tools.Add(AIFunctionFactory.Create(
            (Func<string, string?, int, CancellationToken, Task<TextSearchToolResult>>)SearchTextWithActivityAsync,
            "search_project_text",
            "在目前專案的 C#、JavaScript、TypeScript、ASPX、SQL 與設定文件中執行不分大小寫的純文字搜尋。適合找錯誤訊息、URL、欄位或動態呼叫。"));
        tools.Add(AIFunctionFactory.Create(
            (Func<string, int, int, CancellationToken, Task<FileRangeToolResult>>)ReadFileRangeWithActivityAsync,
            "read_project_file_range",
            "讀取目前專案內指定檔案的一段內容並附行號。filePath 可使用搜尋結果的相對路徑；每次最多讀取 2000 行。"));
        tools.Add(AIFunctionFactory.Create(
            (Func<string, bool, int, CancellationToken, Task<CSharpSymbolToolResult>>)FindCSharpSymbolWithActivityAsync,
            "find_csharp_symbol",
            "以不需要成功建置專案的 C# 語法解析，尋找 class、method、property、field 等定義及 identifier 引用。適合從符號名稱定位候選檔案。"));
        return tools;
    }

    private Task<GraphSearchToolResult> SearchGraphWithActivityAsync(
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "search_project_graph",
            "搜尋知識圖譜",
            cancellationToken,
            ToolCategory.GraphSearch,
            $"{query.Trim()}|{Math.Clamp(limit, 1, MaximumGraphSearchResults)}",
            () => SearchGraphAsync(query, limit, cancellationToken),
            result => $"找到 {result.Hits.Count} 個節點",
            budget => new GraphSearchToolResult(
                query.Trim(),
                [],
                budget.NextAction,
                budget));

    private Task<GraphPathToolResult> TraceGraphPathsWithActivityAsync(
        string nodeId,
        int maxDepth,
        int maxPaths,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "trace_project_graph_paths",
            "追蹤圖譜鏈路",
            cancellationToken,
            ToolCategory.GraphPath,
            $"{nodeId.Trim()}|{Math.Clamp(maxDepth, 1, 4)}|{Math.Clamp(maxPaths, 1, MaximumGraphPaths)}",
            () => TraceGraphPathsAsync(nodeId, maxDepth, maxPaths, cancellationToken),
            result => $"找到 {result.Paths.Count} 條鏈路",
            budget => new GraphPathToolResult(
                nodeId.Trim(),
                [],
                false,
                budget.Message,
                budget));

    private Task<TextSearchToolResult> SearchTextWithActivityAsync(
        string query,
        string? extension,
        int maxResults,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "search_project_text",
            "搜尋專案原始碼",
            cancellationToken,
            ToolCategory.TextSearch,
            $"{NormalizeSearchQueryForCache(query)}|{NormalizeExtension(extension) ?? "*"}|{Math.Clamp(maxResults, 1, MaximumTextSearchResults)}",
            () => SearchTextAsync(query, extension, maxResults, cancellationToken),
            result => $"找到 {result.Matches.Count} 筆文字命中",
            budget => new TextSearchToolResult(
                query.Trim(),
                [],
                0,
                0,
                false,
                budget));

    private Task<FileRangeToolResult> ReadFileRangeWithActivityAsync(
        string filePath,
        int startLine,
        int lineCount,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "read_project_file_range",
            "讀取原始碼區段",
            cancellationToken,
            ToolCategory.FileRead,
            $"{NormalizeFilePathForCache(filePath)}|{Math.Clamp(startLine, 1, MaximumReadStartLine)}|{Math.Clamp(lineCount, 1, MaximumReadLines)}",
            () => ReadFileRangeAsync(filePath, startLine, lineCount, cancellationToken),
            result => $"讀取 {result.Lines.Count} 行",
            budget => new FileRangeToolResult(
                filePath.Trim(),
                Math.Max(1, startLine),
                [],
                false,
                budget));

    private Task<CSharpSymbolToolResult> FindCSharpSymbolWithActivityAsync(
        string symbol,
        bool includeReferences,
        int maxResults,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "find_csharp_symbol",
            "尋找 C# 符號",
            cancellationToken,
            ToolCategory.SymbolSearch,
            $"{symbol.Trim()}|{includeReferences}|{Math.Clamp(maxResults, 1, 100)}",
            () => FindCSharpSymbolAsync(symbol, includeReferences, maxResults, cancellationToken),
            result => $"找到 {result.Matches.Count} 筆符號候選",
            budget => new CSharpSymbolToolResult(
                symbol.Trim(),
                [],
                0,
                false,
                budget.Message,
                budget));

    /// <summary>
    /// 執行單一唯讀工具並回報其生命週期；工具本身的輸出仍只回傳給 Agent。
    /// </summary>
    private async Task<T> RunToolWithActivityAsync<T>(
        string tool,
        string label,
        CancellationToken cancellationToken,
        ToolCategory category,
        string cacheKeySuffix,
        Func<Task<T>> operation,
        Func<T, string> completedDetail,
        Func<ToolBudgetStatus, T> budgetExhaustedResult)
    {
        var cacheKey = $"{tool}|{cacheKeySuffix}";
        Task<object>? sharedTask = null;
        TaskCompletionSource<object>? ownerCompletion = null;
        ToolBudgetStatus? exhaustedBudget = null;
        lock (_toolCacheGate)
        {
            if (_toolCache.TryGetValue(cacheKey, out var cached))
                return (T)cached;
            if (_toolInFlight.TryGetValue(cacheKey, out sharedTask))
            {
                // 完全相同參數正在執行時，共用同一個工作，避免 Agent 重複觸發磁碟或 Neo4j I/O。
            }
            else
            {
                var categoryLimit = category switch
                {
                    ToolCategory.GraphSearch => MaximumGraphSearchCalls,
                    ToolCategory.GraphPath => MaximumGraphPathCalls,
                    ToolCategory.TextSearch => MaximumTextSearchCalls,
                    ToolCategory.SymbolSearch => MaximumSymbolSearchCalls,
                    ToolCategory.FileRead => MaximumFileReadCalls,
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
                    _totalToolCalls++;
                    _toolCounts[category] = _toolCounts.GetValueOrDefault(category) + 1;
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
            return budgetExhaustedResult(exhaustedBudget);

        if (ownerCompletion is null)
            return (T)(await sharedTask!.WaitAsync(cancellationToken).ConfigureAwait(false));

        var activityId = _activity is null
            ? null
            : await _activity.StartAsync(
                "tool.started",
                label,
                tool,
                "正在執行唯讀專案分析工具");
        try
        {
            var result = await operation();
            lock (_toolCacheGate)
            {
                _toolCache[cacheKey] = result!;
                _toolInFlight.Remove(cacheKey);
            }
            ownerCompletion.TrySetResult(result!);
            if (activityId is not null)
                await _activity!.CompleteAsync(
                    activityId,
                    completedDetail(result));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_toolCacheGate)
                _toolInFlight.Remove(cacheKey);
            ownerCompletion.TrySetCanceled(cancellationToken);
            if (activityId is not null)
                await _activity!.FailAsync(
                    activityId,
                    "工具執行已取消");
            throw;
        }
        catch
        {
            lock (_toolCacheGate)
                _toolInFlight.Remove(cacheKey);
            ownerCompletion.TrySetException(new InvalidOperationException(
                $"{tool} 工具執行失敗。"));
            if (activityId is not null)
                await _activity!.FailAsync(
                    activityId,
                    "工具執行失敗");
            throw;
        }
    }

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
            hits = await _graphStore.SearchAsync(
                _projectId,
                GraphRetrievalService.BuildViewerLuceneQuery(query),
                limit,
                _graphVersion,
                cancellationToken);
        }
        catch (GraphStoreException exception)
        {
            // Graph 索引／連線錯誤是可預期的降級狀態，不應讓整個 Agent 回合
            // 變成例外；回傳穩定狀態讓模型改用原始碼工具。
            return new GraphSearchToolResult(
                query.Trim(),
                [],
                DescribeGraphStoreFailure(exception));
        }

        return new GraphSearchToolResult(
            query.Trim(),
            hits.Select(hit => new GraphNodeToolItem(
                    hit.Node.Key,
                    hit.Node.Kind.ToString(),
                    GetNodeText(hit.Node, "role") ?? hit.Node.Kind.ToString(),
                    GetNodeText(hit.Node, "name") ?? hit.Node.Key,
                    GetNodeFilePath(hit.Node),
                    GetNodeLine(hit.Node),
                    GetNodeEndLine(hit.Node),
                    hit.Score))
                .ToList(),
            hits.Count == 0
                ? "Graph 沒有命中；請改用 search_project_text 尋找原始字串或符號。"
                : "如需上下游關係，請將實際 nodeId 傳給 trace_project_graph_paths。"
        );
    }

    /// <summary>
    /// 以 BFS 從已知 Graph 節點找出上下游最短路徑。
    /// 只讀取既有 evidence-backed 關係，並透過深度與結果上限避免高 degree 節點造成圖形爆炸。
    /// </summary>
    [Description("從已知 Graph nodeId 追蹤有界的上下游鏈路。")]
    public async Task<GraphPathToolResult> TraceGraphPathsAsync(
        [Description("search_project_graph 實際回傳的完整 nodeId。")]
        string nodeId,
        [Description("最大關係深度，範圍 1 到 4。")]
        int maxDepth = 3,
        [Description("最多回傳幾條最短路徑，範圍 1 到 80。")]
        int maxPaths = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        maxDepth = Math.Clamp(maxDepth, 1, 4);
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
                try
                {
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
                catch (GraphStoreException fallbackException)
                {
                    return new GraphPathToolResult(
                        nodeId,
                        paths,
                        wasTruncated,
                        DescribeGraphStoreFailure(fallbackException));
                }
            }
            foreach (var current in layer)
            {
                if (!neighborMap.TryGetValue(current.NodeId, out var neighbors))
                    continue;
                foreach (var neighbor in neighbors)
                {
                    var nextNodeId = neighbor.Node.Key;
                    if (!visited.Add(nextNodeId))
                        continue;

                    var relationship = neighbor.Relationship;
                    var step = new GraphPathStepToolItem(
                        relationship.SourceKey,
                        relationship.Kind.ToString(),
                        relationship.TargetKey,
                        neighbor.Direction,
                        relationship.Evidence.SourceKind.ToString(),
                        relationship.Evidence.SourceFile,
                        relationship.Evidence.SourceLine);
                    var nextSteps = current.Steps.Append(step).ToList();
                    paths.Add(new GraphPathToolItem(
                        nodeId,
                        nextNodeId,
                        neighbor.Node.Kind.ToString(),
                        GetNodeText(neighbor.Node, "role") ?? neighbor.Node.Kind.ToString(),
                        GetNodeText(neighbor.Node, "name") ?? neighbor.Node.Key,
                        GetNodeFilePath(neighbor.Node) ?? relationship.Evidence.SourceFile,
                        GetNodeLine(neighbor.Node) ?? relationship.Evidence.SourceLine,
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
        query = query.Trim();
        maxResults = Math.Clamp(maxResults, 1, MaximumTextSearchResults);
        var normalizedExtension = NormalizeExtension(extension);
        var index = await ProjectSourceIndex.GetOrCreateAsync(_rootPath, cancellationToken)
            .ConfigureAwait(false);
        var search = index.SearchText(query, normalizedExtension, maxResults, cancellationToken);
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
        startLine = Math.Max(1, startLine);
        lineCount = Math.Clamp(lineCount, 1, MaximumReadLines);

        if (startLine > MaximumReadStartLine)
            throw new ArgumentOutOfRangeException(
                nameof(startLine),
                $"startLine 不得超過 {MaximumReadStartLine}，請先用搜尋工具定位較小的行號。 ");

        var info = new FileInfo(fullPath);
        if (info.Length > MaximumReadFileBytes)
            throw new InvalidDataException(
                $"檔案大小超過 {MaximumReadFileBytes / (1024 * 1024)} MB 的讀取上限。 ");

        // 只有本輪先前已建立索引時才重用；直接讀取單一檔案不應被迫掃描整個專案。
        ProjectSourceIndex.TryGetExisting(_rootPath, out var index);
        var indexedRange = index?.ReadFileRange(fullPath, startLine, lineCount);
        if (indexedRange is not null)
        {
            return new FileRangeToolResult(
                ToRelativePath(fullPath),
                startLine,
                indexedRange.Lines
                    .Select(line => new FileLineToolItem(line.Line, line.Text))
                    .ToList(),
                indexedRange.HasMore);
        }

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
        symbol = symbol.Trim();
        maxResults = Math.Clamp(maxResults, 1, 100);
        var identifier = symbol.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(identifier) ||
            !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(identifier))
            throw new ArgumentException("symbol 必須包含有效的 C# identifier。", nameof(symbol));
        var index = await ProjectSourceIndex.GetOrCreateAsync(_rootPath, cancellationToken)
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
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("找不到指定的專案檔案。", filePath);
        return fullPath;
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

    /// <summary>把 Graph 基礎設施失敗轉成模型可採取行動的繁中提示。</summary>
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

    /// <summary>將大小寫不同但語意相同的 literal 搜尋統一成快取 key。</summary>
    private static string NormalizeSearchQueryForCache(string query) =>
        query.Trim().ToUpperInvariant();

    /// <summary>將相對與絕對檔案路徑統一成同一個快取 key，避免重複讀取磁碟。</summary>
    private string NormalizeFilePathForCache(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(filePath)
                    ? filePath.Trim()
                    : Path.Combine(_rootPath, filePath.Trim()));
            return fullPath.ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            return filePath.Trim().ToUpperInvariant();
        }
    }

    private enum ToolCategory
    {
        GraphSearch,
        GraphPath,
        TextSearch,
        SymbolSearch,
        FileRead,
    }

    /// <summary>
    /// 從 FBL 權威節點的描述屬性安全取得單一文字值；缺少或空白時回傳 null。
    /// </summary>
    private static string? GetNodeText(
        FblAuthority.GraphNode node,
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
    private static string? GetNodeFilePath(FblAuthority.GraphNode node)
    {
        var path = GetNodeText(node, "file_path") ?? GetNodeText(node, "path");
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
    private static int? GetNodeLine(FblAuthority.GraphNode node)
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

    /// <summary>讀取語意節點的結束行；未提供時使用開始行。</summary>
    private static int? GetNodeEndLine(FblAuthority.GraphNode node)
    {
        if (node.Properties.TryGetValue("end_line", out var value) && value is not null)
        {
            if (value is int line)
                return line;
            if (int.TryParse(value.ToString(), out line))
                return line;
        }
        return GetNodeLine(node);
    }

    private sealed record GraphTraversalState(
        string NodeId,
        IReadOnlyList<GraphPathStepToolItem> Steps,
        int Depth);

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

    /// <summary>
    /// 工具預算用盡時回傳給模型的結構化狀態。這不是執行例外；模型應停止呼叫工具，
    /// 使用已取得的證據完成回答，並誠實列出尚未確認的部分。
    /// </summary>
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

    /// <summary>一筆 C# 定義或語法層引用。</summary>
    public sealed record CSharpSymbolToolItem(
        string FilePath,
        int Line,
        int Column,
        string Classification,
        string? Container,
        string Preview);
}
