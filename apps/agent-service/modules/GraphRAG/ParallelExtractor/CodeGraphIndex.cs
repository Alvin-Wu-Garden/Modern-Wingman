namespace AgentService.Modules.GraphRAG.ParallelExtractor;

/// <summary>
/// 原始 ParallelExtractor 在後端寫入 Neo4j 後回讀的程式碼索引。
/// Modern Wingman 直接由同一輪記憶體圖建立，欄位與查找語意保持一致。
/// </summary>
internal sealed class CodeGraphIndex
{
    public Dictionary<string, string> FileIdsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(string FileId, int StartLine), string> MethodIdsByLocation { get; } = new();
    public Dictionary<string, string> ProjectIdsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProjectPathsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ProjectIndexEntry> Projects { get; } = [];
    public Dictionary<string, List<TypeIndexEntry>> TypesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TypeIndexEntry> TypesByFullName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<TypeIndexEntry>> TypesByFileId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(string TypeId, string MethodName), List<MethodIndexEntry>> MethodsByTypeAndName { get; } = new();
    public Dictionary<(string TypeName, string MethodName), List<MethodIndexEntry>> RiskMasterWebMethods { get; } = new();

    public string? FindProjectForPath(string path)
    {
        var normalized = StableId.NormalizePath(path);
        var candidate = Projects
            .Select(project => new
            {
                Project = project,
                Root = StableId.NormalizePath(Path.GetDirectoryName(project.Path) ?? project.Path),
            })
            .Where(item => normalized.Equals(item.Root, StringComparison.OrdinalIgnoreCase) ||
                           normalized.StartsWith(item.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                           normalized.StartsWith(item.Root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Root.Length)
            .FirstOrDefault();
        return candidate?.Project.Id;
    }
}

internal sealed record ProjectIndexEntry(string Id, string Name, string Path);
internal sealed record TypeIndexEntry(string Id, string Name, string FullName, string ProjectId);
internal sealed record MethodIndexEntry(string Id, string Name, string FullName, string FileId, int StartLine);
