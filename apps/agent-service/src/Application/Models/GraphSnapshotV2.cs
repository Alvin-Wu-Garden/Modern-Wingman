using AgentService.Domain.Models;

namespace AgentService.Application.Models;

public static class GraphSchemaV2
{
    public const string Version = "2.0";
    public const string IndexerId = "modern-wingman-indexer";
}

public sealed record GraphAnalyzerIdentity(string Id, string Version);

public sealed record GraphAnalysisProfile(
    string IndexerVersion,
    string GraphSchemaVersion,
    IReadOnlyList<GraphAnalyzerIdentity> Analyzers);

public sealed record GraphArtifactV2(
    string Id,
    string Path,
    string Kind,
    string? ModuleId,
    long Length,
    string ContentHash,
    string Status,
    string? Reason = null);

public sealed record GraphLocationV2(
    string ArtifactId,
    int? StartLine,
    int? EndLine,
    string Role);

public sealed record GraphEvidenceV2(
    GraphSourceKind SourceKind,
    GraphConfidence Confidence,
    GraphAnalyzerIdentity Extractor,
    IReadOnlyList<string> ArtifactIds,
    string? ContentHash,
    string? Reason);

public sealed record GraphNodeV2(
    string Id,
    CodeNodeKind Kind,
    string Name,
    string? Signature,
    string Language,
    string? Technology,
    string? DocComment,
    IReadOnlyList<GraphLocationV2> Locations,
    IReadOnlyList<GraphEvidenceV2> Evidence);

public sealed record GraphEdgeV2(
    string Id,
    string SourceId,
    CodeEdgeKind Kind,
    string TargetId,
    bool Directed,
    IReadOnlyList<GraphEvidenceV2> Evidence);

public sealed record GraphSnapshotMetadataV2(
    string AnalysisSnapshotHash,
    string WorkingTreeFingerprint,
    string Mode,
    string Status,
    int NodeCount,
    int EdgeCount,
    string? HeadCommit = null);

public sealed record GraphSnapshotV2(
    string SchemaVersion,
    string ProjectId,
    string ManifestVersion,
    DateTimeOffset CreatedAt,
    GraphAnalysisProfile AnalysisProfile,
    GraphSnapshotMetadataV2 Snapshot,
    IReadOnlyList<GraphArtifactV2> Artifacts,
    IReadOnlyList<GraphNodeV2> Nodes,
    IReadOnlyList<GraphEdgeV2> Edges,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> CapabilityGaps);

/// <summary>
/// Canonical graph entities produced for the invalidated artifact closure. Edges may target
/// nodes that are intentionally omitted from the fragment because they already exist in the
/// active base snapshot.
/// </summary>
public sealed record GraphSnapshotFragmentV2(
    IReadOnlyList<GraphNodeV2> Nodes,
    IReadOnlyList<GraphEdgeV2> Edges,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> CapabilityGaps);

public enum GraphDifferenceKind
{
    Missing,
    Unexpected,
    Changed,
}

public sealed record GraphDifference(
    string EntityType,
    string Identity,
    GraphDifferenceKind Kind,
    string? Expected,
    string? Actual);

public sealed record GraphComparisonResult(IReadOnlyList<GraphDifference> Differences)
{
    public bool IsEquivalent => Differences.Count == 0;
}
