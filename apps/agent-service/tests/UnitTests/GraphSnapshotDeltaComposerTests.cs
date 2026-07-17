using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.CodeGraph;

namespace AgentService.UnitTests;

public sealed class GraphSnapshotDeltaComposerTests
{
    private static readonly GraphAnalysisProfile Profile = new(
        "test-indexer",
        GraphSchemaV2.Version,
        [new("roslyn", "1")]);

    [Fact]
    public void ChangedTarget_PreservesUnchangedCallerEdge()
    {
        var baseSnapshot = Snapshot(
            [Node("method:a", "src/A.cs"), Node("method:b", "src/B.cs")],
            [Edge("method:a", "method:b", "src/A.cs")],
            Artifacts(("src/A.cs", "a1"), ("src/B.cs", "b1")));
        var refreshed = Snapshot(
            [Node("method:b", "src/B.cs")],
            [],
            Artifacts(("src/A.cs", "a1"), ("src/B.cs", "b2")));
        var originalEvidenceHash = Assert.Single(baseSnapshot.Edges).Evidence[0].ContentHash;

        var actual = Compose(
            baseSnapshot,
            ["file:src/B.cs"],
            new(refreshed.Nodes, [], [], []),
            refreshed.Artifacts);

        var call = Assert.Single(actual.Edges);
        Assert.Equal("method:a", call.SourceId);
        Assert.Equal("method:b", call.TargetId);
        Assert.Equal(originalEvidenceHash, Assert.Single(call.Evidence).ContentHash);
    }

    [Fact]
    public void ChangedSource_ReplacesItsOutgoingEdgeWithoutTouchingOtherNodes()
    {
        var artifacts = Artifacts(("src/A.cs", "a1"), ("src/B.cs", "b1"), ("src/C.cs", "c1"));
        var baseSnapshot = Snapshot(
            [Node("method:a", "src/A.cs"), Node("method:b", "src/B.cs"), Node("method:c", "src/C.cs")],
            [Edge("method:a", "method:b", "src/A.cs")],
            artifacts);
        var updatedArtifacts = Artifacts(("src/A.cs", "a2"), ("src/B.cs", "b1"), ("src/C.cs", "c1"));
        var refreshed = Snapshot(
            [Node("method:a", "src/A.cs"), Node("method:c", "src/C.cs")],
            [Edge("method:a", "method:c", "src/A.cs")],
            updatedArtifacts);

        var actual = Compose(
            baseSnapshot,
            ["file:src/A.cs"],
            new(
                refreshed.Nodes.Where(node => node.Id == "method:a").ToList(),
                refreshed.Edges,
                [],
                []),
            refreshed.Artifacts);

        var call = Assert.Single(actual.Edges);
        Assert.Equal("method:c", call.TargetId);
        Assert.Contains(actual.Nodes, node => node.Id == "method:b");
        Assert.Contains(actual.Nodes, node => node.Id == "method:c");
    }

    [Fact]
    public void ChangedArtifact_RemovesOnlyItsEvidenceAndRetainsOtherProducerEvidence()
    {
        var graph = new CodeAnalysisResult();
        graph.Nodes.Add(Node("type:partial", "src/Part1.cs"));
        graph.Nodes.Add(Node("type:partial", "src/Part2.cs", extractor: "generator"));
        var baseSnapshot = Snapshot(
            graph.Nodes,
            [],
            Artifacts(("src/Part1.cs", "p1"), ("src/Part2.cs", "p2")));
        var refreshed = Snapshot(
            [Node("type:partial", "src/Part1.cs", extractor: "roslyn-v2")],
            [],
            Artifacts(("src/Part1.cs", "p1-new"), ("src/Part2.cs", "p2")));

        var actual = Compose(
            baseSnapshot,
            ["file:src/Part1.cs"],
            new(refreshed.Nodes, [], [], []),
            refreshed.Artifacts);

        var node = Assert.Single(actual.Nodes);
        Assert.Equal(2, node.Locations.Count);
        Assert.Equal(2, node.Evidence.Count);
        Assert.Contains(node.Evidence, evidence => evidence.Extractor.Id == "generator");
        Assert.Contains(node.Evidence, evidence => evidence.Extractor.Id == "roslyn-v2");
        Assert.DoesNotContain(node.Evidence, evidence =>
            evidence.Extractor.Id == "roslyn" && evidence.ArtifactIds.Contains("file:src/Part1.cs"));
    }

