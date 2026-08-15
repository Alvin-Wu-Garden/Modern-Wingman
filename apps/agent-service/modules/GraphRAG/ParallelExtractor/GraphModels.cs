namespace AgentService.Modules.GraphRAG.ParallelExtractor;

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

/// <summary>定義「CodeGraphData」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class CodeGraphData
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphRelationship> _relationships = new(StringComparer.Ordinal);

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;
    public IReadOnlyCollection<GraphRelationship> Relationships => _relationships.Values;

    public GraphNode AddNode(string label, string id, IReadOnlyDictionary<string, object?>? properties = null)
    {
        var key = $"{label}|{id}";
        if (!_nodes.TryGetValue(key, out var node))
        {
            node = new GraphNode(label, id);
            _nodes.Add(key, node);
        }

        if (properties is not null)
        {
            foreach (var property in properties)
            {
                if (property.Value is not null)
                {
                    node.Properties[property.Key] = property.Value;
                }
            }
        }

        return node;
    }

    /// <summary>加入「AddRelationship」所代表的圖譜抽取或匯入工作。</summary>
    public GraphRelationship AddRelationship(
        string relationshipType,
        string startId,
        string endId,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var key = $"{relationshipType}|{startId}|{endId}";
        if (!_relationships.TryGetValue(key, out var relationship))
        {
            relationship = new GraphRelationship(relationshipType, startId, endId);
            _relationships.Add(key, relationship);
        }

        relationship.OccurrenceCount++;
        if (properties is null)
        {
            return relationship;
        }

        foreach (var property in properties)
        {
            if (property.Value is null)
            {
                continue;
            }

            if (property.Key == "locations" && property.Value is IEnumerable<string> locations)
            {
                foreach (var location in locations)
                {
                    if (relationship.Locations.Count >= 20)
                    {
                        break;
                    }

                    if (!relationship.Locations.Contains(location, StringComparer.Ordinal))
                    {
                        relationship.Locations.Add(location);
                    }
                }
            }
            else if (!relationship.Properties.ContainsKey(property.Key))
            {
                relationship.Properties[property.Key] = property.Value;
            }
        }

        return relationship;
    }

    /// <summary>
    /// 重現原始 Neo4jGraphWriter 對跨 fragment 重複關係執行
    /// <c>MERGE ... SET r += row.properties</c> 的覆寫語意。
    /// 同一個 fragment 內仍由 <see cref="AddRelationship"/> 累加 occurrence；
    /// 不同 fragment 則以最後一次寫入的 occurrence、locations 與屬性為準。
    /// </summary>
    public void ApplyNeo4jWrite(GraphRelationship incoming)
    {
        var key = $"{incoming.Type}|{incoming.StartId}|{incoming.EndId}";
        if (!_relationships.TryGetValue(key, out var relationship))
        {
            relationship = new GraphRelationship(incoming.Type, incoming.StartId, incoming.EndId);
            _relationships.Add(key, relationship);
        }

        foreach (var property in incoming.Properties)
        {
            if (property.Value is not null)
            {
                relationship.Properties[property.Key] = property.Value;
            }
        }
        relationship.OccurrenceCount = incoming.OccurrenceCount;
        if (incoming.Locations.Count > 0)
        {
            relationship.Locations.Clear();
            relationship.Locations.AddRange(incoming.Locations);
        }
    }

    /// <summary>
    /// 重現原始 <c>Neo4jIntegration.WriteRelationshipBatchAsync</c> 的寫入語意。
    /// 資料庫階段只寫一般屬性與 occurrenceCount，刻意不序列化
    /// <see cref="GraphRelationship.Locations"/>；若同一條關係已由後端建立，
    /// Neo4j 的 <c>SET r += row.properties</c> 也會保留既有 locations。
    /// </summary>
    public void ApplyNeo4jIntegrationWrite(GraphRelationship incoming)
    {
        var key = $"{incoming.Type}|{incoming.StartId}|{incoming.EndId}";
        if (!_relationships.TryGetValue(key, out var relationship))
        {
            relationship = new GraphRelationship(incoming.Type, incoming.StartId, incoming.EndId);
            _relationships.Add(key, relationship);
        }

        foreach (var property in incoming.Properties)
        {
            if (property.Value is not null)
            {
                relationship.Properties[property.Key] = property.Value;
            }
        }
        relationship.OccurrenceCount = incoming.OccurrenceCount;
    }
}

/// <summary>定義「GraphNode」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class GraphNode
{
    /// <summary>執行「GraphNode」所代表的圖譜抽取或匯入工作。</summary>
    public GraphNode(string label, string id)
    {
        Label = label;
        Id = id;
    }

    public string Label { get; }
    public string Id { get; }
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);
}

