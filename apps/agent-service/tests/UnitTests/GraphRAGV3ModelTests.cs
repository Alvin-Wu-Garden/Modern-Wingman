using AgentService.Modules.GraphRAG;

namespace AgentService.UnitTests;

public sealed class GraphRAGV3ModelTests
{
    [Fact]
    public void Assemble_MergesCaseOnlyAttributeVariantsDeterministically()
    {
        var first = Node(
            "entry:web:bond/portfoliocombobox",
            GraphNodeKind.EntryPoint,
            GraphRoles.ControllerAction,
            "Bond/PortfolioComboBox",
            Evidence("src/BondController.cs", "由 Controller action 宣告取得。")) with
        {
            Attributes = new Dictionary<string, string>
            {
                ["controller"] = "Bond",
                ["action"] = "PortfolioComboBox",
            },
        };
        var second = first with
        {
            Name = "Bond/PortfolioCombobox",
            Attributes = new Dictionary<string, string>
            {
                ["controller"] = "bond",
                ["action"] = "PortfolioCombobox",
            },
            Evidence =
            [
                Evidence("Scripts/bond.js", "由前端固定 URL 取得。"),
            ],
        };
        var fragment = new GraphFragment();
        fragment.Nodes.AddRange([second, first]);

        var snapshot = Assemble(fragment);

        var merged = Assert.Single(snapshot.Nodes);
        Assert.Equal("PortfolioComboBox", merged.Attributes["action"]);
        Assert.Equal("Bond", merged.Attributes["controller"]);
        Assert.Equal(2, merged.Evidence.Count);
    }

    [Fact]
    public void Assemble_AddsBoundedRoleTermsToSearchableText()
    {
        var fragment = new GraphFragment();
        fragment.Nodes.Add(Node(
            "data:business:csv-format/fmt01",
            GraphNodeKind.Data,
            GraphRoles.CsvFormat,
            "FMT01",
            Evidence("db:FBL/tblCSVFormat/FMT01", "由 CSV 格式主檔取得。")));

        var snapshot = Assemble(fragment);

        var node = Assert.Single(snapshot.Nodes);
        Assert.Contains("CSV 格式", node.SearchableText);
        Assert.Contains("FMT01", node.SearchableText);
    }

    [Fact]
    public void DomainEnums_AreExactlyFourNodesAndNineEdges()
    {
        Assert.Equal(
            [GraphNodeKind.Feature, GraphNodeKind.EntryPoint, GraphNodeKind.Code, GraphNodeKind.Data],
            Enum.GetValues<GraphNodeKind>());
        Assert.Equal(9, Enum.GetValues<GraphEdgeKind>().Length);
    }

    [Fact]
    public void VisualRelationshipCore_PrioritizesConnectedNodesWithinBudget()
    {
        // 關係共享端點時應繼續擴充同一個連通核心，不能退回只依種類挑選孤立節點。
        var result = Neo4jGraphStore.SelectRelationshipCoreNodeIds(
            [
                ("entry:order", "code:controller"),
                ("code:controller", "code:service"),
                ("data:table-a", "data:table-b"),
            ],
            nodeLimit: 3);

        Assert.Equal(
            ["entry:order", "code:controller", "code:service"],
            result);
    }

    [Fact]
    public void VisualRelationshipCore_NeverAddsOnlyOneEndpoint()
    {
        // node budget 不足以容納一條關係的兩端時，應留給後續單節點補位，
        // 不能加入無法畫出 relationship 的半套核心。
        var result = Neo4jGraphStore.SelectRelationshipCoreNodeIds(
            [("entry:order", "code:controller")],
            nodeLimit: 1);

        Assert.Empty(result);
    }

    [Fact]
    public void VisualRelationshipCore_AllowsSelfLoopWithinSingleNodeBudget()
    {
        var result = Neo4jGraphStore.SelectRelationshipCoreNodeIds(
            [("code:recursive", "code:recursive")],
            nodeLimit: 1);

        Assert.Equal(["code:recursive"], result);
    }

    [Fact]
    public void VisualQueryEndpoints_FillsWholeEdgeWithinRemainingBudget()
    {
        var edges = new[]
        {
            VisualEdge("edge:a-b", "node:a", "node:b"),
            VisualEdge("edge:c-d", "node:c", "node:d"),
        };

        var missing = Neo4jGraphStore.SelectMissingVisualEndpointIds(
            edges,
            new HashSet<string>(["node:a"], StringComparer.Ordinal),
            nodeBudget: 2);

        // 第一條 edge 只缺 B，第二條同時缺 C、D 但剩餘額度不足，因此不可只補其中一端。
        Assert.Equal(["node:b"], missing);
    }

