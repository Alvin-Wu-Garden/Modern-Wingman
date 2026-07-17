using System.Xml.Linq;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.CodeAnalysis;

/// <summary>Reads deterministic build metadata without requiring restore or an installed MSBuild host.</summary>
internal static class CSharpProjectGraphExtractor
{
    private const string ExtractorId = "dotnet-project-model";
    private const string ExtractorVersion = "1.0.0";

    public static CodeAnalysisResult Extract(string projectRoot, IReadOnlyCollection<string> sourceFiles)
    {
        var result = new CodeAnalysisResult();
        var seenNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEdges = new HashSet<(string, string, CodeEdgeKind)>();
        void AddNode(CodeNode node) { if (seenNodes.Add(node.Key)) result.Nodes.Add(node); }
        void AddEdge(string source, string target, CodeEdgeKind kind, string? reason = null)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || source == target ||
                !seenEdges.Add((source, target, kind))) return;
            result.Edges.Add(new CodeEdge
            {
                SourceKey = source, TargetKey = target, Kind = kind,
                SourceKind = GraphSourceKind.Ast, Confidence = GraphConfidence.Exact,
                ExtractorId = ExtractorId, ExtractorVersion = ExtractorVersion, Reason = reason,
            });
        }

        var projects = Enumerate(projectRoot, "*.csproj").Select(path => ReadProject(projectRoot, path)).ToList();
        var projectByPath = projects.ToDictionary(project => project.AbsolutePath, StringComparer.OrdinalIgnoreCase);

        foreach (var solutionPath in Enumerate(projectRoot, "*.sln").Concat(Enumerate(projectRoot, "*.slnx")))
        {
            var relative = Relative(projectRoot, solutionPath);
            var key = $"solution:{relative}";
            AddNode(BuildNode(key, CodeNodeKind.Solution, Path.GetFileNameWithoutExtension(solutionPath), relative,
                Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase) ? "slnx" : "sln"));
            foreach (var project in projects.Where(project => SolutionContains(solutionPath, project.AbsolutePath)))
                AddEdge(key, project.Key, CodeEdgeKind.Contains, "Project path declared by solution");
        }

        foreach (var project in projects)
        {
            AddNode(BuildNode(project.Key, CodeNodeKind.Project, project.Name, project.RelativePath, "msbuild",
                project.TargetFrameworks.Count == 0 ? "Target framework could not be determined" :
                $"TargetFramework={string.Join(';', project.TargetFrameworks)}"));
            var assemblyKey = $"assembly:{project.Name}";
            AddNode(BuildNode(assemblyKey, CodeNodeKind.Assembly, project.Name, project.RelativePath, "dotnet"));
            AddEdge(project.Key, assemblyKey, CodeEdgeKind.Contains);

            foreach (var reference in project.ProjectReferences)
            {
                var absolute = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project.AbsolutePath)!, reference));
                var target = projectByPath.TryGetValue(absolute, out var referenced)
                    ? referenced.Key
                    : $"csproj:{Relative(projectRoot, absolute)}";
                if (!projectByPath.ContainsKey(absolute))
                    AddNode(BuildNode(target, CodeNodeKind.Project, Path.GetFileNameWithoutExtension(absolute),
                        Relative(projectRoot, absolute), "msbuild", "Referenced project was not available in the indexed workspace",
                        GraphConfidence.Resolved));
                AddEdge(project.Key, target, CodeEdgeKind.ProjectReferences);
            }

            foreach (var package in project.Packages)
            {
                var packageKey = $"nuget:{package.Id.ToLowerInvariant()}:{package.Version ?? "unspecified"}";
                AddNode(BuildNode(packageKey, CodeNodeKind.Package, package.Id, project.RelativePath, "nuget",
                    package.Version is null ? "Package version is inherited or unresolved" : $"Version={package.Version}",
                    package.Version is null ? GraphConfidence.Resolved : GraphConfidence.Exact));
                AddEdge(project.Key, packageKey, CodeEdgeKind.DependsOnPackage);
            }

            var directory = Path.GetDirectoryName(project.AbsolutePath)!;
            foreach (var source in sourceFiles.Where(file => IsWithin(file, directory) &&
                         !projects.Any(other => other != project && IsWithin(file, Path.GetDirectoryName(other.AbsolutePath)!) &&
                                                Path.GetDirectoryName(other.AbsolutePath)!.Length > directory.Length)))
                AddEdge(project.Key, $"file:{Relative(projectRoot, source)}", CodeEdgeKind.Contains);
        }

        return result;
    }

    private static ProjectDescriptor ReadProject(string root, string path)
    {
        try
        {
            var document = XDocument.Load(path, LoadOptions.None);
            var properties = document.Descendants().Where(element =>
                    element.Name.LocalName is "TargetFramework" or "TargetFrameworks" or "AssemblyName")
                .GroupBy(element => element.Name.LocalName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value.Trim(), StringComparer.Ordinal);
            var frameworks = (properties.GetValueOrDefault("TargetFrameworks") ??
                              properties.GetValueOrDefault("TargetFramework") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var name = properties.GetValueOrDefault("AssemblyName") ?? Path.GetFileNameWithoutExtension(path);
            var references = document.Descendants().Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value).OfType<string>().ToList();
            var packages = document.Descendants().Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => new PackageDescriptor(
                    element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value ?? string.Empty,
                    element.Attribute("Version")?.Value ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value))
                .Where(package => !string.IsNullOrWhiteSpace(package.Id)).ToList();
            return new ProjectDescriptor(path, Relative(root, path), $"csproj:{Relative(root, path)}", name,
                frameworks, references, packages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new ProjectDescriptor(path, Relative(root, path), $"csproj:{Relative(root, path)}",
                Path.GetFileNameWithoutExtension(path), [], [], []);
        }
    }

    private static CodeNode BuildNode(string key, CodeNodeKind kind, string name, string path, string technology,
        string? reason = null, GraphConfidence confidence = GraphConfidence.Exact) => new()
    {
        Key = key, Kind = kind, Name = name, Signature = reason, FilePath = path, Language = "csharp",
        Technology = technology, SourceKind = GraphSourceKind.Ast, Confidence = confidence,
        ExtractorId = ExtractorId, ExtractorVersion = ExtractorVersion, Reason = reason,
    };

    private static IEnumerable<string> Enumerate(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(path => !IsIgnored(path)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsIgnored(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(segment => segment is ".git" or "bin" or "obj" or "node_modules");

    private static bool IsWithin(string path, string directory)
    {
        var candidate = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SolutionContains(string solutionPath, string projectPath)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var relative = Path.GetRelativePath(solutionDirectory, projectPath).Replace('\\', '/');
        try
        {
            var text = File.ReadAllText(solutionPath).Replace('\\', '/');
            return text.Contains(relative, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private sealed record PackageDescriptor(string Id, string? Version);
    private sealed record ProjectDescriptor(
        string AbsolutePath,
        string RelativePath,
        string Key,
        string Name,
        IReadOnlyList<string> TargetFrameworks,
        IReadOnlyList<string> ProjectReferences,
        IReadOnlyList<PackageDescriptor> Packages);
}
