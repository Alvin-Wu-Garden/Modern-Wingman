using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.CodeAnalysis;

/// <summary>Extracts Maven/Gradle modules and dependencies without executing build scripts.</summary>
internal static partial class JavaBuildGraphExtractor
{
    private const string ExtractorId = "java-build-model";
    private const string Version = "1.0.0";

    public static CodeAnalysisResult Extract(string root, IReadOnlyCollection<string> sourceFiles)
    {
        var result = new CodeAnalysisResult();
        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edges = new HashSet<(string, string, CodeEdgeKind)>();
        void AddNode(CodeNode node) { if (nodes.Add(node.Key)) result.Nodes.Add(node); }
        void AddEdge(
            string source,
            string target,
            CodeEdgeKind kind,
            string? reason = null,
            string? artifactPath = null)
        {
            if (source == target || !edges.Add((source, target, kind))) return;
            result.Edges.Add(new CodeEdge
            {
                SourceKey = source, TargetKey = target, Kind = kind, SourceKind = GraphSourceKind.Ast,
                Confidence = GraphConfidence.Exact, ExtractorId = ExtractorId, ExtractorVersion = Version, Reason = reason,
                ArtifactPath = artifactPath,
            });
        }

        foreach (var pom in Enumerate(root, "pom.xml")) ExtractMaven(root, pom, sourceFiles, AddNode, AddEdge);
        foreach (var settings in Enumerate(root, "settings.gradle").Concat(Enumerate(root, "settings.gradle.kts")))
            ExtractGradle(root, settings, sourceFiles, AddNode, AddEdge);
        return result;
    }

