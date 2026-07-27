using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AgentService.Modules.GraphRAG;

/// <summary>Repository 問題的五種固定意圖；只決定檢索範圍，不推測 Graph 事實。</summary>
public enum RepositoryQuestionIntent
{
    /// <summary>定位業務功能、畫面、入口或程式所在位置。</summary>
    LocateFeature,
    /// <summary>解釋畫面到程式與資料庫的主要執行流程。</summary>
    ExplainFlow,
    /// <summary>分析修改欄位、規則或程式碼的上下游影響。</summary>
    AnalyzeImpact,
    /// <summary>尋找資料表、Stored Procedure 或欄位的讀寫位置。</summary>
    FindDataUsage,
    /// <summary>整理整個系統、商品或跨模組的高階概觀。</summary>
    SystemOverview,
}

/// <summary>描述問題需要遍歷 Graph 關係的方向。</summary>
public enum RepositoryTraversalDirection
{
    /// <summary>只沿來源走向目標，適合功能到實作的流程。</summary>
    Outgoing,
    /// <summary>只反查來源，適合資料使用處與呼叫端分析。</summary>
    Incoming,
    /// <summary>同時檢查上下游，適合修改影響分析。</summary>
    Both,
}

/// <summary>確定性問題計畫；搜尋詞、關係、方向與深度均已設上限。</summary>
public sealed record RepositoryQuestionPlan(
    RepositoryQuestionIntent Intent,
    IReadOnlyList<string> SearchTerms,
    IReadOnlySet<GraphEdgeKind> RelationKinds,
    RepositoryTraversalDirection Direction,
    int MaximumDepth);

/// <summary>描述第一輪取得哪些候選；true 不代表內容已經人工確認。</summary>
public sealed record RepositoryRetrievalCoverage(
    bool HasFeature,
    bool HasEntryPoint,
    bool HasCode,
    bool HasData,
    bool HasRelationship);

/// <summary>第一輪不足時唯一允許的一次補查；ShouldRun=false 時禁止再循環。</summary>
public sealed record RepositoryFallbackPlan(
    bool ShouldRun,
    int Attempt,
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<string> MissingEvidence);

