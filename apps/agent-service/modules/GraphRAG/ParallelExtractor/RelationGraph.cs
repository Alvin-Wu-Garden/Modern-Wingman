using System.Security.Cryptography;
using System.Text;

namespace AgentService.Modules.GraphRAG.ParallelExtractor;

/// <summary>原始 ParallelExtractor 的跨前端／資料庫關係圖包裝。</summary>
internal sealed class RelationGraph
{
    private readonly CodeGraphData _inner = new();

    public IReadOnlyCollection<GraphNode> Nodes => _inner.Nodes;
    public IReadOnlyCollection<GraphRelationship> Relationships => _inner.Relationships;

    public GraphNode AddNode(string label, string id, IReadOnlyDictionary<string, object?>? properties = null)
        => _inner.AddNode(label, id, properties);

    public void AddRelationship(string relationshipType, string startId, string endId, IReadOnlyDictionary<string, object?>? properties = null)
        => _inner.AddRelationship(relationshipType, startId, endId, properties);
}

/// <summary>保留 ParallelExtractor 的 stable id 與絕對路徑正規化規則。</summary>
internal static class StableId
{
    public static string For(string prefix, params object?[] values)
    {
        var canonical = string.Join("\u001f", values.Select(value => value?.ToString() ?? string.Empty));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
        return $"{prefix}:{hash}";
    }

    public static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();

    public static string NormalizeObjectName(string value) =>
        value.Trim().Trim('[', ']').ToLowerInvariant();
}
