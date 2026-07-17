using AgentService.Domain.Models;

namespace AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;

internal sealed class DataGraphBuilder(string extractorId, string extractorVersion, string filePath, string contentHash)
{
    private readonly DateTimeOffset _indexedAt = DateTimeOffset.UtcNow;
    private readonly HashSet<string> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _edges = new(StringComparer.Ordinal);
    public CodeAnalysisResult Result { get; } = new();

    public string AddNode(string key, CodeNodeKind kind, string name, string language, GraphSourceKind source, GraphConfidence confidence, int? line = null, string? signature = null, string? technology = null, string? reason = null)
    {
        var evidenceIdentity = string.Join('|', key, kind, source, confidence, line, signature, technology, reason);
        if (_nodes.Add(evidenceIdentity)) Result.Nodes.Add(new CodeNode
        {
            Key = key, Kind = kind, Name = name, Signature = signature, FilePath = filePath,
            StartLine = line, EndLine = line, Language = language, Technology = technology,
            SourceKind = source, Confidence = confidence, ExtractorId = extractorId,
            ExtractorVersion = extractorVersion, IndexedAt = _indexedAt, ContentHash = contentHash, Reason = reason,
        });
        return key;
    }

    public void AddEdge(string source, string target, CodeEdgeKind kind, GraphSourceKind sourceKind, GraphConfidence confidence, string? reason = null)
    {
        var evidenceIdentity = string.Join('|', source, kind, target, sourceKind, confidence, reason);
        if (_edges.Add(evidenceIdentity)) Result.Edges.Add(new CodeEdge
        {
            SourceKey = source, TargetKey = target, Kind = kind, SourceKind = sourceKind,
            Confidence = confidence, ExtractorId = extractorId, ExtractorVersion = extractorVersion, Reason = reason,
            IndexedAt = _indexedAt, ContentHash = contentHash, ArtifactPath = filePath,
        });
    }

    public string EnsureFile(string language, GraphSourceKind source, GraphConfidence confidence)
    {
        var key = $"file:{filePath}";
        return AddNode(key, CodeNodeKind.File, Path.GetFileName(filePath), language, source, confidence,
            reason: "Data artifact containing this extracted fact.");
    }

    public string EnsureStore() => AddNode("data-store:relational", CodeNodeKind.DataStore, "Relational database", "sql", GraphSourceKind.Sql, GraphConfidence.Resolved, technology: "relational");

    public string EnsureSchema(string schema)
    {
        var store = EnsureStore();
        var key = $"schema:{Normalize(schema)}";
        AddNode(key, CodeNodeKind.Schema, schema, "sql", GraphSourceKind.Sql, GraphConfidence.Resolved);
        AddEdge(store, key, CodeEdgeKind.Contains, GraphSourceKind.Sql, GraphConfidence.Resolved);
        return key;
    }

    public string EnsureTable(string identifier, GraphSourceKind source = GraphSourceKind.Sql, GraphConfidence confidence = GraphConfidence.Resolved)
    {
        var (schema, name) = SplitObject(identifier);
        var schemaKey = EnsureSchema(schema);
        var key = $"table:{Normalize(schema)}.{Normalize(name)}";
        AddNode(key, CodeNodeKind.Table, name, "sql", source, confidence, signature: $"{schema}.{name}");
        AddEdge(schemaKey, key, CodeEdgeKind.Contains, source, confidence);
        return key;
    }

    public string EnsureColumn(string tableKey, string columnName, GraphSourceKind source = GraphSourceKind.Sql,
        GraphConfidence confidence = GraphConfidence.Resolved, int? line = null, string? reason = null)
    {
        var cleanName = columnName.Trim().Trim('[', ']', '`', '"');
        var key = $"column:{tableKey[6..]}.{Normalize(cleanName)}";
        AddNode(key, CodeNodeKind.Column, cleanName, "sql", source, confidence, line, reason: reason);
        AddEdge(tableKey, key, CodeEdgeKind.Contains, source, confidence, reason);
        return key;
    }

    public static (string Schema, string Name) SplitObject(string identifier)
    {
        var clean = identifier.Trim().Trim('[', ']', '`', '"').Replace("][", ".", StringComparison.Ordinal);
        var parts = clean.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim('[', ']', '`', '"')).ToArray();
        return parts.Length > 1 ? (parts[^2], parts[^1]) : ("default", parts.Length == 0 ? "unknown" : parts[0]);
    }

    public static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace(' ', '_');
}
