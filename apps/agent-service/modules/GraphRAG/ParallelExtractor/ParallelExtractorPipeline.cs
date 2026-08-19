using AgentService.Modules.GraphRAG.ExtractedGraph;

namespace AgentService.Modules.GraphRAG.ParallelExtractor;

/// <summary>AgentService 使用的 ParallelExtractor 結果與逐類統計。</summary>
public sealed record ParallelExtractorPipelineResult(
    GraphDocument Document,
    int BackendNodeCount,
    int BackendRelationshipCount,
    int FrontendNodeCount,
    int FrontendRelationshipCount,
    int DatabaseNodeCount,
    int DatabaseRelationshipCount,
    IReadOnlyDictionary<string, int> NodeCounts,
    IReadOnlyDictionary<string, int> RelationshipCounts,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// AgentService 與獨立 Wingman.ParallelExtractor 的薄型轉接器；只負責選 Solution、
/// 將原始 label/type 轉成受控儲存契約及寫入繁中統計 Log。
/// </summary>
public sealed class ParallelExtractorPipeline(
    ParallelExtractionEngine engine,
    ILogger<ParallelExtractorPipeline> logger)
{
    /// <summary>完整執行一次抽取；不建立檔案監看器或增量狀態。</summary>
    public async Task<ParallelExtractorPipelineResult> ExtractAsync(
        string projectRoot,
        string? selectedSolutionPath,
        string? sqlServerConnectionString,
        bool includeCodeChunkText,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken = default)
    {
        var solutionPath = ResolveSolutionPath(projectRoot, selectedSolutionPath);
        var result = await engine.ExtractAsync(
            solutionPath,
            sqlServerConnectionString,
            includeCodeChunkText,
            maxDegreeOfParallelism,
            cancellationToken);
        var sourceRoot = Path.GetDirectoryName(solutionPath)
            ?? throw new InvalidOperationException("無法取得 Solution 所在目錄。");
        var document = ToGraphDocument(
            result,
            sourceRoot,
            string.IsNullOrWhiteSpace(sqlServerConnectionString)
                ? "SourceOnly"
                : "SqlServer");
        logger.LogInformation(
            "ParallelExtractor 完整抽取完成。Solution={SolutionPath}, Parallelism={Parallelism}, IncludeCodeChunkText={IncludeCodeChunkText}, BackendNodes={BackendNodes}, BackendEdges={BackendEdges}, FrontendNodes={FrontendNodes}, FrontendEdges={FrontendEdges}, DatabaseNodes={DatabaseNodes}, DatabaseEdges={DatabaseEdges}, Nodes={Nodes}, Edges={Edges}",
            solutionPath,
            maxDegreeOfParallelism,
            includeCodeChunkText,
            result.BackendNodeCount,
            result.BackendRelationshipCount,
            result.FrontendNodeCount,
            result.FrontendRelationshipCount,
            result.DatabaseNodeCount,
            result.DatabaseRelationshipCount,
            document.Nodes.Count,
            document.Relationships.Count);
        return new ParallelExtractorPipelineResult(
            document,
            result.BackendNodeCount,
            result.BackendRelationshipCount,
            result.FrontendNodeCount,
            result.FrontendRelationshipCount,
            result.DatabaseNodeCount,
            result.DatabaseRelationshipCount,
            result.NodeCounts,
            result.RelationshipCounts,
            result.Diagnostics);
    }

    /// <summary>
    /// 根目錄只接受頂層 Solution。零個直接失敗；多個時必須由專案設定明確指定，
    /// 不搜尋子目錄，也不對 DisposeProject.sln 建立特殊規則。
    /// </summary>
    internal static string ResolveSolutionPath(
        string projectRoot,
        string? selectedSolutionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"專案根目錄不存在：{root}");
        var solutions = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (solutions.Length == 0)
            throw new FileNotFoundException($"專案根目錄沒有 .sln：{root}");
        if (solutions.Length == 1)
            return solutions[0];
        if (string.IsNullOrWhiteSpace(selectedSolutionPath))
            throw new InvalidOperationException(
                $"專案根目錄有多個 Solution，請先指定其中一個：{string.Join("、", solutions.Select(Path.GetFileName))}");
        var selected = Path.GetFullPath(selectedSolutionPath);
        if (!solutions.Contains(selected, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("指定的 Solution 必須位於專案根目錄，且存在於目前清單中。");
        return selected;
    }

    private static GraphDocument ToGraphDocument(
        ParallelExtractionResult raw,
        string sourceRoot,
        string provider)
    {
        GraphSchema.EnsureCompleteMappings();
        var nodeKinds = Enum.GetValues<GraphNodeKind>()
            .ToDictionary(GraphSchema.GetNodeLabel, kind => kind, StringComparer.Ordinal);
        var relationshipKinds = Enum.GetValues<GraphRelationshipKind>()
            .ToDictionary(GraphSchema.GetRelationshipType, kind => kind, StringComparer.Ordinal);
        var builder = new GraphDocumentBuilder(new GraphRunMetadata(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            sourceRoot,
            raw.DatabaseName,
            SourceCommit: null,
            DatabaseSnapshotIdentity: null,
            Provider: provider));
        foreach (var node in raw.Nodes)
        {
            if (!nodeKinds.TryGetValue(node.Label, out var kind))
                throw new InvalidOperationException(
                    $"ParallelExtractor 節點 label 尚未建立儲存映射：{node.Label}");
            builder.AddNode(kind, node.Id, node.Properties);
        }
        foreach (var edge in raw.Relationships)
        {
            if (!relationshipKinds.TryGetValue(edge.Type, out var kind))
                throw new InvalidOperationException(
                    $"ParallelExtractor 關係 type 尚未建立儲存映射：{edge.Type}");
            builder.AddRelationship(kind, edge.SourceId, edge.TargetId, edge.Properties);
        }
        return builder.Build();
    }
}