/// <summary>以固定規則辨識意圖並展開少量投資術語，不呼叫 LLM 或要求唯一 Entity ID。</summary>
public sealed partial class RepositoryQuestionPlanner
{
    private static readonly IReadOnlyDictionary<string, string[]> Aliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["登入"] = ["AccountController", "LoginAndPasswordProcess", "ProcessLogin",
                "LoginUtility", "FormsAuthentication"],
            ["基金交易"] = ["AlternativeFundTransaction", "FundTransaction"],
            ["股票交易"] = ["EquityTransaction", "StockTransaction"],
            ["會計公報分類資料維護"] = ["AccountingPurposeCSV"],
            ["損益報表"] = ["PositionProfit_ReportKernel", "PositionProfit", "ProfitLossReport"],
            ["日結批次"] = ["BatchDayEnd", "DailyClosing", "EOD"],
            ["覆核後"] = ["AsyncConfirm", "Announcement", "Confirm"],
            ["交易存檔"] = ["Transaction", "Save", "UpdatePosition"],
            ["部位更新"] = ["PositionProcess", "InventoryReport", "UpdatePosition"],
            ["更新部位"] = ["PositionProcess", "InventoryReport", "UpdatePosition"],
            ["交割日"] = ["SettlementDate", "SettleDate", "ValueDate"],
            ["補登"] = ["BackEntry", "ManualTrade", "InsertDeal"],
            ["部位"] = ["Position", "Holding", "Inventory"],
            ["損益"] = ["ProfitLoss", "PnL"],
            ["日結"] = ["DailyClosing", "EOD"],
            ["覆核"] = ["Confirm", "Approval"],
            ["債券"] = ["Bond", "FixedIncome"],
            ["股票"] = ["Stock", "Equity"],
            ["基金"] = ["Fund"],
        };
    private static readonly IReadOnlyDictionary<RepositoryQuestionIntent, IReadOnlySet<GraphEdgeKind>>
        Relations = new Dictionary<RepositoryQuestionIntent, IReadOnlySet<GraphEdgeKind>>
        {
            [RepositoryQuestionIntent.LocateFeature] =
                EdgeSet(GraphEdgeKind.RoutesTo, GraphEdgeKind.Handles, GraphEdgeKind.Triggers),
            [RepositoryQuestionIntent.ExplainFlow] =
                EdgeSet(GraphEdgeKind.RoutesTo, GraphEdgeKind.Handles, GraphEdgeKind.Calls,
                    GraphEdgeKind.DispatchesTo, GraphEdgeKind.Reads, GraphEdgeKind.Writes,
                    GraphEdgeKind.MapsTo, GraphEdgeKind.Triggers),
            [RepositoryQuestionIntent.AnalyzeImpact] =
                EdgeSet(Enum.GetValues<GraphEdgeKind>()),
            [RepositoryQuestionIntent.FindDataUsage] =
                EdgeSet(GraphEdgeKind.RoutesTo, GraphEdgeKind.Handles, GraphEdgeKind.Reads,
                    GraphEdgeKind.Writes, GraphEdgeKind.MapsTo, GraphEdgeKind.DependsOn,
                    GraphEdgeKind.Calls),
            [RepositoryQuestionIntent.SystemOverview] =
                EdgeSet(GraphEdgeKind.RoutesTo, GraphEdgeKind.Handles, GraphEdgeKind.Triggers,
                    GraphEdgeKind.DependsOn),
        };
    private static readonly IReadOnlySet<string> StopTerms =
        new HashSet<string>([
            "哪裡", "哪些", "怎麼", "什麼", "有關", "請問", "程式", "畫面", "流程",
            "說明", "引用", "Controller", "Service", "Repository", "Method", "Table",
        ],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 分析非空白問題；精確識別碼排在 alias 與一般詞前，最多輸出十二個搜尋詞。
    /// </summary>
    /// <param name="question">使用者原始問題；空白問題會拒絕處理。</param>
    /// <returns>固定意圖、搜尋詞、關係白名單、方向與深度。</returns>
    public RepositoryQuestionPlan Plan(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var intent = Classify(question);
        var terms = IdentifierRegex().Matches(question)
            .Select(match => match.Value.Trim('\'', '"', '`'))
            .Where(value => value.Length >= 3 && !StopTerms.Contains(value))
            .Concat(Aliases.Where(pair =>
                    question.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => new[] { pair.Key }.Concat(pair.Value)))
            .Concat(TermRegex().Matches(question).Select(match => match.Value)
                .Where(term => term.Length >= 2 && !StopTerms.Contains(term)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        return new RepositoryQuestionPlan(
            intent,
            terms.Length == 0 ? [question.Trim()] : terms,
            Relations[intent],
            intent switch
            {
                RepositoryQuestionIntent.AnalyzeImpact => RepositoryTraversalDirection.Both,
                RepositoryQuestionIntent.FindDataUsage => RepositoryTraversalDirection.Incoming,
                _ => RepositoryTraversalDirection.Outgoing,
            },
            intent == RepositoryQuestionIntent.SystemOverview ? 2 : 3);
    }

    /// <summary>
    /// 依意圖檢查 coverage；previousAttempts=1 時只回報缺口，不允許第二次 fallback。
    /// 第一輪 evidence 找到的 method、SP、table 可加入補查詞，但總數仍限制十二個。
    /// </summary>
    /// <param name="plan">第一輪使用的確定性問題計畫。</param>
    /// <param name="coverage">第一輪實際取得的候選層級。</param>
    /// <param name="discoveredIdentifiers">第一輪找到的 method、SP 或 table 識別碼。</param>
    /// <param name="previousAttempts">已執行次數，只允許零或一。</param>
    /// <returns>是否補查、補查詞與仍缺少的 evidence。</returns>
    public RepositoryFallbackPlan PlanFallback(
        RepositoryQuestionPlan plan,
        RepositoryRetrievalCoverage coverage,
        IEnumerable<string>? discoveredIdentifiers = null,
        int previousAttempts = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(coverage);
        if (previousAttempts is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(previousAttempts), "補查次數只允許零或一。");
        var missing = MissingEvidence(plan.Intent, coverage);
        var shouldRun = missing.Count > 0 && previousAttempts == 0;
        return new RepositoryFallbackPlan(
            shouldRun,
            shouldRun ? 1 : previousAttempts,
            plan.SearchTerms.Concat(discoveredIdentifiers ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray(),
            missing);
    }

    /// <summary>較具體的影響與資料訊號優先，避免被一般「流程」用語覆蓋。</summary>
    private static RepositoryQuestionIntent Classify(string question)
    {
        if (Has(question, "整個系統", "系統總覽", "架構總覽", "有哪些模組", "跨模組"))
            return RepositoryQuestionIntent.SystemOverview;
        if (Has(question, "影響", "修改", "變更", "刪除", "調整"))
            return RepositoryQuestionIntent.AnalyzeImpact;
        if (Has(question, "資料表", "欄位", "stored procedure", "誰讀取", "誰寫入",
                "讀寫", "table", "column"))
            return RepositoryQuestionIntent.FindDataUsage;
        return Has(question, "流程", "怎麼走", "為什麼", "如何處理", "呼叫鏈", "bug",
                "存檔後", "沒有更新", "未更新", "異常", "失敗")
            ? RepositoryQuestionIntent.ExplainFlow
            : RepositoryQuestionIntent.LocateFeature;
    }

    /// <summary>回傳各意圖最低 evidence 缺口；候選存在不代表最終答案已確認。</summary>
    private static IReadOnlyList<string> MissingEvidence(
        RepositoryQuestionIntent intent,
        RepositoryRetrievalCoverage value)
    {
        var required = intent switch
        {
            RepositoryQuestionIntent.LocateFeature =>
                new[] { (value.HasFeature, "Feature"), (value.HasEntryPoint || value.HasCode, "EntryPoint 或 Code") },
            RepositoryQuestionIntent.ExplainFlow =>
                [(value.HasEntryPoint, "EntryPoint"), (value.HasCode, "Code"), (value.HasRelationship, "執行關係")],
            RepositoryQuestionIntent.AnalyzeImpact =>
                [(value.HasCode || value.HasData, "Code 或 Data"), (value.HasRelationship, "上下游關係")],
            RepositoryQuestionIntent.FindDataUsage =>
                [(value.HasData, "Data"), (value.HasCode, "Code"), (value.HasRelationship, "讀寫關係")],
            _ => [(value.HasFeature, "Feature")],
        };
        return required.Where(item => !item.Item1).Select(item => item.Item2).ToArray();
    }

    /// <summary>建立固定關係白名單。</summary>
    private static IReadOnlySet<GraphEdgeKind> EdgeSet(params GraphEdgeKind[] values) =>
        new HashSet<GraphEdgeKind>(values);

    /// <summary>比對固定問題訊號。</summary>
    private static bool Has(string value, params string[] signals) =>
        signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));

    /// <summary>辨識 route、symbol、table 與檔案路徑等高精確度技術識別碼。</summary>
    [GeneratedRegex(@"[`'\""]?[A-Za-z_][A-Za-z0-9_.:/\\-]{2,}[`'\""]?")]
    private static partial Regex IdentifierRegex();

    /// <summary>抽取自然語言中的連續中文詞及一般程式識別碼。</summary>
    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}]{2,8}|[A-Za-z_][A-Za-z0-9_]{2,}")]
    private static partial Regex TermRegex();
}

/// <summary>等待讀取的 canonical evidence；Score 與 NodeKind 只用於排序及 coverage。</summary>
public sealed record SourceEvidenceCandidate(
    GraphEvidence Evidence,
    double Score,
    GraphNodeKind? NodeKind = null,
    string? Symbol = null);

/// <summary>通過 root、文字檔與行數檢查的原始碼片段。</summary>
public sealed record SourceEvidenceSnippet(
    string RelativePath,
    int StartLine,
    int EndLine,
    string? Symbol,
    string Content,
    double Score,
    GraphNodeKind? NodeKind);

/// <summary>安全讀取結果；Diagnostics 說明拒絕與截斷，Snippets 不會寫回 Graph。</summary>
public sealed record SourceEvidenceReadResult(
    IReadOnlyList<SourceEvidenceSnippet> Snippets,
    IReadOnlyList<string> Diagnostics,
    bool WasTruncated);

/// <summary>
/// 安全讀取 Graph evidence 指向的實際程式碼；只允許 root 內文字檔，
/// 拒絕 generated、reparse point、binary 與過大檔案。
/// </summary>
public sealed class SourceEvidenceReader
{
    private const int MaximumFileBytes = 1_048_576;
    private const int MaximumSnippetCount = 10;
    private const int MaximumContextCharacters = 20_000;
    private const int MaximumMemberLines = 120;
    private const int MaximumOtherLines = 80;
    private readonly string _root;
    private readonly string _rootPrefix;
    private static readonly ConcurrentDictionary<string, SourceEvidenceSnippet> SnippetCache =
        new(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Extensions = new HashSet<string>(
        [".cs", ".java", ".js", ".jsx", ".ts", ".tsx", ".sql", ".aspx", ".ascx",
            ".cshtml", ".json", ".xml", ".yaml", ".yml", ".config", ".properties"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> BlockedSegments = new HashSet<string>(
        ["bin", "obj", "node_modules", ".git", "dist", "packages", "generated"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 建立 Reader；root 必須存在且本身不可是 junction/reparse point，
    /// 建構失敗時不會降級為未限制的檔案系統讀取。
    /// </summary>
    /// <param name="projectRoot">唯一允許讀取的專案根目錄。</param>
    public SourceEvidenceReader(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"專案根目錄不存在：{_root}");
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("專案根目錄不可為 junction 或 reparse point。");
        _rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// 依 Intent coverage 與分數讀取最多十個片段及 20K 字元，不同檔案最多四路平行；
    /// 同檔重疊範圍合併，超限時捨棄低分片段並留下診斷。
    /// </summary>
    /// <param name="candidates">Canonical Graph 指向的 source evidence 候選。</param>
    /// <param name="intent">決定 EntryPoint／Code／Data 的最低片段 coverage。</param>
    /// <param name="manifestVersion">目前 active manifest；參與 cache 隔離，不得使用空值。</param>
    /// <param name="cancellationToken">取消平行讀取的 token。</param>
    /// <returns>有界片段、拒絕或截斷診斷，以及是否截斷。</returns>
    public async Task<SourceEvidenceReadResult> ReadAsync(
        IEnumerable<SourceEvidenceCandidate> candidates,
        RepositoryQuestionIntent intent,
        string manifestVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestVersion);
        var input = candidates.ToArray();
        var diagnostics = new ConcurrentQueue<string>();
        var resolved = input.OrderByDescending(value => value.Score)
            .Select(value => Resolve(value, diagnostics)).OfType<Resolved>().ToArray();
        var allGroups = resolved.GroupBy(
            value => value.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var quotaGroups = CoverageKinds(intent)
            .Select(kind => allGroups.FirstOrDefault(group =>
                group.Any(value => value.Candidate.NodeKind == kind)))
            .Where(group => group is not null)
            .Cast<IGrouping<string, Resolved>>();
        var groups = quotaGroups.Concat(allGroups)
            .DistinctBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumSnippetCount).ToArray();
        if (allGroups.Length > groups.Length)
            diagnostics.Enqueue("Source evidence 檔案數已依十個檔案上限截斷。");
        var snippets = new ConcurrentBag<SourceEvidenceSnippet>();
        await Parallel.ForEachAsync(groups, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken,
        }, async (group, token) =>
        {
            foreach (var snippet in await ReadFileAsync(
                         group, manifestVersion, diagnostics, token))
                snippets.Add(snippet);
        });
        var selected = new List<SourceEvidenceSnippet>();
        var characters = 0;
        var ordered = snippets.OrderByDescending(value => value.Score).ToArray();
        var quota = CoverageKinds(intent)
            .Select(kind => ordered.FirstOrDefault(value => value.NodeKind == kind))
            .Where(value => value is not null)
            .Cast<SourceEvidenceSnippet>()
            .ToArray();
        var firstPerFile = ordered
            .GroupBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var prioritized = quota.Concat(firstPerFile).Concat(ordered)
            .DistinctBy(value => (value.RelativePath, value.StartLine, value.EndLine));
        foreach (var snippet in prioritized)
        {
            if (selected.Count >= MaximumSnippetCount ||
                characters + snippet.Content.Length > MaximumContextCharacters) continue;
            selected.Add(snippet);
            characters += snippet.Content.Length;
        }
        var truncated = snippets.Count > selected.Count ||
                        resolved.Length > groups.Sum(group => group.Count());
        var missedCoverage = CoverageKinds(intent)
            .Where(kind => ordered.Any(value => value.NodeKind == kind) &&
                           selected.All(value => value.NodeKind != kind))
            .ToArray();
        if (missedCoverage.Length > 0)
        {
            truncated = true;
            diagnostics.Enqueue(
                $"{intent} source coverage 因 20,000 字元上限缺少：{string.Join(", ", missedCoverage)}。");
        }
        if (truncated) diagnostics.Enqueue("Source evidence 已依片段數或 20,000 字元上限截斷。");
        return new SourceEvidenceReadResult(selected, diagnostics.Distinct().ToArray(), truncated);
    }

    /// <summary>
    /// 回傳各 Intent 需要優先保留的 source 層級；只有候選實際存在時才要求 quota，
    /// 因此缺少某層不會憑空製造 evidence。
    /// </summary>
    /// <param name="intent">Planner 判定的固定問題意圖。</param>
    /// <returns>依優先順序排列且不重複的 Graph node kind。</returns>
    private static IReadOnlyList<GraphNodeKind> CoverageKinds(RepositoryQuestionIntent intent) =>
        intent switch
        {
            RepositoryQuestionIntent.LocateFeature =>
                [GraphNodeKind.Feature, GraphNodeKind.EntryPoint, GraphNodeKind.Code],
            RepositoryQuestionIntent.ExplainFlow =>
                [GraphNodeKind.EntryPoint, GraphNodeKind.Code, GraphNodeKind.Data],
            RepositoryQuestionIntent.AnalyzeImpact =>
                [GraphNodeKind.Code, GraphNodeKind.Data, GraphNodeKind.EntryPoint],
            RepositoryQuestionIntent.FindDataUsage =>
                [GraphNodeKind.Data, GraphNodeKind.Code],
            _ => [GraphNodeKind.Feature],
        };

    /// <summary>
    /// Graph 完全沒有可靠 seed 時，以固定檔案數、大小與時間限制做唯一一次文字補查。
    /// 此結果只是 source candidate，不建立 Graph node 或 edge；逾時或取消時保留診斷。
    /// </summary>
    /// <param name="searchTerms">Planner 已限制數量的業務別名與識別碼。</param>
    /// <param name="cancellationToken">取消列舉與讀檔的 token。</param>
    /// <returns>最多十個、總量不超過 20K 字元的文字片段。</returns>
    public async Task<SourceEvidenceReadResult> SearchFallbackAsync(
        IReadOnlyList<string> searchTerms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchTerms);
        var terms = searchTerms.Where(value => value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        if (terms.Length == 0)
            return new SourceEvidenceReadResult([], ["全文補查沒有可用搜尋詞。"], false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var matches = new List<SourceEvidenceSnippet>();
        var visited = 0;
        try
        {
            foreach (var file in EnumerateSafeFiles(timeout.Token))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (++visited > 2_000 || matches.Count >= MaximumSnippetCount) break;
                var relative = Path.GetRelativePath(_root, file);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(BlockedSegments.Contains) ||
                    IsGenerated(relative) || !Extensions.Contains(Path.GetExtension(file)))
                    continue;
                var info = new FileInfo(file);
                if (info.Length > 262_144 || ContainsReparsePoint(file)) continue;
                var lines = await File.ReadAllLinesAsync(file, timeout.Token);
                if (lines.Any(value => value.Contains('\0'))) continue;
                var line = Array.FindIndex(lines, value =>
                    terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)));
                if (line < 0) continue;
                var start = Math.Max(0, line - 8);
                var end = Math.Min(lines.Length - 1, line + 8);
                var content = string.Join('\n',
                    lines[start..(end + 1)].Select((value, index) =>
                        $"{start + index + 1,5} | {value}"));
                matches.Add(new SourceEvidenceSnippet(
                    relative.Replace('\\', '/'), start + 1, end + 1,
                    null, content, 0.25, null));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SourceEvidenceReadResult(
                matches, ["全文補查已達 2 秒上限，僅保留目前候選。"], true);
        }
        return new SourceEvidenceReadResult(
            matches,
            matches.Count == 0
                ? ["Graph 與有界全文補查都沒有找到可靠候選。"]
                : ["Graph 無可靠 seed；以下片段來自唯一一次有界全文補查，尚未形成關係證據。"],
            visited > 2_000 || matches.Count >= MaximumSnippetCount);
    }

    /// <summary>
    /// 逐層列舉 root 內檔案，進入子目錄前先拒絕 reparse point 與輸出目錄。
    /// 不使用 AllDirectories，避免 Windows junction 在檔案驗證前就被框架自動走訪。
    /// </summary>
    /// <param name="cancellationToken">取消尚未開始的目錄列舉。</param>
    /// <returns>只來自實體專案目錄的檔案路徑。</returns>
    private IEnumerable<string> EnumerateSafeFiles(
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(_root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> children;
            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
                children = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (BlockedSegments.Contains(name) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    continue;
                pending.Push(child);
            }
        }
    }

    /// <summary>驗證相對路徑、root 邊界、副檔名、大小及所有實體路徑元件。</summary>
    private Resolved? Resolve(
        SourceEvidenceCandidate candidate,
        ConcurrentQueue<string> diagnostics)
    {
        var artifact = candidate.Evidence.Artifact;
        if (string.IsNullOrWhiteSpace(artifact) || Path.IsPathRooted(artifact))
            return Reject("Evidence artifact 必須是專案內相對路徑。");
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(
                _root, artifact.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Reject($"Evidence 路徑格式無效：{artifact}");
        }
        var relative = Path.GetRelativePath(_root, fullPath);
        if (!fullPath.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            return Reject($"已拒絕根目錄外路徑：{artifact}");
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(BlockedSegments.Contains) || IsGenerated(relative))
            return Reject($"已略過 generated output：{relative}");
        if (!Extensions.Contains(Path.GetExtension(fullPath)))
            return Reject($"已略過非允許文字副檔名：{relative}");
        if (!File.Exists(fullPath))
            return Reject($"Evidence 檔案不存在：{relative}");
        if (ContainsReparsePoint(fullPath))
            return Reject($"已拒絕 junction 或 reparse-point 路徑：{relative}");
        if (new FileInfo(fullPath).Length > MaximumFileBytes)
            return Reject($"Evidence 檔案超過 {MaximumFileBytes} bytes：{relative}");
        return new Resolved(fullPath, relative.Replace('\\', '/'), candidate);

        Resolved? Reject(string message)
        {
            diagnostics.Enqueue(message);
            return null;
        }
    }

    /// <summary>逐層拒絕 root 以下的 reparse point，避免 junction 導向外部資料。</summary>
    private bool ContainsReparsePoint(string fullPath)
    {
        var current = _root;
        foreach (var segment in Path.GetRelativePath(_root, fullPath)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// 單一安全檔案只讀且只 parse 一次；以 SHA-256 內容指紋建立 range snippet cache，
    /// NUL 內容視為 binary。讀檔或解析失敗由呼叫端視為 source 未確認，不會偽造片段。
    /// </summary>
    /// <param name="group">相同已驗證實體檔案的 evidence 候選。</param>
    /// <param name="manifestVersion">隔離不同 active graph manifest 的 cache 身分。</param>
    /// <param name="diagnostics">執行緒安全的降級診斷佇列。</param>
    /// <param name="cancellationToken">取消實際檔案讀取。</param>
    /// <returns>合併且有界的 source snippets。</returns>
    private static async Task<IReadOnlyList<SourceEvidenceSnippet>> ReadFileAsync(
        IGrouping<string, Resolved> group,
        string manifestVersion,
        ConcurrentQueue<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(group.Key, cancellationToken);
        if (text.IndexOf('\0') >= 0)
        {
            diagnostics.Enqueue($"已略過疑似 binary 檔案：{group.First().RelativePath}");
            return [];
        }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var isCSharp = Path.GetExtension(group.Key)
            .Equals(".cs", StringComparison.OrdinalIgnoreCase);
        var tree = isCSharp ? CSharpSyntaxTree.ParseText(text) : null;
        var root = tree?.GetRoot();
        var sourceText = tree?.GetText();
        var maximum = isCSharp ? MaximumMemberLines : MaximumOtherLines;
        var ranges = Merge(group.Select(value => Range(
                value, root, sourceText, lines.Length, isCSharp))
            .OrderBy(value => value.Start).ThenBy(value => value.End).ToArray(), maximum);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var relativePath = group.First().RelativePath;
        return ranges.Select(range =>
        {
            var cacheKey = BuildCacheKey(
                manifestVersion, relativePath, fingerprint, range.Start, range.End);
            if (SnippetCache.TryGetValue(cacheKey, out var cached))
                return cached with
                {
                    Score = range.Score,
                    NodeKind = range.Kind,
                    Symbol = range.Symbol ?? cached.Symbol,
                };
            var content = new StringBuilder();
            for (var line = range.Start; line <= range.End; line++)
                content.Append(line.ToString().PadLeft(5)).Append(" | ")
                    .AppendLine(lines[line - 1]);
            var snippet = new SourceEvidenceSnippet(
                relativePath, range.Start, range.End, range.Symbol,
                content.ToString(), range.Score, range.Kind);
            if (SnippetCache.Count >= 512) SnippetCache.Clear();
            SnippetCache[cacheKey] = snippet;
            return snippet;
        }).ToArray();
    }

    /// <summary>
    /// 建立包含 evidence 行的範圍；C# 使用該檔唯一一次 parse 的 root，
    /// 其他文字檔取前後文，最後分別限制 120/80 行。
    /// </summary>
    /// <param name="value">已通過路徑驗證的 evidence。</param>
    /// <param name="root">C# syntax root；非 C# 為 null。</param>
    /// <param name="sourceText">與 root 相同 parse 的文字行索引。</param>
    /// <param name="lineCount">檔案總行數，用於夾限不可信行號。</param>
    /// <param name="isCSharp">是否套用 member expansion。</param>
    /// <returns>一基底且不超過單片段上限的行號範圍。</returns>
    private static LineRange Range(
        Resolved value,
        Microsoft.CodeAnalysis.SyntaxNode? root,
        SourceText? sourceText,
        int lineCount,
        bool isCSharp)
    {
        var requested = Math.Clamp(value.Candidate.Evidence.StartLine ?? 1, 1, lineCount);
        var start = Math.Max(1, requested - 8);
        var end = Math.Min(lineCount,
            Math.Max(requested, value.Candidate.Evidence.EndLine ?? requested) + 8);
        if (root is not null && sourceText is not null)
        {
            // Type-level evidence 的行號通常指向 class 宣告；若候選已帶精確方法名稱，
            // 優先定位該方法，才能讀到真正的驗證／存檔邏輯，而非只讀類別開頭。
            var namedMethod = string.IsNullOrWhiteSpace(value.Candidate.Symbol)
                ? null
                : root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(method => string.Equals(
                        method.Identifier.ValueText,
                        value.Candidate.Symbol,
                        StringComparison.OrdinalIgnoreCase));
            var member = namedMethod ??
                root.FindToken(sourceText.Lines[requested - 1].Start)
                    .Parent?.AncestorsAndSelf()
                    .OfType<MemberDeclarationSyntax>()
                    .FirstOrDefault();
            if (member is not null)
            {
                var span = member.GetLocation().GetLineSpan();
                start = span.StartLinePosition.Line + 1;
                end = span.EndLinePosition.Line + 1;
                requested = start;
            }
        }
        var maximum = isCSharp ? MaximumMemberLines : MaximumOtherLines;
        if (end - start + 1 > maximum)
        {
            start = Math.Max(start, requested - maximum / 2);
            end = Math.Min(end, start + maximum - 1);
            start = Math.Max(1, end - maximum + 1);
        }
        return new LineRange(start, end, value.Candidate.Score,
            value.Candidate.NodeKind, value.Candidate.Symbol);
    }

    /// <summary>
    /// 建立 process-memory snippet cache key；manifest、正規化相對路徑、
    /// SHA-256 內容指紋與實際範圍缺一不可，避免舊檔或錯誤範圍命中。
    /// </summary>
    /// <param name="manifestVersion">Active graph manifest/revision。</param>
    /// <param name="relativePath">已通過 root 驗證的正規化相對路徑。</param>
    /// <param name="contentFingerprint">完整文字內容的 SHA-256。</param>
    /// <param name="startLine">一基底起始行。</param>
    /// <param name="endLine">一基底結束行。</param>
    /// <returns>不含原始碼內容的穩定 cache key。</returns>
    internal static string BuildCacheKey(
        string manifestVersion,
        string relativePath,
        string contentFingerprint,
        int startLine,
        int endLine) =>
        string.Join('|', manifestVersion, relativePath.Replace('\\', '/'),
            contentFingerprint, $"{startLine}:{endLine}");

    /// <summary>只合併不會突破單片段行數上限的重疊範圍。</summary>
    private static IReadOnlyList<LineRange> Merge(
        IReadOnlyList<LineRange> ranges,
        int maximumLines)
    {
        var result = new List<LineRange>();
        foreach (var range in ranges)
        {
            var length = result.Count == 0 ? 0 :
                Math.Max(result[^1].End, range.End) - Math.Min(result[^1].Start, range.Start) + 1;
            if (result.Count == 0 || range.Start > result[^1].End + 1 || length > maximumLines)
            {
                result.Add(range);
                continue;
            }
            var previous = result[^1];
            result[^1] = previous with
            {
                End = Math.Max(previous.End, range.End),
                Score = Math.Max(previous.Score, range.Score),
                Symbol = previous.Symbol ?? range.Symbol,
                Kind = previous.Kind ?? range.Kind,
            };
        }
        return result;
    }

    /// <summary>排除常見編譯器或設計器產生檔。</summary>
    private static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>已驗證路徑與原候選的內部組合。</summary>
    private sealed record Resolved(
        string FullPath,
        string RelativePath,
        SourceEvidenceCandidate Candidate);

    /// <summary>一基底行號範圍與合併排序資料。</summary>
    private sealed record LineRange(
        int Start,
        int End,
        double Score,
        GraphNodeKind? Kind,
        string? Symbol);
}

/// <summary>把 Graph、source 與診斷編譯成區分 fact、inference、unknown 的固定 Context。</summary>
public sealed class GraphContextCompiler
{
    /// <summary>
    /// 建立預設不超過 20K 字元的 LLM context；graph 路徑優先，
    /// source 依分數加入，超限時只捨棄完整 section，不截斷 code fence 或主要路徑。
    /// </summary>
    /// <param name="plan">確定性問題計畫。</param>
    /// <param name="graph">有界 Graph retrieval 結果。</param>
    /// <param name="source">安全 Reader 的片段與失敗診斷。</param>
    /// <param name="maximumCharacters">完整 context 上限，允許 2K 至 100K。</param>
    /// <returns>完整 section 組成且不超過上限的 evidence context。</returns>
    public string Compile(
        RepositoryQuestionPlan plan,
        GraphRetrievalContext graph,
        SourceEvidenceReadResult source,
        int maximumCharacters = 20_000)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(source);
        if (maximumCharacters is < 2_000 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var sections = new List<string>
        {
            Section(value => value.AppendLine("# 問題判斷")
                .Append("Intent: ").AppendLine(plan.Intent.ToString())
                .Append("搜尋詞: ").AppendLine(string.Join(", ", plan.SearchTerms))),
            NodeSection("命中的業務功能",
                graph.Nodes.Where(value => value.Node.Kind == GraphNodeKind.Feature)),
            RelationshipSection(graph),
            NodeSection("Stored Procedure／Table 依賴",
                graph.Nodes.Where(value => value.Node.Kind == GraphNodeKind.Data)),
        };

        // Source 必須排在影響候選前；讀取失敗時明說未驗證，不能把 Graph fact 當程式行為。
        sections.Add(source.Snippets.Count == 0
            ? "# 原始碼證據\n- 未取得可讀 source；Canonical Graph 事實不得解讀為已確認程式行為。\n"
            : "# 已讀取的原始碼證據\n");
        sections.AddRange(source.Snippets.OrderByDescending(value => value.Score)
            .Select(SourceSection));
        sections.Add(NodeSection("直接影響", graph.Nodes.Where(value => value.Depth == 1)));
        sections.Add(NodeSection("間接影響", graph.Nodes.Where(value => value.Depth > 1)));
        sections.Add(Section(value => AppendEvidence(value, graph)));
        var diagnostics = graph.Diagnostics.Concat(source.Diagnostics).Distinct().ToArray();
        sections.Add(Section(value =>
        {
            value.AppendLine("# 未知資訊與截斷");
            foreach (var diagnostic in diagnostics.Take(30))
                value.Append("- ").AppendLine(diagnostic);
            if (diagnostics.Length == 0)
                value.AppendLine("- 未觀察到截斷；動態呼叫與未索引外部系統仍不在保證範圍。");
        }));

        var output = new StringBuilder();
        var truncated = false;
        const string truncatedSection = "# Context 截斷\n- 已捨棄超出字元上限的完整低優先 section。\n";
        foreach (var section in sections)
        {
            // 預留完整截斷說明，確保任何 section 被捨棄時，模型一定看得到降級狀態。
            if (output.Length + section.Length + truncatedSection.Length <= maximumCharacters)
                output.Append(section);
            else
                truncated = true;
        }
        if (truncated) output.Append(truncatedSection);
        return output.ToString();
    }

    /// <summary>建立 node section；節點名稱只是 Graph candidate，不等於程式行為證明。</summary>
    /// <param name="title">區段標題。</param>
    /// <param name="nodes">依 retrieval 分數排序的節點。</param>
    /// <returns>最多十個節點的完整 Markdown section。</returns>
    private static string NodeSection(
        string title,
        IEnumerable<ScoredGraphNode> nodes)
    {
        return Section(builder =>
        {
            builder.Append("# ").AppendLine(title);
            var values = nodes.Take(10).ToArray();
            foreach (var value in values)
                builder.Append("- [").Append(value.Node.Kind).Append('/').Append(value.Node.Role)
                    .Append("] ").Append(value.Node.Name)
                    .Append(" score=").Append(value.Score.ToString("0.000"))
                    .Append(" location=").Append(value.Node.FilePath ?? "(database)")
                    .Append(':').AppendLine(value.Node.StartLine?.ToString() ?? "?");
            if (values.Length == 0) builder.AppendLine("- 未取得");
        });
    }

    /// <summary>建立可讀關係路徑；只使用 canonical edge 並以節點名稱取代不易讀的 ID。</summary>
    /// <param name="graph">有界 Graph retrieval 結果。</param>
    /// <returns>最多三十條關係的完整 Markdown section。</returns>
    private static string RelationshipSection(GraphRetrievalContext graph) =>
        Section(builder =>
        {
            builder.AppendLine("# 主要關係路徑");
            var names = graph.Nodes.ToDictionary(
                value => value.Node.Id, value => value.Node.Name, StringComparer.Ordinal);
            foreach (var edge in graph.Edges.Take(30))
                builder.Append("- ").Append(names.GetValueOrDefault(edge.SourceId, edge.SourceId))
                    .Append(" --").Append(edge.Kind.ToString().ToUpperInvariant()).Append("--> ")
                    .AppendLine(names.GetValueOrDefault(edge.TargetId, edge.TargetId));
            if (graph.Edges.Count == 0)
                builder.AppendLine("- 未取得可連接的 canonical edge");
        });

    /// <summary>把單一 snippet 包成完整 code fence，讓 budget 裁剪不會留下破損 Markdown。</summary>
    /// <param name="snippet">已通過安全 Reader 的 source snippet。</param>
    /// <returns>含路徑、symbol、行號與完整 code fence 的 section。</returns>
    private static string SourceSection(SourceEvidenceSnippet snippet)
    {
        var symbol = string.IsNullOrWhiteSpace(snippet.Symbol) ? "" : $" ({snippet.Symbol})";
        return $"""
            ## {snippet.RelativePath}:{snippet.StartLine}-{snippet.EndLine}{symbol}
            ```text
            {snippet.Content.TrimEnd()}
            ```

            """;
    }

    /// <summary>
    /// 依 confidence 分隔 canonical fact 與 heuristic；即使 Exact 也只證明 Graph 抽取事實，
    /// 不宣稱 source 讀取成功，且 Community 摘要不會進入此區。
    /// </summary>
    /// <param name="builder">目標 section builder。</param>
    /// <param name="graph">提供 node/edge evidence 的 retrieval 結果。</param>
    private static void AppendEvidence(StringBuilder builder, GraphRetrievalContext graph)
    {
        var evidence = graph.Nodes.SelectMany(value => value.Node.Evidence)
            .Concat(graph.Edges.SelectMany(edge => edge.Evidence)).Distinct().ToArray();
        builder.AppendLine("# Canonical Graph 事實（不代表 source 行為已驗證）");
        foreach (var item in evidence.Where(value =>
                     value.Confidence is GraphConfidence.Exact or GraphConfidence.Resolved).Take(20))
            builder.Append("- ").Append(item.Reason).Append(" [").Append(item.Artifact)
                .Append(':').Append(item.StartLine?.ToString() ?? "?").AppendLine("]");
        builder.AppendLine("# 推論（需要原始碼或資料庫證據再確認）");
        var inferred = evidence.Where(value =>
                value.Confidence is GraphConfidence.Heuristic or GraphConfidence.Inferred)
            .Take(10).ToArray();
        foreach (var item in inferred)
            builder.Append("- ").Append(item.Reason).Append(" [")
                .Append(item.Artifact).AppendLine("]");
        if (inferred.Length == 0) builder.AppendLine("- 無");
    }

    /// <summary>以暫存 builder 產生不可再切割的完整 section。</summary>
    /// <param name="write">只負責寫入單一 section 的動作。</param>
    /// <returns>以換行結尾的完整 section。</returns>
    private static string Section(Action<StringBuilder> write)
    {
        var builder = new StringBuilder();
        write(builder);
        if (builder.Length > 0 && builder[^1] != '\n') builder.AppendLine();
        return builder.ToString();
    }
}
