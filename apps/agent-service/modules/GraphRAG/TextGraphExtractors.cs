using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 以保守的 Java 語法規則抽取 Spring 入口與 type-level 呼叫。
/// Java extractor 不嘗試重建完整 compiler symbol graph；只有專案內可唯一解析的類別名稱才建立 CALLS，
/// 無法確定的外部 library 或同名型別會被略過，避免產生看似完整但錯誤的關係。
/// </summary>
public sealed partial class JavaGraphExtractor(ILogger<JavaGraphExtractor> logger) : IGraphExtractor
{
    private const int MaximumEvidenceItems = 40;

    /// <inheritdoc />
    public string Id => "java-source-v3";

    /// <inheritdoc />
    public string Version => "3.0.0";

    /// <inheritdoc />
    public async Task<GraphFragment> ExtractAsync(
        string projectRoot,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Java 專案根目錄不存在：{root}");

        var documents = new List<JavaDocument>();
        foreach (var file in files
                     .Where(path => string.Equals(Path.GetExtension(path), ".java", StringComparison.OrdinalIgnoreCase))
                     .Select(Path.GetFullPath)
                     .Where(path => IsInsideRoot(root, path))
                     .Where(path => !IgnoredPath(root, path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            var relativePath = GraphIdentity.NormalizePath(Path.GetRelativePath(root, file));
            var package = PackageRegex().Match(content).Groups["name"].Value;
            foreach (Match match in TypeRegex().Matches(content))
            {
                if (match.Groups["kind"].Value.Equals("enum", StringComparison.OrdinalIgnoreCase))
                    continue;
                var typeName = match.Groups["name"].Value;
                var qualifiedName = string.IsNullOrWhiteSpace(package)
                    ? typeName
                    : $"{package}.{typeName}";
                documents.Add(new JavaDocument(
                    file,
                    relativePath,
                    content,
                    typeName,
                    qualifiedName,
                    GraphIdentity.JavaCode(qualifiedName),
                    LineAt(content, match.Index),
                    DetermineJavaRole(typeName),
                    match.Groups["bases"].Value));
            }
        }

        var fragment = new GraphFragment();
        var uniqueBySimpleName = documents
            .GroupBy(document => document.TypeName, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        foreach (var document in documents.OrderBy(item => item.NodeId, StringComparer.Ordinal))
            fragment.Nodes.Add(CreateJavaNode(document));

        ExtractSpringEntries(fragment, documents);
        ExtractJavaCalls(fragment, documents, uniqueBySimpleName);
        logger.LogInformation(
            "Java GraphRAG V3 抽取完成：{TypeCount} 個型別、{EdgeCount} 條關係。",
            documents.Count, fragment.Edges.Count);
        return fragment;
    }

    private static GraphNode CreateJavaNode(JavaDocument document)
    {
        var methods = JavaMethodRegex().Matches(document.Content)
            .Select(match => $"{match.Groups["return"].Value} {match.Groups["name"].Value}(...)")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaximumEvidenceItems)
            .ToList();
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["methods"] = string.Join(" | ", methods),
            ["baseTypes"] = BoundText(document.BaseClause.Trim(), 500),
        };
        var evidence = new GraphEvidence(
            GraphEvidenceSource.Ast,
            GraphConfidence.Exact,
            document.RelativePath,
            "由 Java package 與型別宣告解析；方法、extends、implements 僅保留在 evidence。",
            document.StartLine,
            document.StartLine,
            details);
        return new GraphNode(
            document.NodeId,
            GraphNodeKind.Code,
            document.Role,
            document.TypeName,
            string.Join(' ', new[] { document.QualifiedName, document.TypeName, document.Role }
                .Concat(methods)),
            "java",
            IsSpringController(document) ? "spring" : "java",
            "active",
            [document.TypeName],
            document.RelativePath,
            document.StartLine,
            document.StartLine,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["qualifiedName"] = document.QualifiedName,
            },
            [evidence]);
    }

