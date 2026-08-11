using System.Collections;
using System.Reflection;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Modules.GraphRAG;
using AgentService.Modules.GraphRAG.FblAuthority;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>
/// 驗證專案原始碼工具的兩個核心安全與效能邊界。
/// 測試只在工作區 temp 建立短生命週期資料，並於 finally 中清除，避免污染人工測試環境。
/// </summary>
public sealed class ProjectAnalysisToolsRegressionTests
{
    [Fact]
    public async Task ReadFileRangeAsync_超過兩千行時_應限制回傳範圍()
    {
        var root = CreateTestRoot();
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(root, "large.txt"),
                Enumerable.Range(1, 2_005).Select(number => $"line-{number}"));

            var tools = CreateTools(root);
            var result = await tools.ReadFileRangeAsync("large.txt", 1, 9_999);

            Assert.Equal(2_000, result.Lines.Count);
            Assert.Equal(2_000, result.Lines[^1].Line);
            Assert.True(result.HasMore);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task ReadFileRangeAsync_相對路徑跳出專案根目錄時_應拒絕讀取()
    {
        var root = CreateTestRoot();
        try
        {
            var tools = CreateTools(root);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                tools.ReadFileRangeAsync("../outside.txt", 1, 10));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task CreateTools_工具總預算用盡時_應回傳結構化狀態而非拋出例外()
    {
        var root = CreateTestRoot();
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(root, "budget.txt"),
                Enumerable.Range(1, 20).Select(number => $"line-{number}"));
            var tool = CreateTools(root)
                .CreateTools(includeGraphTools: false)
                .Single(item => item.Name == "read_project_file_range");

            for (var index = 1; index <= 8; index++)
            {
                await tool.InvokeAsync(new AIFunctionArguments
                {
                    ["filePath"] = "budget.txt",
                    ["startLine"] = index,
                    ["lineCount"] = 1,
                });
            }

            var result = await tool.InvokeAsync(new AIFunctionArguments
            {
                ["filePath"] = "budget.txt",
                ["startLine"] = 9,
                ["lineCount"] = 1,
            });
            var json = result is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(result);
            var budget = GetPropertyIgnoreCase(json, "budget");

            Assert.Equal(
                "budget_exhausted",
                GetPropertyIgnoreCase(budget, "status").GetString());
            Assert.True(GetPropertyIgnoreCase(budget, "exhausted").GetBoolean());
            Assert.Equal(8, GetPropertyIgnoreCase(budget, "totalUsed").GetInt32());
            Assert.Contains(
                "直接回答使用者",
                GetPropertyIgnoreCase(budget, "nextAction").GetString());
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void CreateTools_Graph不可用時_不應把Graph工具提供給模型()
    {
        var root = CreateTestRoot();
        try
        {
            var names = CreateTools(root)
                .CreateTools(includeGraphTools: false)
                .Select(tool => tool.Name)
                .ToArray();

            Assert.Equal(3, names.Length);
            Assert.DoesNotContain("search_project_graph", names);
            Assert.DoesNotContain("trace_project_graph_paths", names);
            Assert.Contains("search_project_text", names);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void BuildQueryPlan_應拆解PascalCase並支援中英文反向別名()
    {
        var queries = GetQueryPlanTexts(
            "請追蹤 SettlementDate 在 BondTradeController 的交割流程",
            20);

        Assert.Contains("Settlement Date", queries, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Bond Trade Controller", queries, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("交割", queries, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SettleDate", queries, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildQueryPlan_查詢變體很少時_仍應保留完整問題()
    {
        const string question =
            "比較 BondTradeController SettlementService PositionRepository 的影響";
        var queries = GetQueryPlanTexts(question, 2);

        Assert.Contains(question, queries, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildQueryPlan_長問題應有界截斷而非因原始問題被丟棄而失敗()
    {
        var question = string.Join(' ', Enumerable.Repeat(
            "請分析 BondTradeController 與 SettlementService 的完整上下游影響",
            20));

        var queries = GetQueryPlanTexts(question, 10);

        Assert.NotEmpty(queries);
        Assert.All(queries, query => Assert.InRange(query.Length, 2, 100));
    }

    [Fact]
    public void BuildGraphSearchLuceneQuery_應跳脫控制字元並保留詞首搜尋()
    {
        var query = GraphRetrievalService.BuildGraphSearchLuceneQuery(
            "BondTradeController:/trade?mode=edit");

        Assert.Contains("BondTradeController\\:\\/trade", query);
        Assert.Contains("BondTradeController\\:\\/trade*", query);
        Assert.DoesNotContain("?", query);
        Assert.DoesNotContain(":/", query);
    }

    [Fact]
    public void BuildViewerLuceneQuery_應套用別名與PascalCase拆詞()
    {
        var aliasQuery = GraphRetrievalService.BuildViewerLuceneQuery("交割");
        var identifierQuery = GraphRetrievalService.BuildViewerLuceneQuery(
            "BondTradeController");

        Assert.Contains("Settlement", aliasQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SettleDate", aliasQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bond", identifierQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Trade", identifierQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchSeedCandidatesAsync_精準名稱應優先於高分泛用節點()
    {
        var exact = GraphNode.Create(
            GraphNodeKind.CodeClass,
            "class:BondTradeController",
            new Dictionary<string, object?> { ["name"] = "BondTradeController" });
        var generic = GraphNode.Create(
            GraphNodeKind.CodeClass,
            "class:GenericTradeController",
            new Dictionary<string, object?> { ["name"] = "GenericTradeController" });
        var store = DispatchProxy.Create<IGraphStore, SearchGraphStoreProxy>();
        ((SearchGraphStoreProxy)(object)store).Hits =
        [
            new GraphSearchHit(generic, 999),
            new GraphSearchHit(exact, 0.01),
        ];
        var service = new GraphRetrievalService(
            store,
            Options.Create(new GraphRetrievalOptions()),
            NullLogger<GraphRetrievalService>.Instance);

        var results = await service.SearchSeedCandidatesAsync(
            "test-project",
            "BondTradeController",
            10);

        Assert.Equal(exact.Key, results[0].Node.Key);
        Assert.All(
            ((SearchGraphStoreProxy)(object)store).Queries,
            query => Assert.DoesNotContain(":/", query));
    }

    [Fact]
    public async Task BuildAnswerPromptAsync_Deterministic零命中時_應只執行新增Rewrite查詢()
    {
        var rewrittenNode = GraphNode.Create(
            GraphNodeKind.DatabaseObject,
            "db:ReconciliationLedger",
            new Dictionary<string, object?> { ["name"] = "ReconciliationLedger" });
        var store = DispatchProxy.Create<IGraphStore, RewriteGraphStoreProxy>();
        ((RewriteGraphStoreProxy)(object)store).RewriteHit =
            new GraphSearchHit(rewrittenNode, 5);
        var llm = new RewriteLlmCompletionService(
            """{"queries":[],"terms":[],"aliases":["Reconcile"]}""");
        var service = new GraphRetrievalService(
            store,
            Options.Create(new GraphRetrievalOptions
            {
                MaximumQueryVariants = 10,
                MaximumLlmQueryVariants = 6,
            }),
            NullLogger<GraphRetrievalService>.Instance,
            llm);

        var prompt = await service.BuildAnswerPromptAsync(
            "test-project",
            "D:\\test-project",
            "帳務勾稽",
            providerProfileId: "provider",
            modelId: "model");

        Assert.Equal(1, llm.CallCount);
        Assert.Equal(2, ((RewriteGraphStoreProxy)(object)store).Queries.Count);
        Assert.Single(
            ((RewriteGraphStoreProxy)(object)store).Queries,
            query => query.Contains("Reconcile", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ReconciliationLedger", prompt);
    }

    [Fact]
    public async Task BuildAnswerPromptAsync_使用者取消Rewrite時_不應吞掉取消訊號()
    {
        var store = DispatchProxy.Create<IGraphStore, RewriteGraphStoreProxy>();
        var service = new GraphRetrievalService(
            store,
            Options.Create(new GraphRetrievalOptions()),
            NullLogger<GraphRetrievalService>.Instance,
            new CancelingLlmCompletionService());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.BuildAnswerPromptAsync(
            "test-project",
            "D:\\test-project",
            "帳務勾稽",
            cancellation.Token,
            "provider",
            "model"));
    }

    private static ProjectAnalysisTools CreateTools(string root) =>
        new(
            "core-regression-project",
            root,
            DispatchProxy.Create<IGraphStore, ThrowingGraphStoreProxy>());

    private static IReadOnlyList<string> GetQueryPlanTexts(
        string question,
        int maximumVariants)
    {
        var method = typeof(GraphRetrievalService).GetMethod(
            "BuildQueryPlan",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到 BuildQueryPlan。 ");
        var plan = method.Invoke(null, [question, maximumVariants])
            ?? throw new InvalidOperationException("BuildQueryPlan 沒有回傳結果。 ");
        var queries = plan.GetType().GetProperty("Queries")?.GetValue(plan) as IEnumerable
            ?? throw new InvalidOperationException("Query Plan 缺少 Queries。 ");
        return queries.Cast<object>()
            .Select(query => query.GetType().GetProperty("Text")?.GetValue(query)?.ToString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .ToArray();
    }

    private static JsonElement GetPropertyIgnoreCase(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        throw new InvalidOperationException($"JSON 結果缺少 {name}。 ");
    }

    private static string CreateTestRoot()
    {
        var workspace = FindWorkspaceRoot();
        var temp = Path.Combine(workspace, "temp");
        Directory.CreateDirectory(temp);
        var root = Path.Combine(temp, $"core-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !(Directory.Exists(Path.Combine(directory.FullName, "apps")) &&
                 Directory.Exists(Path.Combine(directory.FullName, "docs")) &&
                 File.Exists(Path.Combine(directory.FullName, "package.json"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("找不到 Modern Wingman 工作區根目錄。");
    }

    private static void DeleteTestRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var expectedTemp = Path.Combine(FindWorkspaceRoot(), "temp") +
                           Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(expectedTemp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒絕清理工作區 temp 以外的測試目錄。");

        if (Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }

    private class ThrowingGraphStoreProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"原始碼邊界測試不應呼叫 Graph Store：{targetMethod?.Name}");
    }

    private class SearchGraphStoreProxy : DispatchProxy
    {
        public IReadOnlyList<GraphSearchHit> Hits { get; set; } = [];
        public List<string> Queries { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGraphStore.SearchAsync))
            {
                Queries.Add((string)args![1]!);
                return Task.FromResult(Hits);
            }
            throw new InvalidOperationException(
                $"搜尋排序測試不應呼叫 Graph Store：{targetMethod?.Name}");
        }
    }

    private class RewriteGraphStoreProxy : DispatchProxy
    {
        public GraphSearchHit? RewriteHit { get; set; }
        public List<string> Queries { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IGraphStore.GetActiveManifestAsync):
                    return Task.FromResult<string?>("graph-v1");
                case nameof(IGraphStore.SearchAsync):
                {
                    var query = (string)args![1]!;
                    Queries.Add(query);
                    IReadOnlyList<GraphSearchHit> hits =
                        query.Contains("Reconcile", StringComparison.OrdinalIgnoreCase) &&
                        RewriteHit is not null
                            ? [RewriteHit]
                            : [];
                    return Task.FromResult(hits);
                }
                case nameof(IGraphStore.GetNeighborsBatchAsync):
                {
                    var nodeIds = (IReadOnlyList<string>)args![1]!;
                    IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbor>> result =
                        nodeIds.ToDictionary(
                            nodeId => nodeId,
                            _ => (IReadOnlyList<GraphNeighbor>)[],
                            StringComparer.Ordinal);
                    return Task.FromResult(result);
                }
                default:
                    throw new InvalidOperationException(
                        $"Query Rewrite 測試不應呼叫 Graph Store：{targetMethod?.Name}");
            }
        }
    }

    private sealed class RewriteLlmCompletionService(string response)
        : ILlmCompletionService
    {
        public int CallCount { get; private set; }

        public Task<string> CompleteAsync(
            string prompt,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(response);
        }

        public Task<string> CompleteAsync(
            string prompt,
            string? providerProfileId,
            string? modelId,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(response);
        }
    }

    private sealed class CancelingLlmCompletionService : ILlmCompletionService
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken ct = default) => WaitAsync(ct);

        public Task<string> CompleteAsync(
            string prompt,
            string? providerProfileId,
            string? modelId,
            CancellationToken ct = default) => WaitAsync(ct);

        private static async Task<string> WaitAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return string.Empty;
        }
    }
}