    [Fact]
    public void VisualQueryNodes_ClampsAggregateAndKeepsConnectedPair()
    {
        var result = Neo4jGraphStore.SelectBoundedVisualNodeIds(
            ["node:isolated-a", "node:isolated-b", "node:source", "node:target"],
            [VisualEdge("edge:source-target", "node:source", "node:target")],
            nodeLimit: 2);

        Assert.Equal(["node:source", "node:target"], result);
    }

    [Fact]
    public void VisualQueryEdges_RemovesOrphansAfterEndpointHydration()
    {
        var complete = VisualEdge("edge:a-b", "node:a", "node:b");
        var orphan = VisualEdge("edge:b-c", "node:b", "node:c");

        var result = Neo4jGraphStore.KeepVisualEdgesWithEndpoints(
            [orphan, complete],
            new HashSet<string>(["node:a", "node:b"], StringComparer.Ordinal));

        Assert.Equal([complete], result);
    }

    [Theory]
    [InlineData("all", "all")]
    [InlineData("in", "in")]
    [InlineData("callers", "in")]
    [InlineData("out", "out")]
    [InlineData("callees", "out")]
    [InlineData("same-file", "same-file")]
    [InlineData(" CALLERS ", "in")]
    public void VisualNeighborMode_NormalizesUiAndLegacyValues(
        string input,
        string expected)
    {
        Assert.Equal(expected, Neo4jGraphStore.NormalizeVisualNeighborMode(input));
    }

