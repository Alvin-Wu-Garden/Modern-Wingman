namespace AgentService.Modules.GraphRAG.ParallelExtractor;

using System.Collections.Concurrent;

/// <summary>定義「ParallelProjectResult」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed record ParallelProjectResult(
    CodeGraphData Graph,
    int DocumentCount,
    int SourceDocumentCount,
    int SyntaxTreeCount,
    int SemanticDocumentCount,
    IReadOnlyList<string> Diagnostics);

/// <summary>定義「ParallelGraphRunResult」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed record ParallelGraphRunResult(
    GraphExtractionResult Summary,
    ParallelGraphManifest Manifest,
    double ProjectExtractionMilliseconds,
    double Neo4jWriteMilliseconds,
    double TotalMilliseconds);

/// <summary>定義「ParallelGraphManifest」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class ParallelGraphManifest
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _nodeIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _relationshipIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _entityIds = new(StringComparer.Ordinal);

    /// <summary>加入「Add」所代表的圖譜抽取或匯入工作。</summary>
    public void Add(CodeGraphData graph)
    {
        foreach (var node in graph.Nodes)
        {
            _nodeIds.GetOrAdd(node.Label, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
                .TryAdd(node.Id, 0);
            _entityIds.TryAdd(node.Id, 0);
        }

        foreach (var relationship in graph.Relationships)
        {
            var key = $"{relationship.StartId}|{relationship.EndId}";
            _relationshipIds.GetOrAdd(relationship.Type, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
                .TryAdd(key, 0);
        }
    }

    /// <summary>統計「Count」所代表的圖譜抽取或匯入工作。</summary>
    public int Count(string label)
        => _nodeIds.TryGetValue(label, out var ids) ? ids.Count : 0;

    /// <summary>統計「CountRelationships」所代表的圖譜抽取或匯入工作。</summary>
    public int CountRelationships(string relationshipType)
        => _relationshipIds.TryGetValue(relationshipType, out var ids) ? ids.Count : 0;

    public int EntityCount => _entityIds.Count;

    public IReadOnlyDictionary<string, int> NodeCounts
        => _nodeIds.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> RelationshipCounts
        => _relationshipIds.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
}

