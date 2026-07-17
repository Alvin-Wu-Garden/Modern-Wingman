using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.CodeGraph;

namespace AgentService.UnitTests;

public sealed class GraphSnapshotV2Tests
{
    private static readonly GraphAnalysisProfile Profile = new(
        "test-indexer",
        GraphSchemaV2.Version,
        [new("roslyn", "1"), new("sql", "1")]);

    [Fact]
    public void CanonicalSnapshot_IsStableAcrossInputOrderAndRunTimestamps()
    {
        var first = CreateGraph(reverse: false, indexedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var second = CreateGraph(reverse: true, indexedAt: DateTimeOffset.Parse("2026-07-17T00:00:00Z"));

        var expected = CreateSnapshot(first, "manifest-a", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var actual = CreateSnapshot(second, "manifest-b", DateTimeOffset.Parse("2026-07-17T00:00:00Z"));

        Assert.Equal(expected.Snapshot.AnalysisSnapshotHash, actual.Snapshot.AnalysisSnapshotHash);
        Assert.True(GraphSnapshotComparer.Compare(expected, actual).IsEquivalent);
    }

    [Fact]
    public void CanonicalSnapshot_MergesLocationsAndEvidenceWithoutFirstWinsLoss()
    {
        var graph = CreateGraph(reverse: false, DateTimeOffset.UtcNow);
        graph.Nodes.Add(Node(
            "type:Orders.Service", "Service", "src/Orders/Service.Partial.cs",
            GraphSourceKind.FrameworkAdapter, GraphConfidence.Heuristic, "framework"));
        graph.Edges.Add(Edge(
            "method:Orders.Service.Place", "method:Payments.Gateway.Charge",
            GraphSourceKind.FrameworkAdapter, GraphConfidence.Heuristic, "framework"));

        var snapshot = CreateSnapshot(graph, "manifest", DateTimeOffset.UtcNow,
            new IndexedFileManifest(
                "src/Orders/Service.Partial.cs", "csharp", 12, "partial-hash"));

        var type = Assert.Single(snapshot.Nodes, node => node.Id == "type:Orders.Service");
        Assert.Equal(2, type.Locations.Count);
        Assert.Equal(2, type.Evidence.Count);
        var call = Assert.Single(snapshot.Edges);
        Assert.Equal(2, call.Evidence.Count);

        var published = GraphSnapshotCanonicalizer.ToAnalysisResult(snapshot);
        var publishedType = Assert.Single(published.Nodes, node => node.Key == type.Id);
        var publishedCall = Assert.Single(published.Edges);
        Assert.Contains("Service.Partial.cs", publishedType.LocationsJson);
        Assert.Contains("framework", publishedType.EvidenceJson);
        Assert.Contains("framework", publishedCall.EvidenceJson);
    }

    [Fact]
    public void ProducerHash_DoesNotChangeWhenUnrelatedArtifactChanges()
    {
        var graph = CreateGraph(reverse: false, DateTimeOffset.UtcNow);
        var first = CreateSnapshot(graph, "a", DateTimeOffset.UtcNow,
            new IndexedFileManifest("src/Unrelated.cs", "csharp", 1, "unrelated-a"));
        var second = CreateSnapshot(graph, "b", DateTimeOffset.UtcNow,
            new IndexedFileManifest("src/Unrelated.cs", "csharp", 1, "unrelated-b"));

        Assert.Equal(
            Assert.Single(first.Edges).Evidence[0].ContentHash,
            Assert.Single(second.Edges).Evidence[0].ContentHash);
        Assert.NotEqual(first.Snapshot.AnalysisSnapshotHash, second.Snapshot.AnalysisSnapshotHash);
    }

    [Fact]
    public void EdgeProducerHash_DoesNotChangeWhenOnlyTargetArtifactChanges()
    {
        var graph = CreateGraph(reverse: false, DateTimeOffset.UtcNow);
        var first = CreateSnapshot(graph, "a", DateTimeOffset.UtcNow);
        var second = GraphSnapshotCanonicalizer.Create(
            "project",
            "b",
            DateTimeOffset.UtcNow,
            Profile,
            "working-tree",
            "full",
            [
                new IndexedFileManifest("src/Orders/Service.cs", "csharp", 100, "orders-hash"),
                new IndexedFileManifest("src/Payments/Gateway.cs", "csharp", 101, "payments-changed"),
            ],
            graph);

        Assert.Equal(
            Assert.Single(first.Edges).Evidence[0].ContentHash,
            Assert.Single(second.Edges).Evidence[0].ContentHash);
    }

    [Fact]
    public void Comparator_ReportsDirectionAndProvenanceChanges()
    {
        var expected = CreateSnapshot(CreateGraph(false, DateTimeOffset.UtcNow), "a", DateTimeOffset.UtcNow);
        var changedGraph = CreateGraph(false, DateTimeOffset.UtcNow);
        changedGraph.Edges.Clear();
        changedGraph.Edges.Add(Edge(
            "method:Payments.Gateway.Charge", "method:Orders.Service.Place",
            GraphSourceKind.Heuristic, GraphConfidence.Heuristic, "guess"));
        var actual = CreateSnapshot(changedGraph, "b", DateTimeOffset.UtcNow);

        var comparison = GraphSnapshotComparer.Compare(expected, actual);

        Assert.False(comparison.IsEquivalent);
        Assert.Contains(comparison.Differences, item =>
            item.EntityType == "edge" && item.Kind == GraphDifferenceKind.Missing);
        Assert.Contains(comparison.Differences, item =>
            item.EntityType == "edge" && item.Kind == GraphDifferenceKind.Unexpected);
    }

    [Fact]
    public void CanonicalSnapshot_RejectsDanglingEdgeAndConflictingNode()
    {
        var dangling = CreateGraph(false, DateTimeOffset.UtcNow);
        dangling.Edges.Add(Edge("missing", "type:Orders.Service"));
        Assert.Throws<InvalidOperationException>(() =>
            CreateSnapshot(dangling, "dangling", DateTimeOffset.UtcNow));

        var conflicting = CreateGraph(false, DateTimeOffset.UtcNow);
        conflicting.Nodes.Add(new CodeNode
        {
            Key = "type:Orders.Service",
            Kind = CodeNodeKind.Method,
            Name = "Different",
            Language = "csharp",
        });
        Assert.Throws<InvalidOperationException>(() =>
            CreateSnapshot(conflicting, "conflict", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CanonicalSnapshot_MergesRelationalObservationsButRetainsEveryEvidenceSource()
    {
        var graph = new CodeAnalysisResult();
        graph.Nodes.Add(new CodeNode
        {
            Key = "table:default.country",
            Kind = CodeNodeKind.Table,
            Name = "Country",
            Signature = "default.Country",
            FilePath = "schema/001.sql",
            Language = "sql",
            SourceKind = GraphSourceKind.Migration,
            Confidence = GraphConfidence.Exact,
            ExtractorId = "wingman.sql-schema",
            ExtractorVersion = "1",
        });
        graph.Nodes.Add(new CodeNode
        {
            Key = "table:default.country",
            Kind = CodeNodeKind.Table,
            Name = "country",
            Signature = "default.country",
            FilePath = "src/CountryMap.cs",
            Language = "csharp",
            SourceKind = GraphSourceKind.Heuristic,
            Confidence = GraphConfidence.Heuristic,
            ExtractorId = "wingman.orm-data",
            ExtractorVersion = "1",
        });

        var snapshot = GraphSnapshotCanonicalizer.Create(
            "project",
            "manifest",
            DateTimeOffset.UtcNow,
            Profile,
            "working-tree",
            "full",
            [
                new IndexedFileManifest("schema/001.sql", "sql", 10, "sql-hash"),
                new IndexedFileManifest("src/CountryMap.cs", "csharp", 10, "orm-hash"),
            ],
            graph);

        var table = Assert.Single(snapshot.Nodes);
        Assert.Equal("table:default.country", table.Id);
        Assert.Equal(2, table.Evidence.Count);
        Assert.Equal(2, table.Locations.Count);
    }

    private static GraphSnapshotV2 CreateSnapshot(
        CodeAnalysisResult graph,
        string manifest,
        DateTimeOffset createdAt,
        params IndexedFileManifest[] extras)
    {
        var artifacts = new List<IndexedFileManifest>
        {
            new("src/Orders/Service.cs", "csharp", 100, "orders-hash"),
            new("src/Payments/Gateway.cs", "csharp", 100, "payments-hash"),
        };
        artifacts.AddRange(extras);
        return GraphSnapshotCanonicalizer.Create(
            "project",
            manifest,
            createdAt,
            Profile,
            "working-tree",
            "full",
            artifacts,
            graph);
    }

    private static CodeAnalysisResult CreateGraph(bool reverse, DateTimeOffset indexedAt)
    {
        var graph = new CodeAnalysisResult();
        var nodes = new[]
        {
            Node("type:Orders.Service", "Service", "src/Orders/Service.cs", indexedAt: indexedAt),
            Node("method:Orders.Service.Place", "Place", "src/Orders/Service.cs", kind: CodeNodeKind.Method, indexedAt: indexedAt),
            Node("type:Payments.Gateway", "Gateway", "src/Payments/Gateway.cs", indexedAt: indexedAt),
            Node("method:Payments.Gateway.Charge", "Charge", "src/Payments/Gateway.cs", kind: CodeNodeKind.Method, indexedAt: indexedAt),
        };
        foreach (var node in reverse ? nodes.Reverse() : nodes)
            graph.Nodes.Add(node);
        graph.Edges.Add(Edge("method:Orders.Service.Place", "method:Payments.Gateway.Charge"));
        graph.Edges[0].IndexedAt = indexedAt;
        return graph;
    }

    private static CodeNode Node(
        string key,
        string name,
        string file,
        GraphSourceKind source = GraphSourceKind.Compiler,
        GraphConfidence confidence = GraphConfidence.Exact,
        string extractor = "roslyn",
        CodeNodeKind kind = CodeNodeKind.Type,
        DateTimeOffset? indexedAt = null) => new()
        {
            Key = key,
            Kind = kind,
            Name = name,
            Signature = key,
            FilePath = file,
            StartLine = 1,
            EndLine = 2,
            Language = "csharp",
            SourceKind = source,
            Confidence = confidence,
            ExtractorId = extractor,
            ExtractorVersion = "1",
            IndexedAt = indexedAt,
            ContentHash = "legacy-run-hash",
            Reason = "test",
        };

    private static CodeEdge Edge(
        string source,
        string target,
        GraphSourceKind sourceKind = GraphSourceKind.Compiler,
        GraphConfidence confidence = GraphConfidence.Exact,
        string extractor = "roslyn") => new()
        {
            SourceKey = source,
            TargetKey = target,
            Kind = CodeEdgeKind.Calls,
            SourceKind = sourceKind,
            Confidence = confidence,
            ExtractorId = extractor,
            ExtractorVersion = "1",
            ContentHash = "legacy-run-hash",
            Reason = "test",
        };
}
