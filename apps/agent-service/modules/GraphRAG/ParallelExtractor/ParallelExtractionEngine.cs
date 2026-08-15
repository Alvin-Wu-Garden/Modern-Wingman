using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Build.Locator;

namespace AgentService.Modules.GraphRAG.ParallelExtractor;

/// <summary>不依賴 AgentService 或 Neo4j 的原始節點。</summary>
public sealed record ParallelRawNode(
    string Label,
    string Id,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>不依賴 AgentService 或 Neo4j 的原始關係。</summary>
public sealed record ParallelRawRelationship(
    string Type,
    string SourceId,
    string TargetId,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>一次三階段抽取的完整原始輸出。</summary>
public sealed record ParallelExtractionResult(
    IReadOnlyList<ParallelRawNode> Nodes,
    IReadOnlyList<ParallelRawRelationship> Relationships,
    int BackendNodeCount,
    int BackendRelationshipCount,
    int FrontendNodeCount,
    int FrontendRelationshipCount,
    int DatabaseNodeCount,
    int DatabaseRelationshipCount,
    IReadOnlyDictionary<string, int> NodeCounts,
    IReadOnlyDictionary<string, int> RelationshipCounts,
    IReadOnlyList<string> Diagnostics,
    string DatabaseName);

/// <summary>
/// 可獨立使用的 ParallelExtractor 引擎。執行順序、合併識別與原始專案一致：
/// Backend → Frontend → SQL Server；不讀寫 Neo4j，也不建立檔案監看器。
/// </summary>
public sealed class ParallelExtractionEngine
{
    private static readonly object MsBuildRegistrationGate = new();

    /// <summary>執行完整抽取並回傳未加入 Wingman envelope 的原始圖。</summary>
    public async Task<ParallelExtractionResult> ExtractAsync(
        string solutionPath,
        string? sqlServerConnectionString,
        bool includeCodeChunkText,
        int maxDegreeOfParallelism = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        solutionPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(solutionPath) ||
            !Path.GetExtension(solutionPath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("指定的 Solution 不存在。", solutionPath);
        maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
        RegisterMsBuild();

        CodeGraphData? combined = null;
        var backendFragments = new ConcurrentDictionary<string, CodeGraphData>(StringComparer.Ordinal);
        var backend = new BackendGraphExtractor(includeCodeChunkText);
        var backendResult = await backend.ExtractAsync(
            solutionPath,
            fragment =>
            {
                if (fragment.Nodes.Any(node => node.Label.Equals("Solution", StringComparison.Ordinal)))
                {
                    combined = fragment;
                    return Task.CompletedTask;
                }

                var projectId = fragment.Relationships
                    .FirstOrDefault(edge => edge.Type.Equals("CONTAINS_FILE", StringComparison.Ordinal))
                    ?.StartId;
                projectId ??= fragment.Nodes
                    .Select(node => node.Properties.TryGetValue("projectId", out var value)
                        ? value?.ToString()
                        : null)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrWhiteSpace(projectId))
                    backendFragments[projectId] = fragment;
                return Task.CompletedTask;
            },
            maxDegreeOfParallelism,
            cancellationToken);
        if (combined is null)
            throw new InvalidOperationException("ParallelExtractor 未產生 Solution 圖譜。");
        combined = MergeBackendFragmentsInSolutionOrder(combined, backendFragments);
        var backendNodes = combined.Nodes.Count;
        var backendRelationships = combined.Relationships.Count;

        cancellationToken.ThrowIfCancellationRequested();
        var codeIndex = BuildCodeIndex(combined);
        var sourceRoot = Path.GetDirectoryName(solutionPath)
            ?? throw new InvalidOperationException("無法取得 Solution 所在目錄。");
        var frontend = new FrontendGraphExtractor(sourceRoot, codeIndex).Build(
            parallel: true,
            degree: maxDegreeOfParallelism,
            progress: null,
            cancellationToken);
        // 原始 FrontendGraphImporter 使用 Neo4jGraphWriter（不是 Neo4jIntegration），
        // 因此前端關係仍會保存 locations。只有 SQL 階段使用 Integration writer。
        Merge(combined, frontend.Graph);
        // 原始 Program 在前端寫回 Neo4j 後，DatabaseGraphExtractor 會重新執行
        // LoadCodeIndexAsync。此處從合併後圖重建相同索引，避免 SQL 階段看見
        // 前端執行前的舊快照。
        codeIndex = BuildCodeIndex(combined);

        var databaseNodes = 0;
        var databaseRelationships = 0;
        var databaseName = string.Empty;
        if (!string.IsNullOrWhiteSpace(sqlServerConnectionString))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await DatabaseMetadata.LoadAsync(
                sqlServerConnectionString,
                cancellationToken);
            databaseName = metadata.DatabaseName;
            var databaseGraph = new DatabaseGraphBuilder(
                sourceRoot,
                sqlServerConnectionString,
                metadata,
                codeIndex).Build();
            databaseNodes = databaseGraph.Nodes.Count;
            databaseRelationships = databaseGraph.Relationships.Count;
            Merge(combined, databaseGraph);
        }

        var nodes = combined.Nodes
            .Select(node => new ParallelRawNode(
                node.Label,
                node.Id,
                new Dictionary<string, object?>(node.Properties, StringComparer.Ordinal)))
            .OrderBy(node => node.Label, StringComparer.Ordinal)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        var relationships = combined.Relationships
            .Select(ToRawRelationship)
            .OrderBy(edge => edge.Type, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToArray();
        return new ParallelExtractionResult(
            nodes,
            relationships,
            backendNodes,
            backendRelationships,
            frontend.Graph.Nodes.Count,
            frontend.Graph.Relationships.Count,
            databaseNodes,
            databaseRelationships,
            nodes.GroupBy(node => node.Label, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            relationships.GroupBy(edge => edge.Type, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            backendResult.Summary.Diagnostics.Concat(frontend.Diagnostics).ToArray(),
            databaseName);
    }

    private static ParallelRawRelationship ToRawRelationship(GraphRelationship edge)
    {
        var properties = new Dictionary<string, object?>(edge.Properties, StringComparer.Ordinal)
        {
            ["occurrenceCount"] = edge.OccurrenceCount,
        };
        if (edge.Locations.Count > 0)
            properties["locations"] = edge.Locations.ToArray();
        return new ParallelRawRelationship(
            edge.Type,
            edge.StartId,
            edge.EndId,
            properties);
    }

    /// <summary>使用與原始 Program 相同的 Visual Studio MSBuild 選擇規則。</summary>
    private static void RegisterMsBuild()
    {
        if (MSBuildLocator.IsRegistered) return;
        lock (MsBuildRegistrationGate)
        {
            if (MSBuildLocator.IsRegistered) return;
            const string visualStudioMsBuild =
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin";
            if (Directory.Exists(visualStudioMsBuild))
            {
                Environment.SetEnvironmentVariable(
                    "MSBUILD_EXE_PATH",
                    Path.Combine(visualStudioMsBuild, "MSBuild.exe"));
                Environment.SetEnvironmentVariable(
                    "VSINSTALLDIR",
                    @"C:\Program Files\Microsoft Visual Studio\2022\Community");
                Environment.SetEnvironmentVariable("VSCMD_VER", "17.14.0");
                Environment.SetEnvironmentVariable("VisualStudioVersion", "17.0");
                MSBuildLocator.RegisterMSBuildPath(visualStudioMsBuild);
                return;
            }
            MSBuildLocator.RegisterDefaults();
        }
    }

    /// <summary>重現原始 Neo4jIntegration.LoadCodeIndexAsync 的索引內容。</summary>
    private static CodeGraphIndex BuildCodeIndex(CodeGraphData graph)
    {
        var index = new CodeGraphIndex();
        var nodesById = graph.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var node in graph.Nodes.Where(node => node.Label == "Project"))
        {
            var name = GetString(node, "name");
            var path = GetString(node, "path");
            index.ProjectIdsByName[name] = node.Id;
            index.ProjectPathsByName[name] = path;
            index.Projects.Add(new ProjectIndexEntry(node.Id, name, path));
        }
        foreach (var node in graph.Nodes.Where(node => node.Label == "File"))
            index.FileIdsByPath[GetString(node, "path")] = node.Id;
        foreach (var node in graph.Nodes.Where(node => node.Label == "Method"))
            index.MethodIdsByLocation[(GetString(node, "fileId"), GetInt32(node, "startLine"))] = node.Id;
        foreach (var node in graph.Nodes.Where(node => node.Label == "Type"))
            AddType(index, new TypeIndexEntry(
                node.Id,
                GetString(node, "name"),
                GetString(node, "fullName"),
                GetString(node, "projectId")));

        foreach (var relationship in graph.Relationships.Where(edge => edge.Type == "DECLARES_TYPE"))
        {
            if (!nodesById.TryGetValue(relationship.EndId, out var typeNode) ||
                typeNode.Label != "Type") continue;
            var item = new TypeIndexEntry(
                typeNode.Id,
                GetString(typeNode, "name"),
                GetString(typeNode, "fullName"),
                GetString(typeNode, "projectId"));
            if (!index.TypesByFileId.TryGetValue(relationship.StartId, out var values))
                index.TypesByFileId[relationship.StartId] = values = [];
            if (values.All(existing => existing.Id != item.Id)) values.Add(item);
        }

        foreach (var relationship in graph.Relationships.Where(edge => edge.Type == "DECLARES_METHOD"))
        {
            if (!nodesById.TryGetValue(relationship.StartId, out var typeNode) ||
                typeNode.Label != "Type" ||
                !nodesById.TryGetValue(relationship.EndId, out var methodNode) ||
                methodNode.Label != "Method") continue;
            var method = new MethodIndexEntry(
                methodNode.Id,
                GetString(methodNode, "name"),
                GetString(methodNode, "fullName"),
                GetString(methodNode, "fileId"),
                GetInt32(methodNode, "startLine"));
            var methodKey = (typeNode.Id, method.Name);
            if (!index.MethodsByTypeAndName.TryGetValue(methodKey, out var methods))
                index.MethodsByTypeAndName[methodKey] = methods = [];
            if (methods.All(existing => existing.Id != method.Id)) methods.Add(method);
            if (index.ProjectIdsByName.TryGetValue("RiskMaster_Web", out var webProjectId) &&
                GetString(typeNode, "projectId") == webProjectId)
            {
                var routeKey = (
                    GetString(typeNode, "name").ToLowerInvariant(),
                    method.Name.ToLowerInvariant());
                if (!index.RiskMasterWebMethods.TryGetValue(routeKey, out var routes))
                    index.RiskMasterWebMethods[routeKey] = routes = [];
                if (routes.All(existing => existing.Id != method.Id)) routes.Add(method);
            }
        }
        return index;
    }

    private static void AddType(CodeGraphIndex index, TypeIndexEntry item)
    {
        if (!index.TypesByName.TryGetValue(item.Name, out var byName))
            index.TypesByName[item.Name] = byName = [];
        byName.Add(item);
        index.TypesByFullName[item.FullName] = item;
    }

    private static string GetString(GraphNode node, string property) =>
        node.Properties.TryGetValue(property, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(GraphNode node, string property) =>
        node.Properties.TryGetValue(property, out var value) && value is not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;

    /// <summary>
    /// 抽取仍維持平行，但依 Solution 專案順序重現原始 DOP=1 的 Neo4j 寫入結果，
    /// 避免 worker 完成順序讓跨專案 Method stub 的最後覆寫值每次漂移。
    /// </summary>
    internal static CodeGraphData MergeBackendFragmentsInSolutionOrder(
        CodeGraphData initialGraph,
        ConcurrentDictionary<string, CodeGraphData> fragments)
    {
        var projectOrder = initialGraph.Nodes
            .Where(node => node.Label.Equals("Project", StringComparison.Ordinal))
            .Select(node => node.Id)
            .ToArray();
        foreach (var projectId in projectOrder)
        {
            if (fragments.TryRemove(projectId, out var fragment))
                Merge(initialGraph, fragment);
        }
        if (!fragments.IsEmpty)
        {
            throw new InvalidOperationException(
                $"ParallelExtractor 有 {fragments.Count} 個專案 fragment 無法對應 Solution 專案順序。");
        }
        return initialGraph;
    }

    private static void Merge(CodeGraphData target, CodeGraphData source)
    {
        foreach (var node in source.Nodes) target.AddNode(node.Label, node.Id, node.Properties);
        foreach (var relationship in source.Relationships) target.ApplyNeo4jWrite(relationship);
    }

    private static void Merge(CodeGraphData target, RelationGraph source)
    {
        foreach (var node in source.Nodes) target.AddNode(node.Label, node.Id, node.Properties);
        foreach (var relationship in source.Relationships)
            target.ApplyNeo4jIntegrationWrite(relationship);
    }
}