    private static void ExtractSpringEntries(GraphFragment fragment, IEnumerable<JavaDocument> documents)
    {
        foreach (var document in documents.Where(IsSpringController)
                     .OrderBy(item => item.NodeId, StringComparer.Ordinal))
        {
            var controllerName = RemoveControllerSuffix(document.TypeName);
            var classPrefix = FirstAnnotationPath(
                document.Content[..Math.Min(document.Content.Length,
                    document.Content.IndexOf(document.TypeName, StringComparison.Ordinal) is var index && index >= 0
                        ? index
                        : document.Content.Length)],
                "RequestMapping");
            foreach (Match match in SpringMethodRegex().Matches(document.Content))
            {
                var annotation = match.Groups["annotation"].Value;
                var methodName = match.Groups["method"].Value;
                var methodPath = FirstQuotedValue(match.Groups["args"].Value);
                var actionName = string.IsNullOrWhiteSpace(methodName) ? "index" : methodName;
                var entryId = GraphIdentity.WebEntry(controllerName, actionName);
                var route = CombineRoute(classPrefix, methodPath, controllerName, actionName);
                var line = LineAt(document.Content, match.Index);
                var details = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["annotation"] = annotation,
                    ["method"] = methodName,
                    ["route"] = route,
                };
                var evidence = new GraphEvidence(
                    GraphEvidenceSource.Framework,
                    GraphConfidence.Resolved,
                    document.RelativePath,
                    "由 Spring Mapping annotation 與方法宣告解析 HTTP 入口。",
                    line,
                    line,
                    details);
                fragment.Nodes.Add(new GraphNode(
                    entryId,
                    GraphNodeKind.EntryPoint,
                    GraphRoles.ControllerAction,
                    $"{controllerName}/{actionName}",
                    $"{controllerName} {actionName} {route}",
                    "java",
                    "spring",
                    "active",
                    [route],
                    document.RelativePath,
                    line,
                    line,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["controller"] = controllerName,
                        ["action"] = actionName,
                    },
                    [evidence]));
                fragment.Edges.Add(CreateEdge(
                    entryId,
                    GraphEdgeKind.Handles,
                    document.NodeId,
                    evidence with
                    {
                        Reason = "由 Spring Controller 方法宣告確認此入口由該 Java 型別處理。",
                    }));
            }
        }
    }

    private static void ExtractJavaCalls(
        GraphFragment fragment,
        IEnumerable<JavaDocument> documents,
        IReadOnlyDictionary<string, JavaDocument> uniqueBySimpleName)
    {
        foreach (var document in documents.OrderBy(item => item.NodeId, StringComparer.Ordinal))
        {
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match field in JavaFieldRegex().Matches(document.Content))
            {
                var typeName = SimpleTypeName(field.Groups["type"].Value);
                if (uniqueBySimpleName.ContainsKey(typeName))
                    variables[field.Groups["name"].Value] = typeName;
            }
            foreach (Match local in JavaNewRegex().Matches(document.Content))
            {
                var typeName = SimpleTypeName(local.Groups["type"].Value);
                if (uniqueBySimpleName.ContainsKey(typeName))
                    variables[local.Groups["name"].Value] = typeName;
            }

            var grouped = new Dictionary<string, List<GraphEvidence>>(StringComparer.Ordinal);
            foreach (Match call in JavaCallRegex().Matches(document.Content))
            {
                if (!variables.TryGetValue(call.Groups["target"].Value, out var typeName) ||
                    !uniqueBySimpleName.TryGetValue(typeName, out var target) ||
                    string.Equals(document.NodeId, target.NodeId, StringComparison.Ordinal))
                    continue;
                if (!grouped.TryGetValue(target.NodeId, out var evidence))
                {
                    evidence = [];
                    grouped.Add(target.NodeId, evidence);
                }
                if (evidence.Count >= MaximumEvidenceItems) continue;
                var line = LineAt(document.Content, call.Index);
                evidence.Add(new GraphEvidence(
                    GraphEvidenceSource.Ast,
                    GraphConfidence.Heuristic,
                    document.RelativePath,
                    "由 Java 變數型別與方法呼叫語法聚合為型別到型別的 CALLS。",
                    line,
                    line,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["variable"] = call.Groups["target"].Value,
                        ["targetMethod"] = call.Groups["method"].Value,
                    }));
            }

            foreach (var pair in grouped.OrderBy(item => item.Key, StringComparer.Ordinal))
                fragment.Edges.Add(new GraphEdge(
                    GraphIdentity.Edge(document.NodeId, GraphEdgeKind.Calls, pair.Key),
                    document.NodeId,
                    GraphEdgeKind.Calls,
                    pair.Key,
                    pair.Value));
        }
    }

    private static bool IsSpringController(JavaDocument document) =>
        document.Role == GraphRoles.Controller ||
        document.Content.Contains("@RestController", StringComparison.Ordinal) ||
        document.Content.Contains("@Controller", StringComparison.Ordinal);

    private static string DetermineJavaRole(string typeName)
    {
        if (typeName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.Controller;
        if (typeName.EndsWith("Repository", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("Dao", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.Repository;
        if (typeName.EndsWith("Service", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.BusinessService;
        if (typeName.EndsWith("Entity", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("Dto", StringComparison.OrdinalIgnoreCase) ||
            typeName.EndsWith("Model", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.DataModel;
        return GraphRoles.Type;
    }

    private static string? FirstAnnotationPath(string content, string annotation)
    {
        var match = Regex.Match(
            content,
            $@"@{Regex.Escape(annotation)}\s*(?:\((?<args>[^)]*)\))?",
            RegexOptions.CultureInvariant);
        return match.Success ? FirstQuotedValue(match.Groups["args"].Value) : null;
    }

    private static string? FirstQuotedValue(string value)
    {
        var match = QuotedValueRegex().Match(value);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string CombineRoute(
        string? prefix,
        string? methodPath,
        string controller,
        string action)
    {
        var segments = new[] { prefix, methodPath }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim('/'))
            .ToList();
        if (segments.Count == 0) return $"/{controller}/{action}";
        return "/" + string.Join('/', segments);
    }

    private static int LineAt(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    private static string SimpleTypeName(string value)
    {
        var token = value.Split('<', '[', '?')[0];
        return token.Split('.').Last();
    }

    private static string RemoveControllerSuffix(string value) =>
        value.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? value[..^"Controller".Length]
            : value;

    private static GraphEdge CreateEdge(
        string source,
        GraphEdgeKind kind,
        string target,
        GraphEvidence evidence) =>
        new(GraphIdentity.Edge(source, kind, target), source, kind, target, [evidence]);

    private static bool IsInsideRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IgnoredPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Split('/').Any(segment =>
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("target", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("build", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("out", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("vendor", StringComparison.OrdinalIgnoreCase));
    }

    private static string BoundText(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

    private sealed record JavaDocument(
        string AbsolutePath,
        string RelativePath,
        string Content,
        string TypeName,
        string QualifiedName,
        string NodeId,
        int StartLine,
        string Role,
        string BaseClause);

    [GeneratedRegex(@"(?m)^\s*package\s+(?<name>[\w.]+)\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex PackageRegex();

    [GeneratedRegex(
        @"(?m)^\s*(?:public\s+|protected\s+|private\s+|abstract\s+|final\s+|static\s+)*" +
        @"(?<kind>class|interface|record|enum)\s+(?<name>[A-Za-z_$][\w$]*)" +
        @"(?<bases>\s+(?:extends|implements)\s+[^{]+)?\s*\{",
        RegexOptions.CultureInvariant)]
    private static partial Regex TypeRegex();

    [GeneratedRegex(
        @"(?m)^\s*(?:public|protected)\s+(?:static\s+)?(?<return>[\w<>,.?\[\]]+)\s+" +
        @"(?<name>[A-Za-z_$][\w$]*)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex JavaMethodRegex();

    [GeneratedRegex(
        @"(?s)@(?<annotation>GetMapping|PostMapping|PutMapping|DeleteMapping|PatchMapping|RequestMapping)" +
        @"\s*(?:\((?<args>[^)]*)\))?\s*" +
        @"(?:public|protected)\s+(?:static\s+)?[\w<>,.?\[\]]+\s+(?<method>[A-Za-z_$][\w$]*)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex SpringMethodRegex();

    [GeneratedRegex(
        @"(?m)^\s*(?:private|protected|public)\s+(?:final\s+)?(?<type>[\w.<>?]+)\s+" +
        @"(?<name>[A-Za-z_$][\w$]*)\s*(?:[;=])",
        RegexOptions.CultureInvariant)]
    private static partial Regex JavaFieldRegex();

    [GeneratedRegex(
        @"(?m)(?<type>[A-Za-z_$][\w$.<>?]*)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*new\s+\k<type>\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex JavaNewRegex();

    [GeneratedRegex(
        @"(?<![\w$])(?<target>[A-Za-z_$][\w$]*)\s*\.\s*(?<method>[A-Za-z_$][\w$]*)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex JavaCallRegex();

    [GeneratedRegex(@"[""'](?<value>[^""']+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValueRegex();
}

/// <summary>
/// 抽取 Ext.js、JavaScript、TypeScript 與 React 檔案中的頁面入口及 HTTP route 關係。
/// 每個檔案最多建立一個 Code node 與一個頁面 EntryPoint，不展開 component tree、store hierarchy 或 npm dependency。
/// </summary>
public sealed partial class FrontendGraphExtractor(ILogger<FrontendGraphExtractor> logger) : IGraphExtractor
{
    private static readonly IReadOnlySet<string> Extensions = new HashSet<string>(
        [".js", ".jsx", ".ts", ".tsx"], StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string Id => "frontend-source-v3";

    /// <inheritdoc />
    public string Version => "3.0.0";

    /// <inheritdoc />
    public async Task<GraphFragment> ExtractAsync(
        string projectRoot,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"前端專案根目錄不存在：{root}");

        var fragment = new GraphFragment();
        foreach (var file in files
                     .Where(path => Extensions.Contains(Path.GetExtension(path)))
                     .Select(Path.GetFullPath)
                     .Where(path => IsInsideRoot(root, path))
                     .Where(path => !IgnoredPath(root, path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GraphIdentity.NormalizePath(Path.GetRelativePath(root, file));
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            var urls = ExtractUrls(content);
            var isPage = IsPage(relativePath, content);
            if (!isPage && urls.Count == 0) continue;

            var codeId = GraphIdentity.FrontendCode(relativePath);
            var evidence = new GraphEvidence(
                GraphEvidenceSource.Ast,
                GraphConfidence.Exact,
                relativePath,
                "以前端檔案作為最小修改單位；不展開 React component 或 Ext.js component hierarchy。",
                1,
                CountLines(content),
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["httpUrls"] = string.Join(" | ", urls.Select(item => item.Url).Distinct()),
                });
            fragment.Nodes.Add(new GraphNode(
                codeId,
                GraphNodeKind.Code,
                GraphRoles.Module,
                Path.GetFileName(relativePath),
                $"{relativePath} {string.Join(' ', urls.Select(item => item.Url))}",
                "frontend",
                DetermineFrontendTechnology(content),
                "active",
                [Path.GetFileNameWithoutExtension(relativePath)],
                relativePath,
                1,
                CountLines(content),
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["relativePath"] = relativePath,
                },
                [evidence]));

            string? pageEntryId = null;
            if (isPage)
            {
                pageEntryId = GraphIdentity.FrontendEntry(relativePath);
                fragment.Nodes.Add(new GraphNode(
                    pageEntryId,
                    GraphNodeKind.EntryPoint,
                    GraphRoles.FrontendPage,
                    Path.GetFileNameWithoutExtension(relativePath),
                    $"{relativePath} {Path.GetFileNameWithoutExtension(relativePath)}",
                    "frontend",
                    DetermineFrontendTechnology(content),
                    "active",
                    [relativePath],
                    relativePath,
                    1,
                    CountLines(content),
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["relativePath"] = relativePath,
                    },
                    [evidence]));
                fragment.Edges.Add(CreateEdge(
                    pageEntryId,
                    GraphEdgeKind.Handles,
                    codeId,
                    evidence with
                    {
                        Reason = "以前端頁面路徑確認此入口由同檔案的程式模組實作。",
                    }));
            }

            foreach (var url in urls)
            {
                if (!TryMapBackendEntry(url.Url, out var controller, out var action)) continue;
                var backendId = GraphIdentity.WebEntry(controller, action);
                var backendEvidence = new GraphEvidence(
                    GraphEvidenceSource.Framework,
                    GraphConfidence.Heuristic,
                    relativePath,
                    "由前端固定 HTTP URL 解析後端 Controller／Action；待 C# 或 Java extractor 解析時合併確認。",
                    url.Line,
                    url.Line,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["url"] = url.Url,
                        ["caller"] = url.Caller,
                    });
                fragment.Nodes.Add(new GraphNode(
                    backendId,
                    GraphNodeKind.EntryPoint,
                    GraphRoles.ControllerAction,
                    $"{controller}/{action}",
                    $"{controller} {action} {url.Url}",
                    "business",
                    "http",
                    "unresolved",
                    [url.Url],
                    null,
                    null,
                    null,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["controller"] = controller,
                        ["action"] = action,
                    },
                    [backendEvidence]));
                fragment.Edges.Add(CreateEdge(
                    pageEntryId ?? codeId,
                    GraphEdgeKind.RoutesTo,
                    backendId,
                    backendEvidence with
                    {
                        Reason = "由前端固定 URL 確認頁面或模組會導向此後端 HTTP 入口。",
                    }));
            }
        }

        logger.LogInformation(
            "Frontend GraphRAG V3 抽取完成：{NodeCount} 個節點、{EdgeCount} 條關係。",
            fragment.Nodes.Count, fragment.Edges.Count);
        return fragment;
    }

    private static IReadOnlyList<FrontendUrl> ExtractUrls(string content)
    {
        var matches = new List<FrontendUrl>();
        AddMatches(matches, content, UrlPropertyRegex(), "url-property");
        AddMatches(matches, content, RootPrefixedUrlPropertyRegex(), "root-prefixed-url");
        AddMatches(matches, content, FetchRegex(), "fetch");
        AddMatches(matches, content, AxiosRegex(), "axios");
        AddMatches(matches, content, WrapperRegex(), "wrapper");
        return matches
            .Where(item => item.Url.StartsWith("/", StringComparison.Ordinal) ||
                           item.Url.StartsWith("~/", StringComparison.Ordinal))
            .DistinctBy(item => (item.Url, item.Line, item.Caller))
            .OrderBy(item => item.Line)
            .ThenBy(item => item.Url, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddMatches(
        ICollection<FrontendUrl> target,
        string content,
        Regex regex,
        string caller)
    {
        foreach (Match match in regex.Matches(content))
        {
            var url = NormalizeUrl(match.Groups["url"].Value);
            if (url.Length == 0 || url.Contains("${", StringComparison.Ordinal)) continue;
            target.Add(new FrontendUrl(url, LineAt(content, match.Index), caller));
        }
    }

    private static bool TryMapBackendEntry(
        string url,
        out string controller,
        out string action)
    {
        controller = string.Empty;
        action = string.Empty;
        var path = url.Split('?', '#')[0].Trim('~', '/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;

        // "/api/Controller/Action" 與 "/Controller/Action" 都很常見；
        // 只在至少有兩個固定 segment 時建立 heuristic endpoint，動態 template 則留在 evidence。
        var offset = segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                     segments.Length >= 3
            ? 1
            : 0;
        controller = segments[offset];
        action = segments[offset + 1];
        return controller.All(IsSafeRouteCharacter) && action.All(IsSafeRouteCharacter);
    }

    private static bool IsSafeRouteCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '-';

    private static string NormalizeUrl(string value)
    {
        var url = value.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            url = absolute.PathAndQuery;
        return url.Replace('\\', '/');
    }

    private static bool IsPage(string relativePath, string content)
    {
        var segments = relativePath.Split('/');
        return segments.Any(segment =>
                   segment.Equals("views", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("pages", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("screens", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("controllers", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("formcollection", StringComparison.OrdinalIgnoreCase)) ||
               content.Contains("Ext.define(", StringComparison.Ordinal) ||
               content.Contains("createBrowserRouter(", StringComparison.Ordinal) ||
               content.Contains("<Route", StringComparison.Ordinal);
    }

    private static string DetermineFrontendTechnology(string content)
    {
        if (content.Contains("Ext.", StringComparison.Ordinal)) return "extjs";
        if (content.Contains("from 'react'", StringComparison.Ordinal) ||
            content.Contains("from \"react\"", StringComparison.Ordinal) ||
            content.Contains("<Route", StringComparison.Ordinal))
            return "react";
        return "javascript";
    }

    private static bool IgnoredPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var fileName = Path.GetFileName(relative);
        return fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase) ||
               relative.Split('/').Any(segment =>
                   segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("vendor", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("out", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInsideRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static int LineAt(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    private static int CountLines(string content) =>
        content.Length == 0 ? 1 : content.Count(character => character == '\n') + 1;

    private static GraphEdge CreateEdge(
        string source,
        GraphEdgeKind kind,
        string target,
        GraphEvidence evidence) =>
        new(GraphIdentity.Edge(source, kind, target), source, kind, target, [evidence]);

    private sealed record FrontendUrl(string Url, int Line, string Caller);

    [GeneratedRegex(
        @"(?i)\burl\s*:\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlPropertyRegex();

    // FBL 的 Ext Store 與 RM.Ext.AjaxRequest 多半把站台根路徑寫成
    // RMSystemData.UrlRoot + "/Controller/Action"。只接受固定字串尾段；
    // String.format、變數或 template literal 不建立 Exact/Heuristic route，避免虛構目標。
    [GeneratedRegex(
        @"(?i)\burl\s*:\s*(?:RMSystemData\.UrlRoot|UrlRoot|window\.location\.origin)\s*\+\s*[""'](?<url>/[^""']+)[""']",
        RegexOptions.CultureInvariant)]
    private static partial Regex RootPrefixedUrlPropertyRegex();

    [GeneratedRegex(
        @"\bfetch\s*\(\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.CultureInvariant)]
    private static partial Regex FetchRegex();

    [GeneratedRegex(
        @"(?i)\baxios\s*\.\s*(?:get|post|put|delete|patch)\s*\(\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.CultureInvariant)]
    private static partial Regex AxiosRegex();

    [GeneratedRegex(
        @"(?i)\b(?:RMCommonLib\.)?(?:Fetch|Request|Ajax)\s*\(\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.CultureInvariant)]
    private static partial Regex WrapperRegex();
}