    [Fact]
    public void Composition_IsDeterministicAndUsesFullSnapshotHashSemantics()
    {
        var baseSnapshot = Snapshot(
            [Node("method:a", "src/A.cs"), Node("method:b", "src/B.cs")],
            [Edge("method:a", "method:b", "src/A.cs")],
            Artifacts(("src/A.cs", "a1"), ("src/B.cs", "b1")),
            diagnostics: ["src/B.cs: old diagnostic", "stable diagnostic"]);
        var updatedArtifacts = Artifacts(("src/A.cs", "a1"), ("src/B.cs", "b2"));
        var refreshed = Snapshot(
            [Node("method:b", "src/B.cs")],
            [],
            updatedArtifacts,
            diagnostics: ["src/B.cs: new diagnostic"]);
        var fragment = new GraphSnapshotFragmentV2(
            refreshed.Nodes,
            [],
            ["src/B.cs: new diagnostic"],
            []);

        var first = GraphSnapshotDeltaComposer.Compose(
            baseSnapshot,
            ["file:src/B.cs"],
            fragment,
            "delta-1",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            "tree-2",
            "incremental",
            updatedArtifacts);
        var second = GraphSnapshotDeltaComposer.Compose(
            baseSnapshot,
            ["file:src\\B.cs"],
            fragment with { Nodes = fragment.Nodes.Reverse().ToList() },
            "delta-2",
            DateTimeOffset.Parse("2026-07-17T00:00:00Z"),
            "tree-2",
            "incremental",
            updatedArtifacts.Reverse().ToList());

        Assert.Equal(first.Snapshot.AnalysisSnapshotHash, second.Snapshot.AnalysisSnapshotHash);
        Assert.True(GraphSnapshotComparer.Compare(first, second).IsEquivalent);
        Assert.DoesNotContain(first.Diagnostics, value => value.Contains("old diagnostic", StringComparison.Ordinal));
        Assert.Contains("src/B.cs: new diagnostic", first.Diagnostics);

        var full = Snapshot(
            [Node("method:a", "src/A.cs"), Node("method:b", "src/B.cs")],
            [Edge("method:a", "method:b", "src/A.cs")],
            updatedArtifacts,
            workingTree: "tree-2",
            diagnostics: ["src/B.cs: new diagnostic", "stable diagnostic"]);
        Assert.Equal(full.Snapshot.AnalysisSnapshotHash, first.Snapshot.AnalysisSnapshotHash);
    }

    [Fact]
    public void Composition_RejectsCoreConflictAndDanglingFragmentEdge()
    {
        var baseSnapshot = Snapshot(
            [Node("method:a", "src/A.cs"), Node("method:b", "src/B.cs")],
            [],
            Artifacts(("src/A.cs", "a1"), ("src/B.cs", "b1")));
        var conflicting = baseSnapshot.Nodes.Single(node => node.Id == "method:a") with
        {
            Name = "different",
        };

        Assert.Throws<InvalidOperationException>(() => Compose(
            baseSnapshot,
            [],
            new([conflicting], [], [], []),
            baseSnapshot.Artifacts));

        var dangling = baseSnapshot.Edges.FirstOrDefault() ?? new GraphEdgeV2(
            "ignored",
            "method:a",
            CodeEdgeKind.Calls,
            "method:missing",
            true,
            []);
        Assert.Throws<InvalidOperationException>(() => Compose(
            baseSnapshot,
            [],
            new([], [dangling], [], []),
            baseSnapshot.Artifacts));
    }

    private static GraphSnapshotV2 Compose(
        GraphSnapshotV2 baseSnapshot,
        IReadOnlyCollection<string> changed,
        GraphSnapshotFragmentV2 fragment,
        IReadOnlyList<GraphArtifactV2> artifacts) => GraphSnapshotDeltaComposer.Compose(
            baseSnapshot,
            changed,
            fragment,
            "delta",
            DateTimeOffset.UtcNow,
            "tree",
            "incremental",
            artifacts);

    private static GraphSnapshotV2 Snapshot(
        IEnumerable<CodeNode> nodes,
        IEnumerable<CodeEdge> edges,
        IReadOnlyList<GraphArtifactV2> artifacts,
        string workingTree = "tree",
        IReadOnlyList<string>? diagnostics = null)
    {
        var graph = new CodeAnalysisResult();
        graph.Nodes.AddRange(nodes);
        graph.Edges.AddRange(edges);
        return GraphSnapshotCanonicalizer.Create(
            "project",
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            Profile,
            workingTree,
            "full",
            artifacts.Select(item => new IndexedFileManifest(
                item.Path, item.Kind, item.Length, item.ContentHash, item.Status, item.Reason)).ToList(),
            graph,
            diagnostics);
    }

    private static IReadOnlyList<GraphArtifactV2> Artifacts(params (string Path, string Hash)[] values) =>
        values.Select(value => new GraphArtifactV2(
            $"file:{value.Path}", value.Path, "csharp", null, 10, value.Hash, "Indexed")).ToList();

    private static CodeNode Node(string key, string file, string extractor = "roslyn") => new()
    {
        Key = key,
        Kind = key.StartsWith("type:", StringComparison.Ordinal) ? CodeNodeKind.Type : CodeNodeKind.Method,
        Name = key[(key.IndexOf(':') + 1)..],
        Signature = key,
        FilePath = file,
        StartLine = 1,
        EndLine = 2,
        Language = "csharp",
        SourceKind = GraphSourceKind.Compiler,
        Confidence = GraphConfidence.Exact,
        ExtractorId = extractor,
        ExtractorVersion = "1",
    };

    private static CodeEdge Edge(string source, string target, string artifactPath) => new()
    {
        SourceKey = source,
        TargetKey = target,
        Kind = CodeEdgeKind.Calls,
        ArtifactPath = artifactPath,
        SourceKind = GraphSourceKind.Compiler,
        Confidence = GraphConfidence.Exact,
        ExtractorId = "roslyn",
        ExtractorVersion = "1",
    };
}
