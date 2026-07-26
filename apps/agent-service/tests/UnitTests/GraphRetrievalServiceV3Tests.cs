using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Modules.GraphRAG;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentService.UnitTests;

public sealed class GraphRetrievalServiceV3Tests
{
    [Fact]
    public async Task LocalSearch_FollowsFeatureEntryCodeDataPathWithinBudget()
    {
        var feature = Node("feature:menu:1", GraphNodeKind.Feature, GraphRoles.MenuFeature, "債券交易作廢");
        var entry = Node("entry:web:bond/cancel", GraphNodeKind.EntryPoint, GraphRoles.ControllerAction, "Bond/Cancel");
        var controller = Node("code:csharp:demo.bondcontroller", GraphNodeKind.Code, GraphRoles.Controller, "BondController");
        var service = Node("code:csharp:demo.bondservice", GraphNodeKind.Code, GraphRoles.BusinessService, "BondService");
        var table = Node("data:sql:fbl/dbo/table/tblbond", GraphNodeKind.Data, GraphRoles.Table, "tblBond");
        var edges = new[]
        {
            Edge(feature, GraphEdgeKind.RoutesTo, entry),
            Edge(entry, GraphEdgeKind.Handles, controller),
            Edge(controller, GraphEdgeKind.Calls, service),
            Edge(service, GraphEdgeKind.Writes, table),
        };
        var store = new InMemoryGraphStore(
            [new GraphSearchHitV3(feature, 10)],
            [feature, entry, controller, service, table],
            edges);
        var serviceUnderTest = new GraphRetrievalService(
            store,
            Options.Create(new GraphRetrievalOptions
            {
                MaximumNodes = 20,
                MaximumEdges = 20,
                MaximumDepth = 4,
                NeighborsPerNode = 10,
            }),
            NullLogger<GraphRetrievalService>.Instance);

        var context = await serviceUnderTest.LocalSearchAsync("project", "債券交易作廢 bug");

        Assert.Equal(5, context.Nodes.Count);
        Assert.Equal(4, context.Edges.Count);
        Assert.Contains(context.Nodes, item => item.Node.Id == table.Id);
        Assert.Equal(0, context.Nodes.Single(item => item.Node.Id == feature.Id).Depth);
        Assert.Equal(4, context.Nodes.Single(item => item.Node.Id == table.Id).Depth);
    }

