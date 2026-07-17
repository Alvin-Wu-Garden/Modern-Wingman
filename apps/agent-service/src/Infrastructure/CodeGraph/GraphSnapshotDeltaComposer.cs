using AgentService.Application.Models;

namespace AgentService.Infrastructure.CodeGraph;

/// <summary>
/// Replaces the graph contribution of an invalidated artifact closure while retaining
/// provenance owned by unaffected artifacts. The caller must include every invalidated
/// producer artifact in <paramref name="changedArtifactIds"/>; this normally includes the
/// reverse-dependency closure, not only files whose bytes changed.
/// </summary>
public static class GraphSnapshotDeltaComposer
{
    public static GraphSnapshotV2 Compose(
        GraphSnapshotV2 baseSnapshot,
        IReadOnlyCollection<string> changedArtifactIds,
        GraphSnapshotFragmentV2 fragment,
        string manifestVersion,
        DateTimeOffset createdAt,
        string workingTreeFingerprint,
        string mode,
        IReadOnlyList<GraphArtifactV2> updatedArtifacts,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<string>? capabilityGaps = null,
        string? headCommit = null,
        string status = "ready")
    {
        ArgumentNullException.ThrowIfNull(baseSnapshot);
        ArgumentNullException.ThrowIfNull(changedArtifactIds);
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(updatedArtifacts);

        if (!string.Equals(baseSnapshot.SchemaVersion, GraphSchemaV2.Version, StringComparison.Ordinal) ||
            !string.Equals(baseSnapshot.AnalysisProfile.GraphSchemaVersion, GraphSchemaV2.Version, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Artifact-delta composition requires graph schema {GraphSchemaV2.Version}.");

        var invalidated = changedArtifactIds
            .Select(NormalizeArtifactId)
            .ToHashSet(StringComparer.Ordinal);
        var sourceOwners = baseSnapshot.Nodes.ToDictionary(
            node => node.Id,
            node => node.Locations.Select(location => NormalizeArtifactId(location.ArtifactId))
                .Concat(node.Evidence.SelectMany(evidence => evidence.ArtifactIds).Select(NormalizeArtifactId))
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var retainedNodes = baseSnapshot.Nodes
            .Select(node => node with
            {
                Locations = node.Locations
                    .Where(location => !invalidated.Contains(NormalizeArtifactId(location.ArtifactId)))
                    .ToList(),
                Evidence = node.Evidence
                    .Where(evidence => !OwnsAny(evidence, invalidated))
                    .ToList(),
            })
            .Where(node => node.Locations.Count > 0 || node.Evidence.Count > 0 ||
                !sourceOwners[node.Id].Overlaps(invalidated))
            .ToList();

        var retainedEdges = baseSnapshot.Edges
            .Select(edge =>
            {
                var sourceWasInvalidated = sourceOwners.TryGetValue(edge.SourceId, out var owners) &&
                    owners.Overlaps(invalidated);
                var hadEvidence = edge.Evidence.Count > 0;
                var evidence = edge.Evidence
                    .Where(item => !OwnsAny(item, invalidated) &&
                        !(item.ArtifactIds.Count == 0 && sourceWasInvalidated))
                    .ToList();
                return (
                    Edge: edge with { Evidence = evidence },
                    SourceWasInvalidated: sourceWasInvalidated,
                    HadEvidence: hadEvidence);
            })
            .Where(item => item.Edge.Evidence.Count > 0 ||
                (!item.HadEvidence && !item.SourceWasInvalidated))
            .Select(item => item.Edge)
            .ToList();

        var mergedDiagnostics = diagnostics ?? baseSnapshot.Diagnostics
            .Where(value => !MentionsInvalidatedArtifact(value, invalidated))
            .Concat(fragment.Diagnostics)
            .ToList();
        var mergedGaps = capabilityGaps ?? baseSnapshot.CapabilityGaps
            .Concat(fragment.CapabilityGaps)
            .ToList();

        return GraphSnapshotCanonicalizer.CreateFromV2Entities(
            baseSnapshot.ProjectId,
            manifestVersion,
            createdAt,
            baseSnapshot.AnalysisProfile,
            workingTreeFingerprint,
            mode,
            updatedArtifacts,
            retainedNodes.Concat(fragment.Nodes).ToList(),
            retainedEdges.Concat(fragment.Edges).ToList(),
            mergedDiagnostics,
            mergedGaps,
            headCommit,
            status);
    }

    private static bool OwnsAny(
        GraphEvidenceV2 evidence,
        IReadOnlySet<string> invalidated) => evidence.ArtifactIds
        .Select(NormalizeArtifactId)
        .Any(invalidated.Contains);

    private static bool MentionsInvalidatedArtifact(
        string diagnostic,
        IReadOnlySet<string> invalidated) => invalidated.Any(id =>
    {
        var path = id.StartsWith("file:", StringComparison.Ordinal) ? id[5..] : id;
        return diagnostic.Replace('\\', '/').Contains(path, StringComparison.Ordinal);
    });

    private static string NormalizeArtifactId(string artifactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        return artifactId.StartsWith("file:", StringComparison.Ordinal)
            ? $"file:{artifactId[5..].Replace('\\', '/').TrimStart('/')}"
            : artifactId.Replace('\\', '/');
    }
}