    [Fact]
    public void VisualNeighborMode_RejectsUnknownValue()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Neo4jGraphStore.NormalizeVisualNeighborMode("dependencies"));

        Assert.Contains("same-file", exception.Message);
    }

    [Fact]
    public void EdgeIdentity_IsDeterministicAndDirectional()
    {
        var first = GraphIdentity.Edge("feature:menu:1", GraphEdgeKind.RoutesTo, "entry:web:order/index");
        var same = GraphIdentity.Edge("feature:menu:1", GraphEdgeKind.RoutesTo, "entry:web:order/index");
        var reverse = GraphIdentity.Edge("entry:web:order/index", GraphEdgeKind.RoutesTo, "feature:menu:1");

        Assert.Equal(first, same);
        Assert.NotEqual(first, reverse);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void StableIdentity_NormalizesFormattingButRejectsAbsolutePaths()
    {
        Assert.Equal("entry:web:order/save", GraphIdentity.WebEntry("OrderController", "Save"));
        Assert.Equal("code:csharp:riskmaster.services.orderservice",
            GraphIdentity.CSharpCode("RiskMaster.Services.OrderService"));
        Assert.Equal("entry:frontend:src/order/index.js",
            GraphIdentity.FrontendEntry(@"Src\Order\Index.JS"));
        Assert.Throws<ArgumentException>(() => GraphIdentity.FrontendEntry(@"C:\secret\page.js"));
        Assert.Throws<ArgumentException>(() => GraphIdentity.FrontendEntry("../secret/page.js"));
    }

    [Fact]
    public void Assemble_MergesEvidenceAndProducesDeterministicDigest()
    {
        var firstFragment = new GraphFragment();
        firstFragment.Nodes.Add(Node(
            "feature:menu:1", GraphNodeKind.Feature, GraphRoles.MenuFeature, "交易維護",
            Evidence("db:FBL/tblMenuMap/1", "由選單主檔直接取得")));
        firstFragment.Nodes.Add(Node(
            "entry:web:order/index", GraphNodeKind.EntryPoint, GraphRoles.ControllerAction, "Order/Index",
            Evidence("src/controllers/ordercontroller.cs", "由 MVC Action 語法直接取得", 12)));
        firstFragment.Edges.Add(Edge(
            "feature:menu:1", GraphEdgeKind.RoutesTo, "entry:web:order/index",
            Evidence("db:FBL/tblMenuMap/1", "由 LinkAddress 唯一解析至 Controller Action")));

        var secondFragment = new GraphFragment();
        secondFragment.Nodes.Add(Node(
            "feature:menu:1", GraphNodeKind.Feature, GraphRoles.MenuFeature, "交易維護",
            Evidence("db:FBL/tblMenuMap/1", "由選單階層再次確認")));

        var descriptor = new GraphIndexerDescriptor("3.0.0",
            new Dictionary<string, string> { ["menu"] = "1.0.0", ["csharp"] = "1.0.0" });
        var artifacts = new[]
        {
            new GraphArtifact("file:src/controllers/ordercontroller.cs",
                "src/controllers/ordercontroller.cs", "csharp", 100, "abc", "indexed"),
        };
        var first = GraphAssembler.Assemble(
            "project-1", "manifest-a", DateTimeOffset.UtcNow, descriptor, "tree", "full",
            artifacts, [firstFragment, secondFragment]);
        var second = GraphAssembler.Assemble(
            "project-1", "manifest-b", DateTimeOffset.UtcNow.AddMinutes(1), descriptor, "tree", "full",
            artifacts, [secondFragment, firstFragment]);

        Assert.Equal(first.CanonicalDigest, second.CanonicalDigest);
        Assert.Equal(2, first.Nodes.Count);
        Assert.Equal(2, first.Nodes.Single(node => node.Id == "feature:menu:1").Evidence.Count);
        Assert.Equal(first.CanonicalDigest, GraphAssembler.RecomputeDigest(first));
    }

    [Fact]
    public void Assemble_RejectsKindConflict()
    {
        var fragment = new GraphFragment();
        fragment.Nodes.Add(Node(
            "shared:id", GraphNodeKind.Code, GraphRoles.Type, "A",
            Evidence("src/a.cs", "由 C# 型別宣告取得")));
        fragment.Nodes.Add(Node(
            "shared:id", GraphNodeKind.Data, GraphRoles.Table, "A",
            Evidence("db:FBL/dbo/A", "由 SQL metadata 取得")));

        var error = Assert.Throws<InvalidOperationException>(() => Assemble(fragment));
        Assert.Contains("不同 kind", error.Message);
    }

    [Fact]
    public void Assemble_RejectsDanglingEdgeAndMissingEvidence()
    {
        var dangling = new GraphFragment();
        dangling.Nodes.Add(Node(
            "feature:menu:1", GraphNodeKind.Feature, GraphRoles.MenuFeature, "交易",
            Evidence("db:FBL/tblMenuMap/1", "由選單主檔取得")));
        dangling.Edges.Add(Edge(
            "feature:menu:1", GraphEdgeKind.RoutesTo, "entry:web:missing/index",
            Evidence("db:FBL/tblMenuMap/1", "由 LinkAddress 解析")));
        Assert.Contains("dangling", Assert.Throws<InvalidOperationException>(() => Assemble(dangling)).Message);

        var missingEvidence = new GraphFragment();
        missingEvidence.Nodes.Add(Node(
            "feature:menu:1", GraphNodeKind.Feature, GraphRoles.MenuFeature, "交易",
            evidence: null));
        Assert.Contains("缺少 evidence",
            Assert.Throws<InvalidOperationException>(() => Assemble(missingEvidence)).Message);
    }

    [Fact]
    public void Assemble_RejectsSecretsAndEmail()
    {
        var password = new GraphFragment();
        password.Nodes.Add(Node(
            "data:sql:fbl/dbo/table/customer", GraphNodeKind.Data, GraphRoles.Table, "Customer",
            Evidence("db:FBL/dbo/Customer", "由 SQL metadata 取得"),
            searchableText: "Server=127.0.0.1;User ID=test;Password=secret"));
        Assert.Contains("密碼", Assert.Throws<InvalidOperationException>(() => Assemble(password)).Message);

        var email = new GraphFragment();
        email.Nodes.Add(Node(
            "feature:batch-report:1", GraphNodeKind.Feature, GraphRoles.BatchReport, "寄送報表",
            Evidence("db:FBL/tblBatchReport/1", "由批次報表主檔取得"),
            searchableText: "owner@example.com"));
        Assert.Contains("Email", Assert.Throws<InvalidOperationException>(() => Assemble(email)).Message);
    }

    private static GraphSnapshot Assemble(GraphFragment fragment) =>
        GraphAssembler.Assemble(
            "project", "manifest", DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor("test", new Dictionary<string, string>()),
            "tree", "full", [], [fragment]);

    private static GraphNode Node(
        string id,
        GraphNodeKind kind,
        string role,
        string name,
        GraphEvidence? evidence,
        string? searchableText = null) =>
        new(
            id,
            kind,
            role,
            name,
            searchableText ?? name,
            kind is GraphNodeKind.Feature or GraphNodeKind.Data ? "business" : "csharp",
            null,
            "active",
            [],
            evidence?.Artifact.StartsWith("db:", StringComparison.Ordinal) == true ? null : evidence?.Artifact,
            evidence?.StartLine,
            evidence?.EndLine,
            new Dictionary<string, string>(),
            evidence is null ? [] : [evidence]);

    private static GraphEdge Edge(
        string source,
        GraphEdgeKind kind,
        string target,
        GraphEvidence evidence) =>
        new(GraphIdentity.Edge(source, kind, target), source, kind, target, [evidence]);

    private static GraphVisualEdgeV3 VisualEdge(
        string id,
        string source,
        string target) =>
        new(
            id,
            source,
            target,
            "CALLS",
            new Dictionary<string, object?>());

    private static GraphEvidence Evidence(string artifact, string reason, int? line = null) =>
        new(
            artifact.StartsWith("db:", StringComparison.Ordinal)
                ? GraphEvidenceSource.Sql
                : GraphEvidenceSource.Ast,
            GraphConfidence.Exact,
            artifact,
            reason,
            line,
            line);
}
