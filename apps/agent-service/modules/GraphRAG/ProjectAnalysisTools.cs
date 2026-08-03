using System.ComponentModel;
using System.Text.Json;
using AgentService.Application.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
    private const long MaximumSearchFileBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> SearchableExtensions = new(
        [
            ".cs", ".csproj", ".sln",
            ".js", ".jsx", ".ts", ".tsx",
            ".aspx", ".ascx", ".master",
            ".sql", ".json", ".xml", ".config",
            ".md", ".txt", ".yaml", ".yml",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedDirectories = new(
        [
            ".git", ".svn", ".vs", "bin", "obj", "node_modules",
            "packages", "dist", "build", "out", "target", "vendor",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly string _projectId;
    private readonly string _rootPath;
    private readonly string _rootPrefix;
    private readonly IGraphStore _graphStore;
    private readonly AgentActivityReporter? _activity;

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
        AgentActivityReporter? activity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(graphStore);

        _projectId = projectId;
        _rootPath = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _rootPrefix = _rootPath + Path.DirectorySeparatorChar;
        _graphStore = graphStore;
        _activity = activity;
    }

    /// <summary>
    /// 建立本輪 Agent 可呼叫的五個唯讀 Function Tools。
    /// 每一輪都使用綁定當前 projectId 與 rootPath 的實例，模型無法自行切換到其他專案。
    /// 工具包裝器只回報開始、完成與失敗狀態，不會把完整工具輸出直接送到前端。
    /// </summary>
    /// <returns>只包含查詢能力的 MAF 工具集合。</returns>
    public IReadOnlyList<AIFunction> CreateTools() =>
    [
        AIFunctionFactory.Create(
            (Func<string, int, CancellationToken, Task<GraphSearchToolResult>>)SearchGraphWithActivityAsync,
            "search_project_graph",
            "以自然語言、業務名稱、程式符號或資料表名稱搜尋目前專案的知識圖譜。先用此工具取得精確 nodeId，再視需要追蹤鏈路。"),
        AIFunctionFactory.Create(
            (Func<string, int, int, CancellationToken, Task<GraphPathToolResult>>)TraceGraphPathsWithActivityAsync,
            "trace_project_graph_paths",
            "從已知 nodeId 逐層查詢目前專案 Graph 的上下游關係，回傳有界的最短鏈路。必須使用 search_project_graph 實際回傳的 nodeId。"),
        AIFunctionFactory.Create(
            (Func<string, string?, int, CancellationToken, Task<TextSearchToolResult>>)SearchTextWithActivityAsync,
            "search_project_text",
            "在目前專案的 C#、JavaScript、TypeScript、ASPX、SQL 與設定文件中執行不分大小寫的純文字搜尋。適合找錯誤訊息、URL、欄位或動態呼叫。"),
        AIFunctionFactory.Create(
            (Func<string, int, int, CancellationToken, Task<FileRangeToolResult>>)ReadFileRangeWithActivityAsync,
            "read_project_file_range",
            "讀取目前專案內指定檔案的一段內容並附行號。filePath 可使用搜尋結果的相對路徑；每次最多讀取 2000 行。"),
        AIFunctionFactory.Create(
            (Func<string, bool, int, CancellationToken, Task<CSharpSymbolToolResult>>)FindCSharpSymbolWithActivityAsync,
            "find_csharp_symbol",
            "以不需要成功建置專案的 C# 語法解析，尋找 class、method、property、field 等定義及 identifier 引用。適合從符號名稱定位候選檔案。"),
    ];

    private Task<GraphSearchToolResult> SearchGraphWithActivityAsync(
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "search_project_graph",
            "搜尋知識圖譜",
            cancellationToken,
            () => SearchGraphAsync(query, limit, cancellationToken),
            result => $"找到 {result.Hits.Count} 個節點");

    private Task<GraphPathToolResult> TraceGraphPathsWithActivityAsync(
        string nodeId,
        int maxDepth,
        int maxPaths,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "trace_project_graph_paths",
            "追蹤圖譜鏈路",
            cancellationToken,
            () => TraceGraphPathsAsync(nodeId, maxDepth, maxPaths, cancellationToken),
            result => $"找到 {result.Paths.Count} 條鏈路");

    private Task<TextSearchToolResult> SearchTextWithActivityAsync(
        string query,
        string? extension,
        int maxResults,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "search_project_text",
            "搜尋專案原始碼",
            cancellationToken,
            () => SearchTextAsync(query, extension, maxResults, cancellationToken),
            result => $"找到 {result.Matches.Count} 筆文字命中");

    private Task<FileRangeToolResult> ReadFileRangeWithActivityAsync(
        string filePath,
        int startLine,
        int lineCount,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "read_project_file_range",
            "讀取原始碼區段",
            cancellationToken,
            () => ReadFileRangeAsync(filePath, startLine, lineCount, cancellationToken),
            result => $"讀取 {result.Lines.Count} 行");

    private Task<CSharpSymbolToolResult> FindCSharpSymbolWithActivityAsync(
        string symbol,
        bool includeReferences,
        int maxResults,
        CancellationToken cancellationToken) =>
        RunToolWithActivityAsync(
            "find_csharp_symbol",
            "尋找 C# 符號",
            cancellationToken,
            () => FindCSharpSymbolAsync(symbol, includeReferences, maxResults, cancellationToken),
            result => $"找到 {result.Matches.Count} 筆符號候選");

    /// <summary>
    /// 執行單一唯讀工具並回報其生命週期；工具本身的輸出仍只回傳給 Agent。
    /// </summary>
    private async Task<T> RunToolWithActivityAsync<T>(
        string tool,
        string label,
        CancellationToken cancellationToken,
        Func<Task<T>> operation,
        Func<T, string> completedDetail)
    {
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
            if (activityId is not null)
                await _activity!.CompleteAsync(
                    activityId,
                    completedDetail(result));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (activityId is not null)
                await _activity!.FailAsync(
                    activityId,
                    "工具執行已取消");
            throw;
        }
        catch
        {
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
        var hits = await _graphStore.SearchAsync(
            _projectId,
            query.Trim(),
            limit,
            cancellationToken);

        return new GraphSearchToolResult(
            query.Trim(),
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
            var current = queue.Dequeue();
            if (current.Depth >= maxDepth)
                continue;

            var neighbors = await _graphStore.GetNeighborsAsync(
                _projectId,
                current.NodeId,
                40,
                cancellationToken);
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
    public Task<TextSearchToolResult> SearchTextAsync(
        [Description("要搜尋的完整文字；使用不分大小寫的 literal match。")]
        string query,
        [Description("可選副檔名，例如 .cs、.js、.tsx、.aspx、.sql；不填時搜尋所有支援格式。")]
        string? extension = null,
        [Description("最多回傳幾筆，範圍 1 到 100。")]
        int maxResults = 40,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        maxResults = Math.Clamp(maxResults, 1, MaximumTextSearchResults);
        var normalizedExtension = NormalizeExtension(extension);
        var matches = new List<TextSearchToolItem>();
        var filesScanned = 0;
        var filesSkipped = 0;

        foreach (var file in EnumerateSearchableFiles(normalizedExtension))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (info.Length > MaximumSearchFileBytes)
                {
                    filesSkipped++;
                    continue;
                }
            }
            catch (IOException)
            {
                filesSkipped++;
                continue;
            }

            filesScanned++;
            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lineNumber++;
                    var column = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (column < 0)
                        continue;
                    matches.Add(new TextSearchToolItem(
                        ToRelativePath(file),
                        lineNumber,
                        column + 1,
                        Truncate(line.Trim(), 500)));
                    if (matches.Count >= maxResults)
                        return Task.FromResult(new TextSearchToolResult(
                            query,
                            matches,
                            filesScanned,
                            filesSkipped,
                            true));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                filesSkipped++;
            }
        }

        return Task.FromResult(new TextSearchToolResult(
            query,
            matches,
            filesScanned,
            filesSkipped,
            false));
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
        maxResults = Math.Clamp(maxResults, 1, 100);
        var identifier = symbol.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(identifier) || !SyntaxFacts.IsValidIdentifier(identifier))
            throw new ArgumentException("symbol 必須包含有效的 C# identifier。", nameof(symbol));

        var results = new List<CSharpSymbolToolItem>();
        var filesScanned = 0;
        foreach (var file in EnumerateSearchableFiles(".cs"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= maxResults)
                break;
            string source;
            try
            {
                source = await File.ReadAllTextAsync(file, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (source.Length > MaximumSearchFileBytes)
                continue;
            filesScanned++;

            var tree = CSharpSyntaxTree.ParseText(
                source,
                cancellationToken: cancellationToken);
            var root = await tree.GetRootAsync(cancellationToken);
            foreach (var token in root.DescendantTokens()
                         .Where(token => string.Equals(
                             token.ValueText,
                             identifier,
                             StringComparison.Ordinal)))
            {
                var classification = ClassifyIdentifier(token);
                if (!includeReferences && classification == "reference")
                    continue;
                var container = GetContainerName(token.Parent);
                if (symbol.Contains('.', StringComparison.Ordinal) &&
                    classification != "reference" &&
                    !QualifiedNameMatches(symbol, container, identifier))
                    continue;

                var position = tree.GetLineSpan(token.Span).StartLinePosition;
                results.Add(new CSharpSymbolToolItem(
                    ToRelativePath(file),
                    position.Line + 1,
                    position.Character + 1,
                    classification,
                    container,
                    Truncate(token.Parent?.Parent?.ToString()
                        .ReplaceLineEndings(" ").Trim() ?? identifier, 500)));
                if (results.Count >= maxResults)
                    break;
            }
        }

        return new CSharpSymbolToolResult(
            symbol,
            results,
            filesScanned,
            results.Count >= maxResults,
            "此結果為 Roslyn 語法層候選；重要呼叫關係請再查 Graph 路徑或讀取原始碼。"
        );
    }

    private IEnumerable<string> EnumerateSearchableFiles(string? extension)
    {
        if (!Directory.Exists(_rootPath))
            yield break;

        var pending = new Stack<string>();
        pending.Push(_rootPath);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory).ToList();
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in childDirectories)
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(child)))
                    pending.Push(child);
            }
            foreach (var file in files)
            {
                var fileExtension = Path.GetExtension(file);
                if (!SearchableExtensions.Contains(fileExtension))
                    continue;
                if (extension is null || fileExtension.Equals(
                        extension,
                        StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
        }
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

    private static string ClassifyIdentifier(SyntaxToken token) => token.Parent switch
    {
        BaseTypeDeclarationSyntax declaration when declaration.Identifier == token => "type-definition",
        MethodDeclarationSyntax declaration when declaration.Identifier == token => "method-definition",
        ConstructorDeclarationSyntax declaration when declaration.Identifier == token => "constructor-definition",
        PropertyDeclarationSyntax declaration when declaration.Identifier == token => "property-definition",
        EventDeclarationSyntax declaration when declaration.Identifier == token => "event-definition",
        VariableDeclaratorSyntax declaration when declaration.Identifier == token => "variable-definition",
        ParameterSyntax declaration when declaration.Identifier == token => "parameter-definition",
        EnumMemberDeclarationSyntax declaration when declaration.Identifier == token => "enum-member-definition",
        _ => "reference",
    };

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

    private static bool QualifiedNameMatches(
        string requested,
        string? container,
        string identifier)
    {
        var candidate = string.IsNullOrWhiteSpace(container)
            ? identifier
            : container.EndsWith(identifier, StringComparison.Ordinal)
                ? container
                : $"{container}.{identifier}";
        return candidate.EndsWith(requested, StringComparison.Ordinal);
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

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

    private sealed record GraphTraversalState(
        string NodeId,
        IReadOnlyList<GraphPathStepToolItem> Steps,
        int Depth);

    /// <summary>Graph 搜尋工具的結構化輸出。</summary>
    public sealed record GraphSearchToolResult(
        string Query,
        IReadOnlyList<GraphNodeToolItem> Hits,
        string NextAction);

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
        string Note);

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
        bool WasTruncated);

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
        bool HasMore);

    /// <summary>帶有一基行號的原始碼文字。</summary>
    public sealed record FileLineToolItem(int Line, string Text);

    /// <summary>C# 符號搜尋工具的結構化輸出。</summary>
    public sealed record CSharpSymbolToolResult(
        string Symbol,
        IReadOnlyList<CSharpSymbolToolItem> Matches,
        int FilesScanned,
        bool WasTruncated,
        string Note);

    /// <summary>一筆 C# 定義或語法層引用。</summary>
    public sealed record CSharpSymbolToolItem(
        string FilePath,
        int Line,
        int Column,
        string Classification,
        string? Container,
        string Preview);
}
