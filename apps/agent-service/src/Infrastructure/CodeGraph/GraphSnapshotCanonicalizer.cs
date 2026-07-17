using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.CodeGraph;

/// <summary>
/// Converts analyzer output into the deterministic V2 intermediate representation used by
/// golden graph and shadow-index comparisons. Runtime timestamps and absolute paths are
/// intentionally excluded from entity identity and equality.
/// </summary>
public static class GraphSnapshotCanonicalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static GraphSnapshotV2 Create(
        string projectId,
        string manifestVersion,
        DateTimeOffset createdAt,
        GraphAnalysisProfile analysisProfile,
        string workingTreeFingerprint,
        string mode,
        IReadOnlyList<IndexedFileManifest> artifacts,
        CodeAnalysisResult graph,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<string>? capabilityGaps = null,
        string? headCommit = null,
        string status = "ready")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestVersion);
        ArgumentNullException.ThrowIfNull(analysisProfile);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(graph);

        var canonicalArtifacts = artifacts
            .Select(ToArtifact)
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ToList();
        EnsureUnique(canonicalArtifacts.Select(item => item.Id), "artifact");
        var artifactsByPath = canonicalArtifacts.ToDictionary(
            item => item.Path,
            item => item,
            StringComparer.Ordinal);

        var sourceNodes = graph.Nodes
            .GroupBy(node => node.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => SelectPrimaryNode(group), StringComparer.Ordinal);
        var canonicalNodes = graph.Nodes
            .GroupBy(node => node.Key, StringComparer.Ordinal)
            .Select(group => ToNode(group, artifactsByPath))
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var edge in graph.Edges)
        {
            if (!sourceNodes.ContainsKey(edge.SourceKey) || !sourceNodes.ContainsKey(edge.TargetKey))
                throw new InvalidOperationException(
                    $"Dangling edge is not valid in graph schema V2: {EdgeIdentity(edge)}");
        }

        var canonicalEdges = graph.Edges
            .GroupBy(EdgeIdentity, StringComparer.Ordinal)
            .Select(group => ToEdge(group, sourceNodes, artifactsByPath))
            .OrderBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToList();

        return CreateFromV2Entities(
            projectId,
            manifestVersion,
            createdAt,
            analysisProfile,
            workingTreeFingerprint,
            mode,
            canonicalArtifacts,
            canonicalNodes,
            canonicalEdges,
            diagnostics,
            capabilityGaps,
            headCommit,
            status);
    }

    /// <summary>
    /// Rebuilds a canonical snapshot from already extracted V2 entities. This is the shared
    /// finalization path for full and artifact-delta indexing, so both modes use identical
    /// ordering, validation, evidence hashing and snapshot-hash semantics.
    /// </summary>
    public static GraphSnapshotV2 CreateFromV2Entities(
        string projectId,
        string manifestVersion,
        DateTimeOffset createdAt,
        GraphAnalysisProfile analysisProfile,
        string workingTreeFingerprint,
        string mode,
        IReadOnlyList<GraphArtifactV2> artifacts,
        IReadOnlyList<GraphNodeV2> nodes,
        IReadOnlyList<GraphEdgeV2> edges,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<string>? capabilityGaps = null,
        string? headCommit = null,
        string status = "ready")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestVersion);
        ArgumentNullException.ThrowIfNull(analysisProfile);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var canonicalArtifacts = artifacts
            .Select(item => item with
            {
                Id = NormalizeArtifactId(item.Id),
                Path = NormalizePath(item.Path),
                Kind = NormalizeToken(item.Kind, "unknown"),
            })
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ToList();
        EnsureUnique(canonicalArtifacts.Select(item => item.Id), "artifact");
        var artifactsById = canonicalArtifacts.ToDictionary(item => item.Id, StringComparer.Ordinal);

        var conflictingEdgeId = edges
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group
                .Select(EdgeIdentity)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any());
        if (conflictingEdgeId is not null)
            throw new InvalidOperationException(
                $"Conflicting edge definitions are not valid in graph schema V2: {conflictingEdgeId.Key}");

        var canonicalNodes = nodes
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => CanonicalizeNode(group, artifactsById))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        var nodeIds = canonicalNodes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (!nodeIds.Contains(edge.SourceId) || !nodeIds.Contains(edge.TargetId))
                throw new InvalidOperationException(
                    $"Dangling edge is not valid in graph schema V2: {EdgeIdentity(edge)}");
        }

        var canonicalEdges = edges
            .GroupBy(EdgeIdentity, StringComparer.Ordinal)
            .Select(group => CanonicalizeEdge(group, artifactsById))
            .OrderBy(item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.TargetId, StringComparer.Ordinal)
            .ThenBy(item => item.Directed)
            .ToList();

        var normalizedDiagnostics = (diagnostics ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var normalizedGaps = (capabilityGaps ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var normalizedProfile = analysisProfile with
        {
            Analyzers = analysisProfile.Analyzers
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ToList(),
        };

        var hashPayload = new SnapshotHashPayload(
            GraphSchemaV2.Version,
            normalizedProfile,
            workingTreeFingerprint,
            headCommit,
            canonicalArtifacts,
            canonicalNodes,
            canonicalEdges,
            normalizedDiagnostics,
            normalizedGaps);
        var snapshotHash = Hash(JsonSerializer.Serialize(hashPayload, JsonOptions));

        return new GraphSnapshotV2(
            GraphSchemaV2.Version,
            projectId,
            manifestVersion,
            createdAt,
            normalizedProfile,
            new GraphSnapshotMetadataV2(
                snapshotHash,
                workingTreeFingerprint,
                mode,
                status,
                canonicalNodes.Count,
                canonicalEdges.Count,
                headCommit),
            canonicalArtifacts,
            canonicalNodes,
            canonicalEdges,
            normalizedDiagnostics,
            normalizedGaps);
    }

    private static GraphNodeV2 CanonicalizeNode(
        IEnumerable<GraphNodeV2> sources,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsById)
    {
        var sourceList = sources.ToList();
        var primary = sourceList
            .OrderBy(item => item.Evidence.Select(evidence => EvidenceRank(evidence.Confidence)).DefaultIfEmpty(99).Min())
            .ThenBy(item => item.Evidence.FirstOrDefault()?.Extractor.Id ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Locations.FirstOrDefault()?.ArtifactId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Signature ?? string.Empty, StringComparer.Ordinal)
            .First();

        foreach (var candidate in sourceList)
        {
            if (candidate.Kind != primary.Kind ||
                !IsCompatibleSemanticValue(primary.Kind, candidate.Name, primary.Name) ||
                !IsCompatibleSemanticValue(primary.Kind, candidate.Signature, primary.Signature) ||
                !IsCompatibleLanguage(primary.Kind, candidate.Language, primary.Language))
            {
                throw new InvalidOperationException(
                    $"Conflicting node definitions are not valid in graph schema V2: {primary.Id}; " +
                    $"primary=({primary.Kind},{primary.Name},{primary.Signature},{primary.Language}), " +
                    $"candidate=({candidate.Kind},{candidate.Name},{candidate.Signature},{candidate.Language})");
            }
        }

        var locations = sourceList
            .SelectMany(item => item.Locations)
            .Select(item => item with { ArtifactId = NormalizeArtifactId(item.ArtifactId) })
            .Select(item =>
            {
                EnsureArtifactExists(item.ArtifactId, artifactsById);
                return item;
            })
            .Distinct()
            .OrderBy(item => item.ArtifactId, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine)
            .ThenBy(item => item.EndLine)
            .ThenBy(item => item.Role, StringComparer.Ordinal)
            .ToList();
        var evidence = CanonicalizeEvidence(
            sourceList.SelectMany(item => item.Evidence), artifactsById);

        return primary with { Locations = locations, Evidence = evidence };
    }

    private static GraphEdgeV2 CanonicalizeEdge(
        IEnumerable<GraphEdgeV2> sources,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsById)
    {
        var sourceList = sources.ToList();
        var primary = sourceList[0];
        var direction = primary.Directed ? "forward" : "undirected";
        return primary with
        {
            Id = Hash($"{primary.SourceId}\0{primary.Kind}\0{primary.TargetId}\0{direction}"),
            Evidence = CanonicalizeEvidence(sourceList.SelectMany(item => item.Evidence), artifactsById),
        };
    }

    private static IReadOnlyList<GraphEvidenceV2> CanonicalizeEvidence(
        IEnumerable<GraphEvidenceV2> sources,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsById) => sources
        .Select(item =>
        {
            var artifactIds = item.ArtifactIds
                .Select(NormalizeArtifactId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            foreach (var artifactId in artifactIds)
                EnsureArtifactExists(artifactId, artifactsById);
            return item with
            {
                Extractor = new GraphAnalyzerIdentity(
                    NormalizeToken(item.Extractor.Id, "unknown"),
                    NormalizeToken(item.Extractor.Version, "unknown")),
                ArtifactIds = artifactIds,
                ContentHash = ProducerHashById(artifactIds, item.ContentHash, artifactsById),
            };
        })
        .DistinctBy(EvidenceIdentity, StringComparer.Ordinal)
        .OrderBy(item => EvidenceRank(item.Confidence))
        .ThenBy(item => item.Extractor.Id, StringComparer.Ordinal)
        .ThenBy(item => item.Extractor.Version, StringComparer.Ordinal)
        .ThenBy(item => string.Join('\n', item.ArtifactIds), StringComparer.Ordinal)
        .ToList();

    private static string EdgeIdentity(GraphEdgeV2 edge) =>
        $"{edge.SourceId}\0{edge.Kind}\0{edge.TargetId}\0{(edge.Directed ? "forward" : "undirected")}";

    private static string NormalizeArtifactId(string artifactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        return artifactId.StartsWith("file:", StringComparison.Ordinal)
            ? $"file:{NormalizePath(artifactId[5..])}"
            : artifactId.Replace('\\', '/');
    }

    private static void EnsureArtifactExists(
        string artifactId,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsById)
    {
        if (!artifactsById.ContainsKey(artifactId))
            throw new InvalidOperationException(
                $"Unknown artifact reference is not valid in graph schema V2: {artifactId}");
    }

    private static string? ProducerHashById(
        IReadOnlyList<string> artifactIds,
        string? fallback,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsById)
    {
        if (artifactIds.Count == 0) return fallback;
        return Hash(string.Join('\n', artifactIds.Select(id => $"{id}:{artifactsById[id].ContentHash}")));
    }

    public static string Serialize(GraphSnapshotV2 snapshot, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var options = new JsonSerializerOptions(JsonOptions) { WriteIndented = indented };
        return JsonSerializer.Serialize(snapshot, options);
    }

    public static CodeAnalysisResult ToAnalysisResult(GraphSnapshotV2 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = new CodeAnalysisResult();
        foreach (var node in snapshot.Nodes)
        {
            var location = node.Locations.FirstOrDefault();
            var evidence = node.Evidence.FirstOrDefault();
            result.Nodes.Add(new CodeNode
            {
                Key = node.Id,
                Kind = node.Kind,
                Name = node.Name,
                Signature = node.Signature,
                FilePath = location?.ArtifactId.StartsWith("file:", StringComparison.Ordinal) == true
                    ? location.ArtifactId[5..]
                    : null,
                StartLine = location?.StartLine,
                EndLine = location?.EndLine,
                Language = node.Language,
                Technology = node.Technology,
                DocComment = node.DocComment,
                SourceKind = evidence?.SourceKind ?? GraphSourceKind.Unknown,
                Confidence = evidence?.Confidence ?? GraphConfidence.Unknown,
                ExtractorId = evidence?.Extractor.Id,
                ExtractorVersion = evidence?.Extractor.Version,
                ContentHash = evidence?.ContentHash,
                Reason = evidence?.Reason,
                LocationsJson = JsonSerializer.Serialize(node.Locations, JsonOptions),
                EvidenceJson = JsonSerializer.Serialize(node.Evidence, JsonOptions),
                IndexedAt = snapshot.CreatedAt,
            });
        }

        foreach (var edge in snapshot.Edges)
        {
            var evidence = edge.Evidence.FirstOrDefault();
            result.Edges.Add(new CodeEdge
            {
                SourceKey = edge.SourceId,
                TargetKey = edge.TargetId,
                Kind = edge.Kind,
                SourceKind = evidence?.SourceKind ?? GraphSourceKind.Unknown,
                Confidence = evidence?.Confidence ?? GraphConfidence.Unknown,
                ExtractorId = evidence?.Extractor.Id,
                ExtractorVersion = evidence?.Extractor.Version,
                ContentHash = evidence?.ContentHash,
                Reason = evidence?.Reason,
                EvidenceJson = JsonSerializer.Serialize(edge.Evidence, JsonOptions),
                IndexedAt = snapshot.CreatedAt,
            });
        }

        return result;
    }

    private static GraphArtifactV2 ToArtifact(IndexedFileManifest source)
    {
        var path = NormalizePath(source.RelativePath);
        return new GraphArtifactV2(
            $"file:{path}",
            path,
            NormalizeToken(source.Language, "unknown"),
            null,
            source.Length,
            source.ContentHash,
            source.Status,
            source.Reason);
    }

    private static GraphNodeV2 ToNode(
        IEnumerable<CodeNode> sources,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsByPath)
    {
        var sourceList = sources.ToList();
        var source = SelectPrimaryNode(sourceList);
        EnsureCompatibleNodes(sourceList, source);
        var locations = sourceList
            .SelectMany(item => ResolveArtifactIds(item.FilePath, artifactsByPath)
                .Select(artifactId => new GraphLocationV2(
                    artifactId, item.StartLine, item.EndLine, "declaration")))
            .Distinct()
            .OrderBy(item => item.ArtifactId, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine)
            .ThenBy(item => item.EndLine)
            .ToList();
        var evidence = sourceList
            .Select(item => CreateEvidence(
                item.SourceKind,
                item.Confidence,
                item.ExtractorId,
                item.ExtractorVersion,
                ResolveArtifactIds(item.FilePath, artifactsByPath),
                item.ContentHash,
                item.Reason,
                artifactsByPath))
            .DistinctBy(EvidenceIdentity, StringComparer.Ordinal)
            .OrderBy(item => EvidenceRank(item.Confidence))
            .ThenBy(item => item.Extractor.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Extractor.Version, StringComparer.Ordinal)
            .ThenBy(item => string.Join('\n', item.ArtifactIds), StringComparer.Ordinal)
            .ToList();
        return new GraphNodeV2(
            source.Key,
            source.Kind,
            source.Name,
            source.Signature,
            source.Language,
            source.Technology,
            source.DocComment,
            locations,
            evidence);
    }

    private static GraphEdgeV2 ToEdge(
        IEnumerable<CodeEdge> sources,
        IReadOnlyDictionary<string, CodeNode> sourceNodes,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsByPath)
    {
        var sourceList = sources.ToList();
        var source = sourceList[0];
        var evidence = sourceList
            .Select(item => CreateEvidence(
                item.SourceKind,
                item.Confidence,
                item.ExtractorId,
                item.ExtractorVersion,
                ResolveArtifactIds(
                    item.ArtifactPath ?? sourceNodes[item.SourceKey].FilePath,
                    artifactsByPath),
                item.ContentHash,
                item.Reason,
                artifactsByPath))
            .DistinctBy(EvidenceIdentity, StringComparer.Ordinal)
            .OrderBy(item => EvidenceRank(item.Confidence))
            .ThenBy(item => item.Extractor.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Extractor.Version, StringComparer.Ordinal)
            .ThenBy(item => string.Join('\n', item.ArtifactIds), StringComparer.Ordinal)
            .ToList();

        return new GraphEdgeV2(
            Hash($"{source.SourceKey}\0{source.Kind}\0{source.TargetKey}\0forward"),
            source.SourceKey,
            source.Kind,
            source.TargetKey,
            true,
            evidence);
    }

    private static GraphEvidenceV2 CreateEvidence(
        GraphSourceKind sourceKind,
        GraphConfidence confidence,
        string? extractorId,
        string? extractorVersion,
        IReadOnlyList<string> artifactIds,
        string? fallbackContentHash,
        string? reason,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsByPath)
    {
        var sortedArtifactIds = artifactIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var contentHash = ProducerHash(sortedArtifactIds, fallbackContentHash, artifactsByPath);
        return new GraphEvidenceV2(
            sourceKind,
            confidence,
            new GraphAnalyzerIdentity(
                NormalizeToken(extractorId, "unknown"),
                NormalizeToken(extractorVersion, "unknown")),
            sortedArtifactIds,
            contentHash,
            reason);
    }

    private static IReadOnlyList<string> ResolveArtifactIds(
        string? filePath,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsByPath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return [];
        var path = NormalizePath(filePath);
        return artifactsByPath.TryGetValue(path, out var artifact)
            ? [artifact.Id]
            : [];
    }

    private static string? ProducerHash(
        IReadOnlyList<string> artifactIds,
        string? fallback,
        IReadOnlyDictionary<string, GraphArtifactV2> artifactsByPath)
    {
        if (artifactIds.Count == 0) return fallback;
        var values = artifactIds.Select(id =>
        {
            var path = id.StartsWith("file:", StringComparison.Ordinal) ? id[5..] : id;
            return artifactsByPath.TryGetValue(path, out var artifact)
                ? $"{id}:{artifact.ContentHash}"
                : id;
        });
        return Hash(string.Join('\n', values));
    }

    private static string EdgeIdentity(CodeEdge edge) =>
        $"{edge.SourceKey}\0{edge.Kind}\0{edge.TargetKey}\0forward";

    private static string EvidenceIdentity(GraphEvidenceV2 evidence) =>
        JsonSerializer.Serialize(evidence, JsonOptions);

    private static CodeNode SelectPrimaryNode(IEnumerable<CodeNode> nodes) => nodes
        .OrderBy(node => EvidenceRank(node.Confidence))
        .ThenBy(node => node.ExtractorId ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(node => node.ExtractorVersion ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(node => NormalizePath(node.FilePath ?? string.Empty), StringComparer.Ordinal)
        .ThenBy(node => node.StartLine)
        .First();

    private static void EnsureCompatibleNodes(IReadOnlyList<CodeNode> nodes, CodeNode primary)
    {
        foreach (var node in nodes)
        {
            if (node.Kind != primary.Kind ||
                !IsCompatibleSemanticValue(primary.Kind, node.Name, primary.Name) ||
                !IsCompatibleSemanticValue(primary.Kind, node.Signature, primary.Signature) ||
                !IsCompatibleLanguage(primary.Kind, node.Language, primary.Language))
            {
                throw new InvalidOperationException(
                    $"Conflicting node definitions are not valid in graph schema V2: {primary.Key}; " +
                    $"primary=({primary.Kind},{primary.Name},{primary.Signature},{primary.Language}), " +
                    $"candidate=({node.Kind},{node.Name},{node.Signature},{node.Language})");
            }
        }
    }

    /// <summary>
    /// A relational artifact is identified by its normalized key and kind. The same table or
    /// column is commonly observed through case-insensitive SQL, an ORM mapping and multiple
    /// historical migrations. Those observations legitimately have different display casing,
    /// source language and (for columns/constraints) declaration signatures; their distinct
    /// provenance is retained in Evidence and Locations. Code symbols remain strict because a
    /// mismatched name/signature there indicates an analyzer identity collision.
    /// </summary>
    private static bool IsCompatibleSemanticValue(CodeNodeKind kind, string? left, string? right) =>
        IsRelationalArtifact(kind) || string.Equals(left, right, StringComparison.Ordinal);

    private static bool IsCompatibleLanguage(CodeNodeKind kind, string? left, string? right) =>
        IsRelationalArtifact(kind) || string.Equals(left, right, StringComparison.Ordinal);

    private static bool IsRelationalArtifact(CodeNodeKind kind) => kind is
        CodeNodeKind.DataStore or
        CodeNodeKind.Schema or
        CodeNodeKind.Table or
        CodeNodeKind.Column or
        CodeNodeKind.PrimaryKey or
        CodeNodeKind.ForeignKey or
        CodeNodeKind.Index or
        CodeNodeKind.Constraint or
        CodeNodeKind.View or
        CodeNodeKind.Procedure;

    private static int EvidenceRank(GraphConfidence confidence) => confidence switch
    {
        GraphConfidence.Exact => 0,
        GraphConfidence.Resolved => 1,
        GraphConfidence.Confirmed => 2,
        GraphConfidence.Heuristic => 3,
        GraphConfidence.Inferred => 4,
        _ => 5,
    };

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string NormalizeToken(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void EnsureUnique(IEnumerable<string> identities, string entityType)
    {
        var duplicate = identities
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Duplicate {entityType} identity is not valid in graph schema V2: {duplicate.Key}");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record SnapshotHashPayload(
        string SchemaVersion,
        GraphAnalysisProfile AnalysisProfile,
        string WorkingTreeFingerprint,
        string? HeadCommit,
        IReadOnlyList<GraphArtifactV2> Artifacts,
        IReadOnlyList<GraphNodeV2> Nodes,
        IReadOnlyList<GraphEdgeV2> Edges,
        IReadOnlyList<string> Diagnostics,
        IReadOnlyList<string> CapabilityGaps);
}

public static class GraphSnapshotComparer
{
    public static GraphComparisonResult Compare(GraphSnapshotV2 expected, GraphSnapshotV2 actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var differences = new List<GraphDifference>();
        CompareEntities(
            "artifact",
            expected.Artifacts.ToDictionary(item => item.Id, StringComparer.Ordinal),
            actual.Artifacts.ToDictionary(item => item.Id, StringComparer.Ordinal),
            differences);
        CompareEntities(
            "node",
            expected.Nodes.ToDictionary(item => item.Id, StringComparer.Ordinal),
            actual.Nodes.ToDictionary(item => item.Id, StringComparer.Ordinal),
            differences);
        CompareEntities(
            "edge",
            expected.Edges.ToDictionary(item => item.Id, StringComparer.Ordinal),
            actual.Edges.ToDictionary(item => item.Id, StringComparer.Ordinal),
            differences);
        CompareScalar("analysisProfile", expected.AnalysisProfile, actual.AnalysisProfile, differences);
        CompareScalar("diagnostics", expected.Diagnostics, actual.Diagnostics, differences);
        CompareScalar("capabilityGaps", expected.CapabilityGaps, actual.CapabilityGaps, differences);

        return new GraphComparisonResult(differences
            .OrderBy(item => item.EntityType, StringComparer.Ordinal)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ToList());
    }

    private static void CompareEntities<T>(
        string entityType,
        IReadOnlyDictionary<string, T> expected,
        IReadOnlyDictionary<string, T> actual,
        ICollection<GraphDifference> differences)
    {
        foreach (var (identity, expectedValue) in expected)
        {
            if (!actual.TryGetValue(identity, out var actualValue))
            {
                differences.Add(new(entityType, identity, GraphDifferenceKind.Missing,
                    JsonSerializer.Serialize(expectedValue), null));
            }
            else
            {
                var expectedJson = JsonSerializer.Serialize(expectedValue);
                var actualJson = JsonSerializer.Serialize(actualValue);
                if (!string.Equals(expectedJson, actualJson, StringComparison.Ordinal))
                    differences.Add(new(entityType, identity, GraphDifferenceKind.Changed,
                        expectedJson, actualJson));
            }
        }

        foreach (var (identity, actualValue) in actual)
        {
            if (!expected.ContainsKey(identity))
                differences.Add(new(entityType, identity, GraphDifferenceKind.Unexpected,
                    null, JsonSerializer.Serialize(actualValue)));
        }
    }

    private static void CompareScalar<T>(
        string identity,
        T expected,
        T actual,
        ICollection<GraphDifference> differences)
    {
        var expectedJson = JsonSerializer.Serialize(expected);
        var actualJson = JsonSerializer.Serialize(actual);
        if (!string.Equals(expectedJson, actualJson, StringComparison.Ordinal))
            differences.Add(new("snapshot", identity, GraphDifferenceKind.Changed,
                expectedJson, actualJson));
    }
}