    [Fact]
    public async Task LocalSearch_RespectsHighDegreeNodeBudget()
    {
        var center = Node("data:sql:fbl/dbo/table/shared", GraphNodeKind.Data, GraphRoles.Table, "Shared");
        var neighbors = Enumerable.Range(0, 100)
            .Select(index => Node($"code:csharp:demo.service{index}", GraphNodeKind.Code, GraphRoles.Type, $"Service{index}"))
            .ToList();
        var store = new InMemoryGraphStore(
            [new GraphSearchHitV3(center, 1)],
            neighbors.Prepend(center).ToList(),
            neighbors.Select(node => Edge(node, GraphEdgeKind.Reads, center)).ToList());
        var service = new GraphRetrievalService(
            store,
            Options.Create(new GraphRetrievalOptions
            {
                MaximumNodes = 10,
                MaximumEdges = 10,
                MaximumDepth = 2,
                NeighborsPerNode = 10,
            }),
            NullLogger<GraphRetrievalService>.Instance);

        var context = await service.LocalSearchAsync("project", "Shared table");

        Assert.True(context.Nodes.Count <= 10);
        Assert.True(context.Edges.Count <= 10);
        Assert.Contains(context.Diagnostics, value => value.Contains("上限", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalSearch_PreservesDirectWriteEdgeBeforeNoisyTraversalFillsBudget()
    {
        var noisy = Node(
            "code:csharp:demo.utility",
            GraphNodeKind.Code,
            GraphRoles.Type,
            "SharedUpdateUtility");
        var writer = Node(
            "code:csharp:demo.asyncconfirm",
            GraphNodeKind.Code,
            GraphRoles.Repository,
            "AsyncConfirm");
        var table = Node(
            "data:sql:fbl/dbo/table/tblasyncconfirm",
            GraphNodeKind.Data,
            GraphRoles.Table,
            "tblAsyncConfirm");
        var noise = Enumerable.Range(0, 20)
            .Select(index => Node(
                $"code:csharp:demo.noise{index}",
                GraphNodeKind.Code,
                GraphRoles.Type,
                $"Noise{index}"))
            .ToList();
        var store = new InMemoryGraphStore(
            [
                new GraphSearchHitV3(noisy, 10),
                new GraphSearchHitV3(writer, 9),
            ],
            [noisy, writer, table, .. noise],
            [
                Edge(writer, GraphEdgeKind.Writes, table),
                .. noise.Select(node => Edge(noisy, GraphEdgeKind.Calls, node)),
            ]);
        var service = new GraphRetrievalService(
            store,
            Options.Create(new GraphRetrievalOptions
            {
                MaximumNodes = 10,
                MaximumEdges = 20,
                MaximumDepth = 4,
                NeighborsPerNode = 50,
            }),
            NullLogger<GraphRetrievalService>.Instance);

        var context = await service.LocalSearchAsync(
            "project",
            "覆核後資料沒有更新");

        Assert.Contains(context.Nodes, item => item.Node.Id == table.Id);
        Assert.Contains(context.Edges, edge =>
            edge.Kind == GraphEdgeKind.Writes &&
            edge.SourceId == writer.Id &&
            edge.TargetId == table.Id);
        Assert.True(context.Nodes.Count <= 10);
    }

    [Fact]
    public void BuildLuceneQuery_EscapesOperatorsAndKeepsChineseTerms()
    {
        var query = GraphRetrievalService.BuildLuceneQuery(
            "債券交易作廢 bug controller:Save && delete?");

        Assert.Contains("\"債券交易作廢\"", query);
        Assert.Contains("\"債券\"", query);
        Assert.Contains("\"交易\"", query);
        Assert.Contains("\"作廢\"", query);
        Assert.DoesNotContain("\"bug\"", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\:", query);
        Assert.DoesNotContain("?", query);
        Assert.DoesNotContain(" && ", query);
    }

    [Fact]
    public void BuildLuceneQuery_ExpandsWriteAndApprovalIntentWithoutGenericDataNoise()
    {
        var query = GraphRetrievalService.BuildLuceneQuery("覆核後資料沒有更新");

        Assert.Contains("\"覆核\"", query);
        Assert.Contains("\"更新\"", query);
        Assert.Contains("\"Confirm\"", query);
        Assert.Contains("\"Approval\"", query);
        Assert.Contains("\"Update\"", query);
        Assert.Contains("\"Save\"", query);
        Assert.Contains("\"Write\"", query);
        Assert.DoesNotContain("\"資料\"", query);
        Assert.DoesNotContain("\"後資\"", query);
        Assert.DoesNotContain("\"料沒\"", query);
        Assert.DoesNotContain("\"有更\"", query);
    }

    [Fact]
    public void Neo4jSearch_DiversifiesSeedsAcrossFourNodeKinds()
    {
        var featureHits = Enumerable.Range(0, 20)
            .Select(index => new GraphSearchHitV3(
                Node($"feature:menu:{index}", GraphNodeKind.Feature,
                    GraphRoles.MenuFeature, $"商品功能 {index}"),
                100 - index));
        var entry = new GraphSearchHitV3(
            Node("entry:web:product/index", GraphNodeKind.EntryPoint,
                GraphRoles.ControllerAction, "Product/Index"),
            20);
        var code = new GraphSearchHitV3(
            Node("code:csharp:productservice", GraphNodeKind.Code,
                GraphRoles.BusinessService, "ProductService"),
            10);
        var data = new GraphSearchHitV3(
            Node("data:business:product-type/1", GraphNodeKind.Data,
                GraphRoles.ProductType, "商品類型"),
            5);

        var selected = Neo4jGraphStore.DiversifySearchHits(
            featureHits.Append(entry).Append(code).Append(data).ToList(),
            8);

        Assert.Equal(8, selected.Count);
        Assert.Equal(4, selected.Select(hit => hit.Node.Kind).Distinct().Count());
    }

    [Fact]
    public void CommunityBuilder_UsesMenuRootAndDoesNotCreateDomainNodes()
    {
        var feature = Node(
            "feature:menu:1", GraphNodeKind.Feature, GraphRoles.MenuFeature, "債券交易",
            new Dictionary<string, string> { ["menuPath"] = "交易管理 > 債券 > 交易維護" });
        var entry = Node("entry:web:bond/index", GraphNodeKind.EntryPoint, GraphRoles.ControllerAction, "Bond/Index");
        var edge = Edge(feature, GraphEdgeKind.RoutesTo, entry);
        var fragment = new GraphFragment();
        fragment.Nodes.AddRange([feature, entry]);
        fragment.Edges.Add(edge);
        var snapshot = GraphAssembler.Assemble(
            "project", "manifest", DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor("test", new Dictionary<string, string>()),
            "tree", "full", [], [fragment]);

        var reports = GraphCommunityBuilder.BuildPrimaryReports(snapshot);

        var report = Assert.Single(reports);
        Assert.Equal("primary:menu:交易管理", report.CommunityId);
        Assert.Equal("交易管理", report.Title);
        Assert.Equal(2, report.MemberIds.Count);
        Assert.Equal(2, snapshot.Nodes.Count);
    }

    [Fact]
    public void CommunityBuilder_KeepsMaintainConfirmTogetherAndAddsSecondaryOverlay()
    {
        var maintain = Node(
            "feature:menu:maintain",
            GraphNodeKind.Feature,
            GraphRoles.MenuFeature,
            "交易維護",
            new Dictionary<string, string> { ["menuPath"] = "交易管理 > 交易維護" });
        var confirm = Node(
            "feature:menu:confirm",
            GraphNodeKind.Feature,
            GraphRoles.ApprovalFeature,
            "交易覆核",
            new Dictionary<string, string> { ["menuPath"] = "覆核管理 > 交易覆核" });
        var entry = Node(
            "entry:web:trade/confirm",
            GraphNodeKind.EntryPoint,
            GraphRoles.ControllerAction,
            "Trade/Confirm");
        var fragment = new GraphFragment();
        fragment.Nodes.AddRange([maintain, confirm, entry]);
        fragment.Edges.AddRange(
        [
            Edge(maintain, GraphEdgeKind.Triggers, confirm),
            Edge(confirm, GraphEdgeKind.RoutesTo, entry),
        ]);
        var snapshot = GraphAssembler.Assemble(
            "project",
            "manifest",
            DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor("test", new Dictionary<string, string>()),
            "tree",
            "full",
            [],
            [fragment]);

        var reports = GraphCommunityBuilder.BuildReports(snapshot);

        var primary = Assert.Single(
            reports,
            report => report.Kind == "primary");
        Assert.Contains(maintain.Id, primary.MemberIds);
        Assert.Contains(confirm.Id, primary.MemberIds);
        Assert.Contains(reports, report => report.Kind == "secondary");
        Assert.Equal(3, snapshot.Nodes.Count);
    }

    [Fact]
    public void CommunityBuilder_ConvertsLeidenMembershipToStableSecondaryReport()
    {
        var feature = Node(
            "feature:menu:trade",
            GraphNodeKind.Feature,
            GraphRoles.MenuFeature,
            "交易維護");
        var entry = Node(
            "entry:web:trade/index",
            GraphNodeKind.EntryPoint,
            GraphRoles.ControllerAction,
            "Trade/Index");
        var code = Node(
            "code:csharp:tradecontroller",
            GraphNodeKind.Code,
            GraphRoles.Controller,
            "TradeController");
        var fragment = new GraphFragment();
        fragment.Nodes.AddRange([feature, entry, code]);
        fragment.Edges.AddRange(
        [
            Edge(feature, GraphEdgeKind.RoutesTo, entry),
            Edge(entry, GraphEdgeKind.Handles, code),
        ]);
        var snapshot = GraphAssembler.Assemble(
            "project",
            "manifest",
            DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor("test", new Dictionary<string, string>()),
            "tree",
            "full",
            [],
            [fragment]);

        var first = GraphCommunityBuilder.BuildSecondaryReportsFromGroups(
            snapshot,
            [[code.Id, feature.Id, entry.Id]],
            "leiden");
        var second = GraphCommunityBuilder.BuildSecondaryReportsFromGroups(
            snapshot,
            [[entry.Id, code.Id, feature.Id]],
            "leiden");

        var report = Assert.Single(first);
        var secondReport = Assert.Single(second);
        Assert.Equal(report.CommunityId, secondReport.CommunityId);
        Assert.Equal(report.CacheKey, secondReport.CacheKey);
        Assert.False(string.IsNullOrWhiteSpace(report.CacheKey));
        Assert.StartsWith("secondary:leiden:", report.CommunityId);
        Assert.Contains("加權 Leiden", report.Summary);
        Assert.Equal(
            new[] { code.Id, entry.Id, feature.Id }.OrderBy(id => id, StringComparer.Ordinal),
            report.MemberIds);
        Assert.Equal(3, snapshot.Nodes.Count);
    }

    [Fact]
    public async Task AnswerGlobalAsync_UsesBoundedMapReduceAcrossCommunityReports()
    {
        var reports = Enumerable.Range(0, 9)
            .Select(index => new GraphCommunityReport(
                $"primary:batch:{index}",
                "primary",
                $"批次報表 {index}",
                $"批次報表社群 {index} 的排程與資料來源摘要。",
                []))
            .ToList();
        var store = new InMemoryGraphStore([], [], [], reports);
        var llm = new RecordingLlm();
        var service = new GraphRetrievalService(
            store,
            Options.Create(new GraphRetrievalOptions()),
            NullLogger<GraphRetrievalService>.Instance,
            llm);

        var answer = await service.AnswerGlobalAsync(
            "project",
            "系統有哪些批次報表鏈？");

        Assert.Equal("reduce-result", answer);
        Assert.Equal(4, llm.Calls.Count);
        Assert.Equal(
            3,
            llm.Calls.Count(prompt => prompt.Contains("map worker", StringComparison.Ordinal)));
        Assert.Contains("reduce worker", llm.Calls[^1]);
        Assert.Contains("Map 結果", llm.Calls[^1]);
    }

    [Theory]
    [InlineData("覆核後資料沒有更新")]
    [InlineData("債券交易作廢發生錯誤")]
    [InlineData("請新增商品 CSV 格式")]
    [InlineData("BondController.Save 要怎麼調整")]
    [InlineData("tblAsyncConfirm 寫入失敗")]
    public void LooksLikeLocalQuestion_RecognizesDefectsChangesAndCodeIdentifiers(
        string question)
    {
        Assert.True(GraphRetrievalService.LooksLikeLocalQuestion(question));
    }

    [Theory]
    [InlineData("系統有哪些批次報表鏈？")]
    [InlineData("整理交易模組與風控模組的整體關係")]
    public void LooksLikeLocalQuestion_KeepsCrossModuleQuestionsGlobal(string question)
    {
        Assert.False(GraphRetrievalService.LooksLikeLocalQuestion(question));
    }

    [Fact]
    public async Task BuildCommunitySummariesAsync_ReusesEvidenceCacheWithoutCallingLlm()
    {
        var cached = new GraphCommunityReport(
            "primary:trade",
            "primary",
            "交易",
            "已快取的 AI 摘要。",
            ["feature:menu:trade"],
            "stable-cache-key",
            true);
        var store = new InMemoryGraphStore([], [], [], [cached]);
        var llm = new RecordingLlm();
        var service = new GraphRetrievalService(
            store,
            Options.Create(new GraphRetrievalOptions()),
            NullLogger<GraphRetrievalService>.Instance,
            llm);

        var count = await service.BuildCommunitySummariesAsync("project");

        Assert.Equal(1, count);
        Assert.Empty(llm.Calls);
        Assert.Equal("已快取的 AI 摘要。", Assert.Single(store.SavedReports).Summary);
        Assert.True(Assert.Single(store.SavedReports).AiEnriched);
    }

    private static GraphNode Node(
        string id,
        GraphNodeKind kind,
        string role,
        string name,
        IReadOnlyDictionary<string, string>? attributes = null) =>
        new(
            id, kind, role, name, name, "business", null, "active", [], null, null, null,
            attributes ?? new Dictionary<string, string>(),
            [new GraphEvidence(
                GraphEvidenceSource.Ast,
                GraphConfidence.Exact,
                "src/test.txt",
                "由測試 fixture 建立可追溯圖譜事實。")]);

    private static GraphEdge Edge(
        GraphNode source,
        GraphEdgeKind kind,
        GraphNode target) =>
        new(
            GraphIdentity.Edge(source.Id, kind, target.Id),
            source.Id,
            kind,
            target.Id,
            [new GraphEvidence(
                GraphEvidenceSource.Ast,
                GraphConfidence.Exact,
                "src/test.txt",
                "由測試 fixture 建立可追溯圖譜關係。")]);

    private sealed class InMemoryGraphStore(
        IReadOnlyList<GraphSearchHitV3> search,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        IReadOnlyList<GraphCommunityReport>? reports = null) : IGraphStore
    {
        private readonly IReadOnlyDictionary<string, GraphNode> _nodes =
            nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        private IReadOnlyList<GraphCommunityReport> _reports =
            reports?.ToList() ?? [];

        internal IReadOnlyList<GraphCommunityReport> SavedReports => _reports;

        public Task<IReadOnlyList<GraphSearchHitV3>> SearchAsync(
            string projectId, string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GraphSearchHitV3>>(search.Take(limit).ToList());

        public Task<IReadOnlyList<GraphNeighborV3>> GetNeighborsAsync(
            string projectId, string nodeId, int limit, CancellationToken cancellationToken = default)
        {
            var result = edges
                .Where(edge => edge.SourceId == nodeId || edge.TargetId == nodeId)
                .Take(limit)
                .Select(edge =>
                {
                    var outgoing = edge.SourceId == nodeId;
                    return new GraphNeighborV3(
                        _nodes[outgoing ? edge.TargetId : edge.SourceId],
                        edge,
                        outgoing ? "outgoing" : "incoming");
                })
                .ToList();
            return Task.FromResult<IReadOnlyList<GraphNeighborV3>>(result);
        }

        public Task<IReadOnlyList<GraphCommunityReport>> ListCommunityReportsAsync(
            string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_reports);

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task PublishAsync(GraphSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<string?> GetActiveManifestAsync(
            string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("manifest");
        public Task DeleteProjectAsync(
            string projectId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<(int Nodes, int Edges)> GetStatsAsync(
            string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult((_nodes.Count, edges.Count));
        public Task<IReadOnlyList<GraphSearchHitV3>> GetCentralNodesAsync(
            string projectId,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GraphSearchHitV3>>(
                _nodes.Values.Take(limit)
                    .Select(node => new GraphSearchHitV3(node, 1))
                    .ToList());
        public Task SaveCommunityReportsAsync(
            string projectId,
            string manifestVersion,
            IReadOnlyList<GraphCommunityReport> reports,
            CancellationToken cancellationToken = default)
        {
            _reports = reports.ToList();
            return Task.CompletedTask;
        }
        public Task<GraphVisualDataV3> GetVisualGraphAsync(
            string projectId,
            int limit,
            IReadOnlyList<string>? kinds,
            IReadOnlyList<string>? relationshipTypes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GraphVisualDataV3([], [], 0, 0, 0, false));
        public Task<GraphVisualSchemaV3> GetVisualSchemaAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GraphVisualSchemaV3(0, 0, [], [], []));
        public Task<GraphVisualDataV3> GetVisualNeighborsAsync(
            string projectId,
            IReadOnlyList<string> nodeIds,
            int depth,
            int limit,
            string mode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GraphVisualDataV3([], [], 0, 0, 0, false));
        public Task<GraphVisualQueryResultV3> QueryVisualGraphAsync(
            string projectId,
            string cypher,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GraphVisualQueryResultV3(
                [], [], new GraphVisualDataV3([], [], 0, 0, 0, false)));
    }

    private sealed class RecordingLlm : ILlmCompletionService
    {
        internal List<string> Calls { get; } = [];

        public Task<string> CompleteAsync(
            string prompt,
            CancellationToken ct = default)
        {
            Calls.Add(prompt);
            return Task.FromResult(
                prompt.Contains("reduce worker", StringComparison.Ordinal)
                    ? "reduce-result"
                    : $"map-result-{Calls.Count}");
        }

        public Task<string> CompleteAsync(
            string prompt,
            string? providerProfileId,
            string? modelId,
            CancellationToken ct = default) =>
            CompleteAsync(prompt, ct);
    }
}