/// <summary>定義「GraphRelationship」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class GraphRelationship
{
    /// <summary>執行「GraphRelationship」所代表的圖譜抽取或匯入工作。</summary>
    public GraphRelationship(string type, string startId, string endId)
    {
        Type = type;
        StartId = startId;
        EndId = endId;
    }

    public string Type { get; }
    public string StartId { get; }
    public string EndId { get; }
    public string SourceId => StartId;
    public string TargetId => EndId;
    public int OccurrenceCount { get; set; }
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);
    public List<string> Locations { get; } = new();
}

/// <summary>定義「GraphExtractionResult」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed record GraphExtractionResult(
    CodeGraphData Graph,
    int ProjectCount,
    int DocumentCount,
    int SourceDocumentCount,
    int SyntaxTreeCount,
    int TypeCount,
    int MethodCount,
    int CallRelationshipCount,
    int ExternalSymbolCount,
    int CodeChunkCount,
    int SemanticDocumentCount,
    IReadOnlyList<string> Diagnostics,
    double LoadMilliseconds,
    double ExtractionMilliseconds);

/// <summary>定義「GraphIds」資料結構或服務職責，供圖譜抽取流程使用。</summary>
static class GraphIds
{
    /// <summary>正規化「NormalizePath」所代表的圖譜抽取或匯入工作。</summary>
    public static string Solution(string solutionPath) => Create("solution", NormalizePath(solutionPath));

    /// <summary>執行「Project」所代表的圖譜抽取或匯入工作。</summary>
    public static string Project(Project project)
    {
        var path = project.FilePath is null ? string.Empty : NormalizePath(project.FilePath);
        return Create("project", path, project.Name, project.AssemblyName ?? project.Name);
    }

    /// <summary>正規化「NormalizePath」所代表的圖譜抽取或匯入工作。</summary>
    public static string File(string filePath) => Create("file", NormalizePath(filePath));

    /// <summary>執行「Create」所代表的圖譜抽取或匯入工作。</summary>
    public static string Namespace(string namespaceName) => Create("namespace", namespaceName);

    /// <summary>執行「Create」所代表的圖譜抽取或匯入工作。</summary>
    public static string Type(string projectId, string fullName) => Create("type", projectId, fullName);

    /// <summary>執行「Method」所代表的圖譜抽取或匯入工作。</summary>
    public static string Method(string projectId, string containingTypeName, string signature)
        => Create("method", projectId, containingTypeName, signature);

    /// <summary>執行「FallbackMethod」所代表的圖譜抽取或匯入工作。</summary>
    public static string FallbackMethod(string projectId, string fileId, int spanStart, string name)
        => Create("method-fallback", projectId, fileId, spanStart.ToString(), name);

    /// <summary>執行「Chunk」所代表的圖譜抽取或匯入工作。</summary>
    public static string Chunk(string ownerId, string fileId, int spanStart)
        => Create("chunk", ownerId, fileId, spanStart.ToString());

    /// <summary>執行「External」所代表的圖譜抽取或匯入工作。</summary>
    public static string External(string symbolKind, string assemblyName, string displayName)
        => Create("external", symbolKind, assemblyName, displayName);

    /// <summary>正規化「NormalizePath」所代表的圖譜抽取或匯入工作。</summary>
    public static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();

    /// <summary>執行「StripGlobalPrefix」所代表的圖譜抽取或匯入工作。</summary>
    public static string StripGlobalPrefix(string displayName)
        => displayName.StartsWith("global::", StringComparison.Ordinal)
            ? displayName[8..]
            : displayName;

    /// <summary>執行「HashText」所代表的圖譜抽取或匯入工作。</summary>
    public static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>執行「Create」所代表的圖譜抽取或匯入工作。</summary>
    private static string Create(string prefix, params string[] values)
    {
        var canonical = string.Join("\u001f", values);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
        return $"{prefix}:{hash}";
    }
}

/// <summary>定義「GraphSymbolFormatting」資料結構或服務職責，供圖譜抽取流程使用。</summary>
static class GraphSymbolFormatting
{
    /// <summary>執行「TypeName」所代表的圖譜抽取或匯入工作。</summary>
    public static string TypeName(INamedTypeSymbol symbol)
        => GraphIds.StripGlobalPrefix(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    /// <summary>執行「MethodSignature」所代表的圖譜抽取或匯入工作。</summary>
    public static string MethodSignature(IMethodSymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    /// <summary>執行「NamespaceName」所代表的圖譜抽取或匯入工作。</summary>
    public static string NamespaceName(INamespaceSymbol symbol)
        => symbol.IsGlobalNamespace ? string.Empty : symbol.ToDisplayString();

    /// <summary>執行「ToString」所代表的圖譜抽取或匯入工作。</summary>
    public static string Accessibility(ISymbol symbol) => symbol.DeclaredAccessibility.ToString();

    /// <summary>執行「Modifiers」所代表的圖譜抽取或匯入工作。</summary>
    public static string Modifiers(IMethodSymbol symbol)
    {
        var modifiers = new List<string>();
        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");
        if (symbol.IsSealed) modifiers.Add("sealed");
        if (symbol.IsAsync) modifiers.Add("async");
        return string.Join(' ', modifiers);
    }
}