    private static void ExtractMaven(string root, string pom, IReadOnlyCollection<string> files,
        Action<CodeNode> addNode, Action<string, string, CodeEdgeKind, string?, string?> addEdge)
    {
        try
        {
            var doc = XDocument.Load(pom, LoadOptions.None);
            string? Value(XElement owner, string name) => owner.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            var project = doc.Root;
            if (project is null) return;
            var artifact = Value(project, "artifactId") ??
                Path.GetFileName(Path.GetDirectoryName(pom)) ?? "unknown";
            var parent = project.Elements().FirstOrDefault(e => e.Name.LocalName == "parent");
            var group = Value(project, "groupId") ??
                (parent is null ? null : Value(parent, "groupId"));
            var moduleKey = $"maven:{group ?? "unknown"}:{artifact}";
            var relativePom = Relative(root, pom);
            addNode(BuildNode(moduleKey, CodeNodeKind.Module, artifact, relativePom, "maven", $"groupId={group ?? "unresolved"}"));

            foreach (var dependency in project.Descendants().Where(e => e.Name.LocalName == "dependency"))
            {
                var depGroup = Value(dependency, "groupId") ?? "unknown";
                var depArtifact = Value(dependency, "artifactId");
                if (string.IsNullOrWhiteSpace(depArtifact)) continue;
                var depVersion = Value(dependency, "version");
                var key = $"maven-dependency:{depGroup}:{depArtifact}:{depVersion ?? "managed"}";
                addNode(BuildNode(key, CodeNodeKind.Dependency, depArtifact, relativePom, "maven",
                    $"{depGroup}:{depArtifact}:{depVersion ?? "managed"}",
                    depVersion is null ? GraphConfidence.Resolved : GraphConfidence.Exact));
                addEdge(moduleKey, key, CodeEdgeKind.DependsOnPackage,
                    "Declared Maven dependency", relativePom);
            }

            LinkSources(root, Path.GetDirectoryName(pom)!, moduleKey, relativePom, files, addEdge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new InvalidDataException($"Maven descriptor could not be parsed: {pom}", ex);
        }
    }

    private static void ExtractGradle(string root, string settings, IReadOnlyCollection<string> files,
        Action<CodeNode> addNode, Action<string, string, CodeEdgeKind, string?, string?> addEdge)
    {
        string settingsText;
        try { settingsText = File.ReadAllText(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Gradle settings could not be read: {settings}", ex);
        }
        var buildRoot = Path.GetDirectoryName(settings)!;
        var modules = GradleIncludeRegex().Matches(settingsText).SelectMany(match =>
                GradleModuleRegex().Matches(match.Groups[1].Value).Select(item => item.Groups[1].Value))
            .Select(name => name.TrimStart(':').Replace(':', Path.DirectorySeparatorChar)).Distinct(StringComparer.Ordinal).ToList();
        if (modules.Count == 0) modules.Add(string.Empty);

        foreach (var module in modules)
        {
            var directory = Path.Combine(buildRoot, module);
            var displayName = string.IsNullOrWhiteSpace(module) ? Path.GetFileName(buildRoot) : module.Replace(Path.DirectorySeparatorChar, ':');
            var moduleKey = $"gradle:{Relative(root, directory)}";
            addNode(BuildNode(moduleKey, CodeNodeKind.Module, displayName, Relative(root, settings), "gradle"));
            LinkSources(root, directory, moduleKey, Relative(root, settings), files, addEdge);

            var buildFile = new[] { Path.Combine(directory, "build.gradle"), Path.Combine(directory, "build.gradle.kts") }
                .FirstOrDefault(File.Exists);
            if (buildFile is null) continue;
            string buildText;
            try { buildText = File.ReadAllText(buildFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"Gradle build descriptor could not be read: {buildFile}", ex);
            }
            foreach (Match match in GradleDependencyRegex().Matches(buildText))
            {
                var coordinate = match.Groups[1].Value;
                var key = $"gradle-dependency:{coordinate}";
                addNode(BuildNode(key, CodeNodeKind.Dependency, coordinate.Split(':').ElementAtOrDefault(1) ?? coordinate,
                    Relative(root, buildFile), "gradle", coordinate));
                addEdge(moduleKey, key, CodeEdgeKind.DependsOnPackage,
                    "Declared Gradle dependency", Relative(root, buildFile));
            }
        }
    }

    private static void LinkSources(
        string root,
        string moduleDirectory,
        string moduleKey,
        string descriptorPath,
        IEnumerable<string> files,
        Action<string, string, CodeEdgeKind, string?, string?> addEdge)
    {
        var prefix = Path.GetFullPath(moduleDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in files.Where(file => Path.GetFullPath(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var relative = Relative(root, file);
            var sourceSet = relative.Replace('\\', '/').Contains("/src/test/", StringComparison.OrdinalIgnoreCase)
                ? "test" : "main";
            addEdge(moduleKey, $"file:{relative}", CodeEdgeKind.Contains,
                $"Java {sourceSet} source set", descriptorPath);
        }
    }

    private static CodeNode BuildNode(string key, CodeNodeKind kind, string name, string filePath, string technology,
        string? reason = null, GraphConfidence confidence = GraphConfidence.Exact) => new()
    {
        Key = key, Kind = kind, Name = name, Signature = reason, FilePath = filePath, Language = "java",
        Technology = technology, SourceKind = GraphSourceKind.Ast, Confidence = confidence,
        ExtractorId = ExtractorId, ExtractorVersion = Version, Reason = reason,
    };

    private static IEnumerable<string> Enumerate(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is ".git" or "build" or "target" or "node_modules")).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    [GeneratedRegex(@"(?m)^\s*include\s*(?:\(|\s)([^\r\n\)]*)", RegexOptions.CultureInvariant)]
    private static partial Regex GradleIncludeRegex();
    [GeneratedRegex("""['"](:?[^'"]+)['"]""", RegexOptions.CultureInvariant)]
    private static partial Regex GradleModuleRegex();
    [GeneratedRegex("""(?m)^\s*(?:api|implementation|compileOnly|runtimeOnly|testImplementation|testRuntimeOnly)\s*(?:\(|\s)\s*['"]([^'"]+)['"]""", RegexOptions.CultureInvariant)]
    private static partial Regex GradleDependencyRegex();
}
