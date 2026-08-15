namespace AgentService.Modules.GraphRAG.ParallelExtractor;

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Esprima;
using Esprima.Ast;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using JsNode = Esprima.Ast.Node;
using TsLanguage = TreeSitter.Language;
using TsParser = TreeSitter.Parser;
using TsNode = TreeSitter.Node;

/// <summary>定義「FrontendFileInfo」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed record FrontendFileInfo(
    string Path,
    string RelativePath,
    string Extension,
    bool IsBundle);

/// <summary>定義「FrontendGraphBuildResult」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed record FrontendGraphBuildResult(
    CodeGraphData Graph,
    int ScannedFrontendFileCount,
    int PageCount,
    int ScriptAssetCount,
    int BundleCount,
    int ComponentCount,
    int FunctionCount,
    int ApiEndpointCount,
    int BackendViewRelationshipCount,
    int ParseWarningCount,
    IReadOnlyList<string> Diagnostics);

/// <summary>定義「FrontendFileCatalog」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class FrontendFileCatalog
{
    /// <summary>執行「FrontendFileCatalog」所代表的圖譜抽取或匯入工作。</summary>
    private FrontendFileCatalog(
        string sourceRoot,
        IReadOnlyList<FrontendFileInfo> frontendFiles,
        IReadOnlyList<string> csharpFiles,
        IReadOnlyList<string> buildConfigurationFiles)
    {
        SourceRoot = sourceRoot;
        FrontendFiles = frontendFiles;
        CSharpFiles = csharpFiles;
        BuildConfigurationFiles = buildConfigurationFiles;
        Pages = frontendFiles
            .Where(file => file.Extension.Equals(".aspx", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public string SourceRoot { get; }
    public IReadOnlyList<FrontendFileInfo> FrontendFiles { get; }
    public IReadOnlyList<FrontendFileInfo> Pages { get; }
    public IReadOnlyList<string> CSharpFiles { get; }
    public IReadOnlyList<string> BuildConfigurationFiles { get; }

    /// <summary>執行「Create」所代表的圖譜抽取或匯入工作。</summary>
    public static FrontendFileCatalog Create(string sourceRoot)
    {
        var frontendFiles = new List<FrontendFileInfo>();
        var csharpFiles = new List<string>();
        var buildFiles = new List<string>();

        foreach (var file in EnumerateFiles(sourceRoot))
        {
            var extension = Path.GetExtension(file);
            var name = Path.GetFileName(file);
            var relativePath = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            var lowerName = name.ToLowerInvariant();
            var lowerRelative = relativePath.ToLowerInvariant();

            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                csharpFiles.Add(file);
            }

            if (IsBuildConfiguration(lowerName))
            {
                buildFiles.Add(file);
            }

            if (extension.Equals(".aspx", StringComparison.OrdinalIgnoreCase))
            {
                frontendFiles.Add(new FrontendFileInfo(file, relativePath, extension, false));
                continue;
            }

            if (!extension.Equals(".js", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Bundles are inventory-only. The source is intentionally never read.
            var isBundle = lowerName.EndsWith(".bundle.js", StringComparison.Ordinal) ||
                           lowerRelative.Contains("/dist/", StringComparison.Ordinal) ||
                           lowerName.EndsWith(".min.js", StringComparison.Ordinal);
            frontendFiles.Add(new FrontendFileInfo(file, relativePath, extension, isBundle));
        }

        return new FrontendFileCatalog(
            sourceRoot,
            frontendFiles.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            csharpFiles.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray(),
            buildFiles.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>取得「FindByReference」所代表的圖譜抽取或匯入工作。</summary>
    public FrontendFileInfo? FindByReference(string value)
    {
        var relative = NormalizeReference(value);
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var exact = FrontendFiles.FirstOrDefault(file =>
            file.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return FrontendFiles
            .Where(file => file.RelativePath.EndsWith("/" + relative, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.RelativePath.Length)
            .FirstOrDefault();
    }

    /// <summary>取得「ResolveView」所代表的圖譜抽取或匯入工作。</summary>
    public FrontendFileInfo? ResolveView(string controllerName, string viewName)
    {
        var clean = NormalizeReference(viewName).TrimStart('/');
        clean = clean.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)
            ? clean[..^5]
            : clean;

        if (clean.Contains('/', StringComparison.Ordinal))
        {
            var direct = FindByReference(clean + ".aspx") ?? FindByReference(clean);
            if (direct is not null)
            {
                return direct;
            }
        }

        var candidates = Pages
            .Where(page => Path.GetFileNameWithoutExtension(page.Path)
                .Equals(Path.GetFileName(clean), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var controllerMatch = candidates.FirstOrDefault(page =>
            page.RelativePath.Contains("/" + controllerName + "/", StringComparison.OrdinalIgnoreCase) ||
            page.RelativePath.Contains("/" + controllerName + "Views/", StringComparison.OrdinalIgnoreCase));
        return controllerMatch ?? candidates.FirstOrDefault();
    }

    /// <summary>判斷「IsThirdPartySource」所代表的圖譜抽取或匯入工作。</summary>
    public static bool IsThirdPartySource(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("/node_modules/") || lower.Contains("/packages/") ||
            lower.Contains("/ext-3.3.0/") || lower.Contains("/ext/") ||
            lower.Contains("/extux/") || lower.Contains("/fusioncharts/"))
        {
            return true;
        }

        var name = Path.GetFileName(path).ToLowerInvariant();
        return name.StartsWith("jquery", StringComparison.Ordinal) ||
               name.StartsWith("jquery-ui", StringComparison.Ordinal) ||
               name.StartsWith("microsoftajax", StringComparison.Ordinal) ||
               name.StartsWith("microsoftmvcajax", StringComparison.Ordinal) ||
               name.StartsWith("knockout", StringComparison.Ordinal) ||
               name.StartsWith("signalr", StringComparison.Ordinal) ||
               name.StartsWith("ext-all", StringComparison.Ordinal);
    }

    /// <summary>正規化「NormalizeReference」所代表的圖譜抽取或匯入工作。</summary>
    private static string NormalizeReference(string value)
    {
        var normalized = value.Trim().Replace('\\', '/');
        normalized = normalized.Split('?', '#')[0];
        normalized = normalized.Trim().Trim('"', '\'');
        normalized = Regex.Replace(normalized, @"^https?://[^/]+", string.Empty, RegexOptions.IgnoreCase);
        normalized = normalized.TrimStart('~', '/');
        return normalized.TrimStart('/');
    }

    /// <summary>判斷「IsBuildConfiguration」所代表的圖譜抽取或匯入工作。</summary>
    private static bool IsBuildConfiguration(string fileName)
        => fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("webpack", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("rollup", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("vite", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("gulpfile", StringComparison.OrdinalIgnoreCase) ||
           fileName.StartsWith("gruntfile", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("browserify.js", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("tsconfig.json", StringComparison.OrdinalIgnoreCase) ||
           fileName.Equals("babel.config.js", StringComparison.OrdinalIgnoreCase);

    /// <summary>執行「EnumerateFiles」所代表的圖譜抽取或匯入工作。</summary>
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/packages/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }
}
/// <summary>定義「FrontendGraphExtractor」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class FrontendGraphExtractor
{
    private static readonly Regex ScriptTagRegex = new(
        "<script\\b(?=[^>]*\\bsrc\\s*=)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ScriptPathRegex = new(
        "[\\\"'](?<path>(?:https?://[^\\\"']+|[^\\\"']+\\.(?:js|jsx|ts|tsx)(?:\\?[^\\\"']*)?))[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(
        "^(?:/|~/|https?://)|(?:/|\\\\)(?:api|ajax|service|controller|data)(?:/|\\\\)|/(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _sourceRoot;
    private readonly CodeGraphIndex _codeIndex;
    private readonly FrontendFileCatalog _catalog;
    private readonly List<string> _diagnostics = new();
    private int _parseWarnings;

    /// <summary>執行「FrontendGraphExtractor」所代表的圖譜抽取或匯入工作。</summary>
    public FrontendGraphExtractor(string sourceRoot, CodeGraphIndex codeIndex)
    {
        _sourceRoot = sourceRoot;
        _codeIndex = codeIndex;
        _catalog = FrontendFileCatalog.Create(sourceRoot);
    }

    /// <summary>建立「Build」所代表的圖譜抽取或匯入工作。</summary>
    public FrontendGraphBuildResult Build(
        bool parallel,
        int degree,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CodeGraphData>();
        var files = _catalog.FrontendFiles;

        if (parallel && degree > 1)
        {
            var bag = new ConcurrentBag<(string Path, CodeGraphData Graph)>();
            Parallel.ForEach(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
                file => bag.Add((file.Path, ProcessFrontendFile(file, progress))));

            results.AddRange(bag.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(item => item.Graph));
        }
        else
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(ProcessFrontendFile(file, progress));
            }
        }

        var graph = new CodeGraphData();
        foreach (var result in results)
        {
            Merge(graph, result);
        }

        AddBundleComponentRelations(graph);
        AddBackendViewRelations(graph, cancellationToken);

        return new FrontendGraphBuildResult(
            graph,
            files.Count,
            CountNodes(graph, "WebFormPage"),
            CountNodes(graph, "ScriptAsset"),
            CountNodes(graph, "FrontendBundle"),
            CountNodes(graph, "FrontendComponent"),
            CountNodes(graph, "FrontendFunction"),
            CountNodes(graph, "ApiEndpoint"),
            CountRelationships(graph, "RETURNS_VIEW"),
            _parseWarnings,
            _diagnostics.ToArray());
    }

    /// <summary>處理「ProcessFrontendFile」所代表的圖譜抽取或匯入工作。</summary>
    private CodeGraphData ProcessFrontendFile(FrontendFileInfo file, Action<string>? progress)
    {
        var graph = new CodeGraphData();
        var projectId = _codeIndex.FindProjectForPath(file.Path);
        var assetId = AddAssetNode(graph, file, projectId);

        if (file.Extension.Equals(".aspx", StringComparison.OrdinalIgnoreCase))
        {
            ParsePage(graph, file, assetId, projectId);
            return graph;
        }

        if (file.IsBundle)
        {
            // Explicitly do not read or parse bundle content.
            return graph;
        }

        if (FrontendFileCatalog.IsThirdPartySource(file.Path))
        {
            AddAssetProperty(graph, assetId, "parseStatus", "THIRD_PARTY_NOT_PARSED");
            return graph;
        }

        if (file.Extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var source = File.ReadAllText(file.Path);
                ParseTypeScript(graph, file, assetId, source);
            }
            catch (Exception exception)
            {
                _parseWarnings++;
                AddAssetProperty(graph, assetId, "parseStatus", "PARSE_ERROR");
                AddDiagnostic($"TypeScript/TSX parse failed for {file.Path}: {exception.Message}");
            }
            return graph;
        }

        try
        {
            var source = File.ReadAllText(file.Path);
            ParseJavaScript(graph, file, assetId, source);
        }
        catch (Exception exception)
        {
            _parseWarnings++;
            AddAssetProperty(graph, assetId, "parseStatus", "PARSE_ERROR");
            AddDiagnostic($"Frontend parse failed for {file.Path}: {exception.Message}");
        }

        return graph;
    }

    /// <summary>加入「AddAssetNode」所代表的圖譜抽取或匯入工作。</summary>
    private string AddAssetNode(CodeGraphData graph, FrontendFileInfo file, string? projectId)
    {
        var label = file.Extension.Equals(".aspx", StringComparison.OrdinalIgnoreCase)
            ? "WebFormPage"
            : file.IsBundle ? "FrontendBundle" : "ScriptAsset";
        var parser = label == "WebFormPage"
            ? "ASPX_ScriptTagScanner"
            : file.IsBundle
                ? "INVENTORY_ONLY"
                : file.Extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                  file.Extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase)
                    ? "TreeSitter.DotNet"
                    : "Esprima.NET";
        var id = StableId.For(label.ToLowerInvariant(), StableId.NormalizePath(file.Path));
        var lineCount = SafeLineCount(file.Path);
        graph.AddNode(label, id, new Dictionary<string, object?>
        {
            ["name"] = Path.GetFileName(file.Path),
            ["path"] = StableId.NormalizePath(file.Path),
            ["relativePath"] = file.RelativePath,
            ["extension"] = file.Extension,
            ["projectId"] = projectId,
            ["lineCount"] = lineCount,
            ["contentStored"] = false,
            ["parser"] = parser
        });

        if (projectId is not null)
        {
            graph.AddRelationship("CONTAINS_FRONTEND_ASSET", projectId, id);
        }

        if (_codeIndex.FileIdsByPath.TryGetValue(StableId.NormalizePath(file.Path), out var fileId))
        {
            graph.AddRelationship("REPRESENTS_SOURCE_FILE", id, fileId, new Dictionary<string, object?>
            {
                ["path"] = StableId.NormalizePath(file.Path)
            });
        }

        return id;
    }

    /// <summary>解析「ParsePage」所代表的圖譜抽取或匯入工作。</summary>
    private void ParsePage(CodeGraphData graph, FrontendFileInfo page, string pageId, string? projectId)
    {
        string source;
        try
        {
            source = File.ReadAllText(page.Path);
        }
        catch (Exception exception)
        {
            _parseWarnings++;
            AddDiagnostic($"ASPX read failed for {page.Path}: {exception.Message}");
            return;
        }

        try
        {
            // ASPX embeds server expressions such as
            // src="<% =UrlUtility.JSPath(\"/dist/page.bundle.js\") %>".
            // A normal HTML DOM parser sees the nested quote as the end of the
            // attribute. Scan the complete script tag instead, then extract the
            // literal JS/TS path from the tag.
            foreach (Match scriptTag in ScriptTagRegex.Matches(source))
            {
                var reference = ExtractScriptReference(scriptTag.Value);
                if (string.IsNullOrWhiteSpace(reference))
                {
                    continue;
                }

                var asset = _catalog.FindByReference(reference);
                var assetId = asset is null
                    ? AddUnresolvedAsset(graph, reference, projectId)
                    : AddAssetNode(graph, asset, _codeIndex.FindProjectForPath(asset.Path));
                graph.AddRelationship("INCLUDES_SCRIPT", pageId, assetId, new Dictionary<string, object?>
                {
                    ["sourcePath"] = StableId.NormalizePath(page.Path),
                    ["reference"] = reference,
                    ["resolved"] = asset is not null,
                    ["confidence"] = asset is null ? "URL_ONLY" : "SCRIPT_TAG_LITERAL"
                });
            }
        }
        catch (Exception exception)
        {
            _parseWarnings++;
            AddDiagnostic($"ASPX parse failed for {page.Path}: {exception.Message}");
        }
    }

    /// <summary>解析「ParseJavaScript」所代表的圖譜抽取或匯入工作。</summary>
    private void ParseJavaScript(CodeGraphData graph, FrontendFileInfo file, string assetId, string source)
    {
        var parser = new JsxParser(new JsxParserOptions
        {
            Tolerant = true,
            AllowReturnOutsideFunction = true
        });

        JsNode program;
        try
        {
            program = parser.ParseModule(source, file.Path);
        }
        catch
        {
            program = parser.ParseScript(source, file.Path, true);
        }

        WalkJavaScriptNode(graph, file, assetId, program, null, null, null);
    }

    /// <summary>解析「ParseTypeScript」所代表的圖譜抽取或匯入工作。</summary>
    private void ParseTypeScript(CodeGraphData graph, FrontendFileInfo file, string assetId, string source)
    {
        using var language = new TsLanguage(file.Extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ? "TSX" : "TypeScript");
        using var parser = new TsParser(language);
        using var tree = parser.Parse(source);
        if (tree is null)
        {
            throw new InvalidOperationException("Tree-sitter returned no syntax tree.");
        }

        AddAssetProperty(graph, assetId, "parseStatus", "TREE_SITTER_PARSED");
        WalkTypeScriptNode(graph, file, assetId, tree.RootNode, null, null);
    }

    /// <summary>執行「WalkTypeScriptNode」所代表的圖譜抽取或匯入工作。</summary>
    private void WalkTypeScriptNode(
        CodeGraphData graph,
        FrontendFileInfo file,
        string assetId,
        TsNode node,
        string? componentId,
        string? functionId)
    {
        var currentComponentId = componentId;
        var currentFunctionId = functionId;

        switch (node.Type)
        {
            case "class_declaration":
            case "class":
            {
                var name = GetTypeScriptName(GetTypeScriptField(node, "name"));
                if (!string.IsNullOrWhiteSpace(name) && IsComponentName(name!))
                {
                    currentComponentId = AddComponentAt(graph, file, assetId, name!, "React", "ClassComponent", node.StartPosition.Row + 1, node.EndPosition.Row + 1);
                }
                break;
            }
            case "function_declaration":
            case "function":
            {
                var name = GetTypeScriptName(GetTypeScriptField(node, "name"));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (currentComponentId is null && IsComponentName(name!))
                    {
                        currentComponentId = AddComponentAt(graph, file, assetId, name!, "React", "FunctionComponent", node.StartPosition.Row + 1, node.EndPosition.Row + 1);
                    }

                    currentFunctionId = AddFunctionAt(graph, file, assetId, name!, currentComponentId, node.StartPosition.Row + 1, node.EndPosition.Row + 1);
                }
                break;
            }
            case "method_definition":
            {
                var name = GetTypeScriptName(GetTypeScriptField(node, "name"));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    currentFunctionId = AddFunctionAt(graph, file, assetId, name!, currentComponentId, node.StartPosition.Row + 1, node.EndPosition.Row + 1);
                }
                break;
            }
            case "variable_declarator":
            {
                var name = GetTypeScriptName(GetTypeScriptField(node, "name"));
                var value = GetTypeScriptField(node, "value");
                if (!string.IsNullOrWhiteSpace(name) && value is not null && IsTypeScriptFunction(value))
                {
                    if (currentComponentId is null && IsComponentName(name!))
                    {
                        currentComponentId = AddComponentAt(graph, file, assetId, name!, "React", "FunctionComponent", value.StartPosition.Row + 1, value.EndPosition.Row + 1);
                    }

                    currentFunctionId = AddFunctionAt(graph, file, assetId, name!, currentComponentId, value.StartPosition.Row + 1, value.EndPosition.Row + 1);
                }
                break;
            }
            case "jsx_opening_element":
            case "jsx_self_closing_element":
            {
                var name = GetTypeScriptName(GetTypeScriptField(node, "name"));
                if (!string.IsNullOrWhiteSpace(name) && currentComponentId is not null)
                {
                    AddTypeScriptUiElement(graph, file, currentComponentId, name!, node.StartPosition.Row + 1, node.EndPosition.Row + 1);
                }
                break;
            }
            case "call_expression":
            {
                AddTypeScriptApiCall(graph, file, assetId, currentComponentId, currentFunctionId, node);
                break;
            }
        }

        foreach (var child in node.NamedChildren)
        {
            WalkTypeScriptNode(graph, file, assetId, child, currentComponentId, currentFunctionId);
        }
    }

    /// <summary>建立前端元件節點，保存其檔案位置與行號範圍。</summary>
    private string AddComponentAt(CodeGraphData graph, FrontendFileInfo file, string assetId, string name, string framework, string kind, int startLine, int endLine)
    {
        var id = StableId.For("frontend-component", StableId.NormalizePath(file.Path), name);
        graph.AddNode("FrontendComponent", id, new Dictionary<string, object?>
        {
            ["name"] = name,
            ["fullName"] = name,
            ["framework"] = framework,
            ["kind"] = kind,
            ["filePath"] = StableId.NormalizePath(file.Path),
            ["startLine"] = startLine,
            ["endLine"] = endLine,
            ["contentStored"] = false,
            ["bodyStored"] = false
        });
        graph.AddRelationship("DECLARES_COMPONENT", assetId, id, new Dictionary<string, object?>
        {
            ["framework"] = framework,
            ["sourceLine"] = startLine
        });
        return id;
    }

    /// <summary>建立前端函式節點，只保存函式名稱與開始、結束行號。</summary>
    private string AddFunctionAt(CodeGraphData graph, FrontendFileInfo file, string assetId, string name, string? componentId, int startLine, int endLine)
    {
        var id = StableId.For("frontend-function", StableId.NormalizePath(file.Path), name, startLine);
        graph.AddNode("FrontendFunction", id, new Dictionary<string, object?>
        {
            ["name"] = name,
            ["signature"] = name,
            ["filePath"] = StableId.NormalizePath(file.Path),
            ["startLine"] = startLine,
            ["endLine"] = endLine,
            ["bodyStored"] = false,
            ["contentStored"] = false
        });
        graph.AddRelationship("DECLARES_FUNCTION", assetId, id, new Dictionary<string, object?>
        {
            ["startLine"] = startLine,
            ["endLine"] = endLine
        });
        if (componentId is not null)
        {
            graph.AddRelationship("COMPONENT_HAS_FUNCTION", componentId, id);
        }

        return id;
    }

    /// <summary>建立 TypeScript UI 元素節點並連結至元件。</summary>
    private void AddTypeScriptUiElement(CodeGraphData graph, FrontendFileInfo file, string componentId, string name, int startLine, int endLine)
    {
        var uiId = StableId.For("frontend-ui", StableId.NormalizePath(file.Path), "tsx", name, startLine);
        graph.AddNode("UIElement", uiId, new Dictionary<string, object?>
        {
            ["name"] = name,
            ["kind"] = "JSX_ELEMENT",
            ["filePath"] = StableId.NormalizePath(file.Path),
            ["startLine"] = startLine,
            ["endLine"] = endLine,
            ["contentStored"] = false
        });
        graph.AddRelationship("RENDERS_UI_ELEMENT", componentId, uiId);
    }

    /// <summary>建立 TypeScript API 呼叫節點並記錄其來源位置。</summary>
    private void AddTypeScriptApiCall(CodeGraphData graph, FrontendFileInfo file, string assetId, string? componentId, string? functionId, TsNode node)
    {
        var callee = GetTypeScriptField(node, "function")?.Text;
        if (string.IsNullOrWhiteSpace(callee) || !LooksLikeApiCall(callee!))
        {
            return;
        }

        var endpoint = FindTypeScriptString(node);
        if (string.IsNullOrWhiteSpace(endpoint) || !UrlRegex.IsMatch(endpoint!))
        {
            return;
        }

        var endpointId = StableId.For("frontend-api", endpoint!);
        graph.AddNode("ApiEndpoint", endpointId, new Dictionary<string, object?>
        {
            ["name"] = endpoint,
            ["path"] = endpoint,
            ["framework"] = "Frontend",
            ["contentStored"] = false
        });
        graph.AddRelationship("CALLS_API", functionId ?? componentId ?? assetId, endpointId, new Dictionary<string, object?>
        {
            ["caller"] = callee,
            ["sourceFile"] = StableId.NormalizePath(file.Path),
            ["sourceLine"] = node.StartPosition.Row + 1,
            ["confidence"] = "TREE_SITTER_CALL_LITERAL_URL"
        });
    }

    /// <summary>從 TypeScript 語法節點取得指定欄位。</summary>
    private static TsNode? GetTypeScriptField(TsNode node, string field)
    {
        var child = node.GetChildForField(field);
        if (child is null)
        {
            return null;
        }

        return child.Id == IntPtr.Zero ? null : child;
    }

    /// <summary>從 TypeScript 語法節點取得可解析的名稱。</summary>
    private static string? GetTypeScriptName(TsNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var value = node.Text.Trim().Trim('"', '\'', '`');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>判斷 TypeScript 節點是否代表函式。</summary>
    private static bool IsTypeScriptFunction(TsNode node)
        => node.Type is "arrow_function" or "function" or "function_expression";

    /// <summary>從 TypeScript 語法節點尋找字串常值。</summary>
    private static string? FindTypeScriptString(TsNode node)
    {
        if (node.Type is "string" or "template_string")
        {
            return node.Text.Trim().Trim('"', '\'', '`');
        }

        foreach (var child in node.NamedChildren)
        {
            var value = FindTypeScriptString(child);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>遞迴走訪 JavaScript AST，抽取元件、函式與 API 線索。</summary>
    private void WalkJavaScriptNode(
        CodeGraphData graph,
        FrontendFileInfo file,
        string assetId,
        JsNode node,
        string? componentId,
        string? functionId,
        string? suggestedName)
    {
        var currentComponentId = componentId;
        var currentFunctionId = functionId;
        var nodeType = node.GetType().Name;

        if (nodeType == "CallExpression" && GetStaticName(GetNodeProperty(node, "Callee"))?.Equals("Ext.define", StringComparison.OrdinalIgnoreCase) == true)
        {
            var arguments = GetNodeList(node, "Arguments");
            var componentName = arguments.Select(GetStringValue).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(componentName))
            {
                currentComponentId = AddComponent(graph, file, assetId, componentName!, "ExtJS", "Ext.define", node);
            }
        }

        if (nodeType is "ClassDeclaration" or "ClassExpression")
        {
            var name = GetStringValue(GetNodeProperty(node, "Id"));
            if (!string.IsNullOrWhiteSpace(name) && IsComponentName(name!))
            {
                currentComponentId = AddComponent(graph, file, assetId, name!, "React", "ClassComponent", node);
            }
        }

        if (nodeType is "FunctionDeclaration" or "FunctionExpression" or "ArrowFunctionExpression")
        {
            var name = GetStringValue(GetNodeProperty(node, "Id")) ?? suggestedName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (currentComponentId is null && IsComponentName(name!))
                {
                    currentComponentId = AddComponent(graph, file, assetId, name!, "React", "FunctionComponent", node);
                }

                currentFunctionId = AddFunction(graph, file, assetId, name!, currentComponentId, node);
            }
        }

        if (nodeType == "MethodDefinition")
        {
            var methodName = GetStaticName(GetNodeProperty(node, "Key"));
            if (!string.IsNullOrWhiteSpace(methodName))
            {
                currentFunctionId = AddFunction(graph, file, assetId, methodName!, currentComponentId, node);
            }
        }

        if (nodeType == "VariableDeclarator")
        {
            var variableName = GetStringValue(GetNodeProperty(node, "Id"));
            var initializer = GetNodeProperty(node, "Init");
            if (!string.IsNullOrWhiteSpace(variableName) && initializer is not null && IsFunctionNode(initializer))
            {
                if (currentComponentId is null && IsComponentName(variableName!))
                {
                    currentComponentId = AddComponent(graph, file, assetId, variableName!, "React", "FunctionComponent", initializer);
                }

                WalkJavaScriptNode(graph, file, assetId, initializer, currentComponentId, currentFunctionId, variableName);
                return;
            }
        }

        if (nodeType == "Property")
        {
            var propertyName = GetStaticName(GetNodeProperty(node, "Key"));
            var propertyValue = GetNodeProperty(node, "Value");
            if (!string.IsNullOrWhiteSpace(propertyName) && propertyValue is not null && IsFunctionNode(propertyValue))
            {
                WalkJavaScriptNode(graph, file, assetId, propertyValue, currentComponentId, currentFunctionId, propertyName);
                return;
            }

            if (propertyName is not null &&
                (propertyName.Equals("xtype", StringComparison.OrdinalIgnoreCase) ||
                 propertyName.Equals("alias", StringComparison.OrdinalIgnoreCase)))
            {
                var value = GetStringValue(propertyValue);
                if (!string.IsNullOrWhiteSpace(value) && currentComponentId is not null)
                {
                    var uiId = StableId.For("frontend-ui", StableId.NormalizePath(file.Path), propertyName, value!);
                    graph.AddNode("UIElement", uiId, new Dictionary<string, object?>
                    {
                        ["name"] = value,
                        ["kind"] = propertyName.Equals("xtype", StringComparison.OrdinalIgnoreCase) ? "ExtJS_XTYPE" : "ExtJS_ALIAS",
                        ["filePath"] = StableId.NormalizePath(file.Path),
                        ["startLine"] = GetStartLine(node),
                        ["endLine"] = GetEndLine(node),
                        ["contentStored"] = false
                    });
                    graph.AddRelationship("USES_UI_ELEMENT", currentComponentId, uiId);
                }
            }
        }

        if (nodeType == "JsxOpeningElement")
        {
            var jsxName = GetStaticName(GetNodeProperty(node, "Name"));
            if (!string.IsNullOrWhiteSpace(jsxName) && currentComponentId is not null)
            {
                var uiId = StableId.For("frontend-ui", StableId.NormalizePath(file.Path), "jsx", jsxName!, GetStartLine(node));
                graph.AddNode("UIElement", uiId, new Dictionary<string, object?>
                {
                    ["name"] = jsxName,
                    ["kind"] = "JSX_ELEMENT",
                    ["filePath"] = StableId.NormalizePath(file.Path),
                    ["startLine"] = GetStartLine(node),
                    ["endLine"] = GetEndLine(node),
                    ["contentStored"] = false
                });
                graph.AddRelationship("RENDERS_UI_ELEMENT", currentComponentId, uiId);
            }
        }

        if (nodeType == "CallExpression")
        {
            AddApiCall(graph, file, assetId, currentComponentId, currentFunctionId, node);
        }

        if (nodeType == "ImportDeclaration")
        {
            var sourceValue = GetStringValue(GetNodeProperty(node, "Source"));
            AddImportRelation(graph, file, assetId, sourceValue);
        }

        foreach (var child in node.ChildNodes)
        {
            WalkJavaScriptNode(graph, file, assetId, child, currentComponentId, currentFunctionId, null);
        }
    }

    /// <summary>建立 JavaScript 元件節點並保存其位置範圍。</summary>
    private string AddComponent(CodeGraphData graph, FrontendFileInfo file, string assetId, string name, string framework, string kind, JsNode node)
    {
        var id = StableId.For("frontend-component", StableId.NormalizePath(file.Path), name);
        graph.AddNode("FrontendComponent", id, new Dictionary<string, object?>
        {
            ["name"] = name,
            ["fullName"] = name,
            ["framework"] = framework,
            ["kind"] = kind,
            ["filePath"] = StableId.NormalizePath(file.Path),
            ["startLine"] = GetStartLine(node),
            ["endLine"] = GetEndLine(node),
            ["contentStored"] = false,
            ["bodyStored"] = false
        });
        graph.AddRelationship("DECLARES_COMPONENT", assetId, id, new Dictionary<string, object?>
        {
            ["framework"] = framework,
            ["sourceLine"] = GetStartLine(node)
        });
        return id;
    }

    /// <summary>建立 JavaScript 函式節點，只保存名稱與行號範圍。</summary>
    private string AddFunction(CodeGraphData graph, FrontendFileInfo file, string assetId, string name, string? componentId, JsNode node)
    {
        var id = StableId.For("frontend-function", StableId.NormalizePath(file.Path), name, GetStartLine(node));
        graph.AddNode("FrontendFunction", id, new Dictionary<string, object?>
        {
            ["name"] = name,
            ["signature"] = name,
            ["filePath"] = StableId.NormalizePath(file.Path),
            ["startLine"] = GetStartLine(node),
            ["endLine"] = GetEndLine(node),
            ["bodyStored"] = false,
            ["contentStored"] = false
        });
        graph.AddRelationship("DECLARES_FUNCTION", assetId, id, new Dictionary<string, object?>
        {
            ["startLine"] = GetStartLine(node),
            ["endLine"] = GetEndLine(node)
        });
        if (componentId is not null)
        {
            graph.AddRelationship("COMPONENT_HAS_FUNCTION", componentId, id);
        }

        return id;
    }

    /// <summary>加入「AddApiCall」所代表的圖譜抽取或匯入工作。</summary>
    private void AddApiCall(CodeGraphData graph, FrontendFileInfo file, string assetId, string? componentId, string? functionId, JsNode node)
    {
        var callee = GetStaticName(GetNodeProperty(node, "Callee"));
        if (string.IsNullOrWhiteSpace(callee) || !LooksLikeApiCall(callee!))
        {
            return;
        }

        var endpoint = GetNodeList(node, "Arguments")
            .SelectMany(FindStringValues)
            .FirstOrDefault(value => UrlRegex.IsMatch(value));
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        var endpointId = StableId.For("frontend-api", endpoint!);
        graph.AddNode("ApiEndpoint", endpointId, new Dictionary<string, object?>
        {
            ["name"] = endpoint,
            ["path"] = endpoint,
            ["framework"] = "Frontend",
            ["contentStored"] = false
        });
        var sourceId = functionId ?? componentId ?? assetId;
        graph.AddRelationship("CALLS_API", sourceId, endpointId, new Dictionary<string, object?>
        {
            ["caller"] = callee,
            ["sourceFile"] = StableId.NormalizePath(file.Path),
            ["sourceLine"] = GetStartLine(node),
            ["confidence"] = "AST_CALL_LITERAL_URL"
        });
    }

    /// <summary>加入「AddImportRelation」所代表的圖譜抽取或匯入工作。</summary>
    private void AddImportRelation(CodeGraphData graph, FrontendFileInfo file, string assetId, string? sourceValue)
    {
        if (string.IsNullOrWhiteSpace(sourceValue) || !sourceValue.StartsWith(".", StringComparison.Ordinal))
        {
            return;
        }

        var basePath = Path.Combine(Path.GetDirectoryName(file.Path)!, sourceValue.Replace('/', Path.DirectorySeparatorChar));
        var candidates = new[]
        {
            basePath,
            basePath + ".js",
            basePath + ".jsx",
            basePath + ".ts",
            basePath + ".tsx",
            Path.Combine(basePath, "index.js"),
            Path.Combine(basePath, "index.jsx")
        };
        var target = candidates.FirstOrDefault(File.Exists);
        if (target is null)
        {
            return;
        }

        var targetInfo = _catalog.FrontendFiles.FirstOrDefault(fileInfo =>
            StableId.NormalizePath(fileInfo.Path).Equals(StableId.NormalizePath(target), StringComparison.OrdinalIgnoreCase));
        if (targetInfo is null)
        {
            return;
        }

        var targetId = AddAssetNode(graph, targetInfo, _codeIndex.FindProjectForPath(targetInfo.Path));
        graph.AddRelationship("IMPORTS_SCRIPT", assetId, targetId, new Dictionary<string, object?>
        {
            ["import"] = sourceValue
        });
    }

    /// <summary>加入「AddBackendViewRelations」所代表的圖譜抽取或匯入工作。</summary>
    private void AddBackendViewRelations(CodeGraphData graph, CancellationToken cancellationToken)
    {
        foreach (var file in _catalog.CSharpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileId = _codeIndex.FileIdsByPath.TryGetValue(StableId.NormalizePath(file), out var resolvedFileId)
                ? resolvedFileId
                : null;
            if (fileId is null)
            {
                continue;
            }

            string source;
            try { source = File.ReadAllText(file); }
            catch { continue; }

            var root = CSharpSyntaxTree.ParseText(source, path: file).GetRoot();
            foreach (var controller in root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                         .Where(item => item.Identifier.ValueText.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)))
            {
                var controllerName = controller.Identifier.ValueText[..^"Controller".Length];
                foreach (var method in controller.Members.OfType<MethodDeclarationSyntax>())
                {
                    var startLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    if (!_codeIndex.MethodIdsByLocation.TryGetValue((fileId, startLine), out var methodId))
                    {
                        continue;
                    }

                    graph.AddNode("Method", methodId, new Dictionary<string, object?>
                    {
                        ["role"] = "ControllerAction",
                        ["controllerName"] = controllerName,
                        ["frontendFramework"] = "ASP.NET_MVC_WebFormsView"
                    });

                    var returnStatements = method.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
                    foreach (var returnStatement in returnStatements)
                    {
                        var viewCalls = returnStatement.Expression?
                            .DescendantNodesAndSelf()
                            .OfType<InvocationExpressionSyntax>()
                            .Where(IsViewInvocation)
                            .ToList() ?? new List<InvocationExpressionSyntax>();

                        for (var index = 0; index < viewCalls.Count; index++)
                        {
                            AddViewRelation(
                                graph,
                                methodId,
                                file,
                                controllerName,
                                method.Identifier.ValueText,
                                viewCalls[index],
                                GetBranchInfo(returnStatement),
                                index);
                        }

                    }

                    // MVC actions in this codebase frequently use expression-bodied
                    // methods, for example: `Index() => View("frmCapitalMaintain")`.
                    // Such methods have no ReturnStatementSyntax, so handle them here.
                    var expressionBodyViewCalls = method.ExpressionBody?.Expression
                        .DescendantNodesAndSelf()
                        .OfType<InvocationExpressionSyntax>()
                        .Where(IsViewInvocation)
                        .ToList() ?? new List<InvocationExpressionSyntax>();
                    for (var index = 0; index < expressionBodyViewCalls.Count; index++)
                    {
                        var viewCall = expressionBodyViewCalls[index];
                        AddViewRelation(
                            graph,
                            methodId,
                            file,
                            controllerName,
                            method.Identifier.ValueText,
                            viewCall,
                            ("expression", GetLine(viewCall), GetLine(viewCall)),
                            index);
                    }
                }
            }
        }
    }

    /// <summary>加入「AddViewRelation」所代表的圖譜抽取或匯入工作。</summary>
    private void AddViewRelation(
        CodeGraphData graph,
        string methodId,
        string sourceFile,
        string controllerName,
        string actionName,
        InvocationExpressionSyntax viewCall,
        (string Kind, int StartLine, int EndLine) branch,
        int branchIndex)
    {
        var viewName = GetViewName(viewCall) ?? actionName;
        var page = _catalog.ResolveView(controllerName, viewName);
        var pageId = page is null
            ? StableId.For("unresolved-view", StableId.NormalizePath(sourceFile), actionName, viewName)
            : EnsurePageNode(graph, page);

        if (page is null)
        {
            graph.AddNode("UnresolvedView", pageId, new Dictionary<string, object?>
            {
                ["name"] = viewName,
                ["controller"] = controllerName,
                ["exists"] = false,
                ["sourceFile"] = StableId.NormalizePath(sourceFile),
                ["sourceLine"] = GetLine(viewCall)
            });
        }

        graph.AddRelationship("RETURNS_VIEW", methodId, pageId, new Dictionary<string, object?>
        {
            ["viewName"] = viewName,
            ["viewKind"] = GetViewInvocationName(viewCall),
            ["sourceFile"] = StableId.NormalizePath(sourceFile),
            ["sourceLine"] = GetLine(viewCall),
            ["branchKind"] = branch.Kind,
            ["branchIndex"] = branchIndex,
            ["conditionStartLine"] = branch.StartLine,
            ["conditionEndLine"] = branch.EndLine,
            ["resolved"] = page is not null,
            ["confidence"] = page is null ? "ACTION_VIEW_NAME_ONLY" : "ACTION_VIEW_NAME_AND_ASPX_EXISTS"
        });
    }

    /// <summary>確保「EnsurePageNode」所代表的圖譜抽取或匯入工作。</summary>
    private string EnsurePageNode(CodeGraphData graph, FrontendFileInfo page)
    {
        var projectId = _codeIndex.FindProjectForPath(page.Path);
        var pageId = AddAssetNode(graph, page, projectId);
        if (!graph.Nodes.Any(node => node.Id == pageId && node.Label == "WebFormPage"))
        {
            return pageId;
        }

        return pageId;
    }

    /// <summary>加入「AddBundleComponentRelations」所代表的圖譜抽取或匯入工作。</summary>
    private void AddBundleComponentRelations(CodeGraphData graph)
    {
        var bundles = graph.Nodes.Where(node => node.Label == "FrontendBundle").ToList();
        var components = graph.Nodes.Where(node => node.Label == "FrontendComponent").ToList();
        var configTexts = new List<(string Path, string Text)>();
        foreach (var file in _catalog.BuildConfigurationFiles)
        {
            try { configTexts.Add((file, File.ReadAllText(file))); }
            catch { }
        }

        foreach (var bundle in bundles)
        {
            var bundlePath = GetProperty(bundle, "path");
            var bundleName = Path.GetFileNameWithoutExtension(bundlePath);
            var bundleStem = Regex.Replace(bundleName, @"\.bundle$", string.Empty, RegexOptions.IgnoreCase);
            foreach (var component in components)
            {
                var componentPath = GetProperty(component, "filePath");
                var componentStem = Path.GetFileNameWithoutExtension(componentPath);
                var exactNameMatch = componentStem.Equals(bundleStem, StringComparison.OrdinalIgnoreCase) ||
                                     bundleStem.Contains(componentStem, StringComparison.OrdinalIgnoreCase);
                var configMatch = configTexts.Any(config =>
                    config.Text.Contains(Path.GetFileName(bundlePath), StringComparison.OrdinalIgnoreCase) &&
                    (config.Text.Contains(componentStem, StringComparison.OrdinalIgnoreCase) ||
                     config.Text.Contains(GetProperty(component, "name"), StringComparison.OrdinalIgnoreCase)));
                if (!exactNameMatch && !configMatch)
                {
                    continue;
                }

                graph.AddRelationship("COMPILED_TO", component.Id, bundle.Id, new Dictionary<string, object?>
                {
                    ["confidence"] = configMatch ? "BUILD_CONFIGURATION" : "FILENAME_CONVENTION",
                    ["bundleContentParsed"] = false,
                    ["sourceMapParsed"] = false
                });
            }
        }
    }

    /// <summary>執行「Merge」所代表的圖譜抽取或匯入工作。</summary>
    private static void Merge(CodeGraphData target, CodeGraphData source)
    {
        foreach (var node in source.Nodes)
        {
            target.AddNode(node.Label, node.Id, node.Properties);
        }

        foreach (var relationship in source.Relationships)
        {
            var properties = new Dictionary<string, object?>(relationship.Properties, StringComparer.Ordinal);
            if (relationship.Locations.Count > 0)
            {
                properties["locations"] = relationship.Locations.ToArray();
            }

            for (var index = 0; index < Math.Max(relationship.OccurrenceCount, 1); index++)
            {
                target.AddRelationship(relationship.Type, relationship.StartId, relationship.EndId, properties);
            }
        }
    }

    /// <summary>加入「AddUnresolvedAsset」所代表的圖譜抽取或匯入工作。</summary>
    private string AddUnresolvedAsset(CodeGraphData graph, string reference, string? projectId)
    {
        var isBundle = reference.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase) ||
                       reference.Contains("/dist/", StringComparison.OrdinalIgnoreCase);
        var label = isBundle ? "FrontendBundle" : "ScriptAsset";
        var id = StableId.For("unresolved-frontend-asset", reference);
        graph.AddNode(label, id, new Dictionary<string, object?>
        {
            ["name"] = Path.GetFileName(reference),
            ["path"] = reference,
            ["relativePath"] = reference,
            ["exists"] = false,
            ["contentStored"] = false,
            ["parser"] = "INVENTORY_ONLY"
        });
        if (projectId is not null)
        {
            graph.AddRelationship("CONTAINS_FRONTEND_ASSET", projectId, id);
        }

        return id;
    }

    /// <summary>抽取「ExtractScriptReference」所代表的圖譜抽取或匯入工作。</summary>
    private static string ExtractScriptReference(string raw)
    {
        var match = ScriptPathRegex.Match(raw);
        if (match.Success)
        {
            return match.Groups["path"].Value.Split('?', '#')[0];
        }

        return string.Empty;
    }

    /// <summary>判斷「IsViewInvocation」所代表的圖譜抽取或匯入工作。</summary>
    private static bool IsViewInvocation(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty
        };
        return name.Equals("View", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("PartialView", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取得「GetViewInvocationName」所代表的圖譜抽取或匯入工作。</summary>
    private static string GetViewInvocationName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => "View"
        };

    /// <summary>取得「GetViewName」所代表的圖譜抽取或匯入工作。</summary>
    private static string? GetViewName(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)
                => literal.Token.ValueText,
            _ => null
        };

    /// <summary>取得「GetBranchInfo」所代表的圖譜抽取或匯入工作。</summary>
    private static (string Kind, int StartLine, int EndLine) GetBranchInfo(ReturnStatementSyntax returnStatement)
    {
        var ifStatement = returnStatement.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        if (ifStatement is not null)
        {
            var isElse = returnStatement.Ancestors().OfType<ElseClauseSyntax>().Any(elseClause =>
                elseClause.Statement.Span.Contains(returnStatement.Span));
            return (isElse ? "else" : "if", GetLine(ifStatement), ifStatement.GetLocation().GetLineSpan().EndLinePosition.Line + 1);
        }

        var switchSection = returnStatement.Ancestors().OfType<SwitchSectionSyntax>().FirstOrDefault();
        if (switchSection is not null)
        {
            return ("switch", GetLine(switchSection), switchSection.GetLocation().GetLineSpan().EndLinePosition.Line + 1);
        }

        return ("unconditional", GetLine(returnStatement), GetLine(returnStatement));
    }

    /// <summary>取得「GetLine」所代表的圖譜抽取或匯入工作。</summary>
    private static int GetLine(SyntaxNode node)
        => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>執行「SafeLineCount」所代表的圖譜抽取或匯入工作。</summary>
    private static int SafeLineCount(string path)
    {
        try { return File.ReadLines(path).Count(); }
        catch { return 0; }
    }

    /// <summary>判斷「IsFunctionNode」所代表的圖譜抽取或匯入工作。</summary>
    private static bool IsFunctionNode(JsNode node)
        => node.GetType().Name is "FunctionDeclaration" or "FunctionExpression" or "ArrowFunctionExpression";

    /// <summary>判斷「IsComponentName」所代表的圖譜抽取或匯入工作。</summary>
    private static bool IsComponentName(string name)
        => !string.IsNullOrWhiteSpace(name) && char.IsUpper(name.TrimStart('_')[0]);

    /// <summary>執行「LooksLikeApiCall」所代表的圖譜抽取或匯入工作。</summary>
    private static bool LooksLikeApiCall(string name)
        => name.Equals("fetch", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("axios.", StringComparison.OrdinalIgnoreCase) ||
           name.EndsWith(".ajax", StringComparison.OrdinalIgnoreCase) ||
           name.EndsWith(".request", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Ext.Ajax.request", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("Ext.net.DirectMethod.request", StringComparison.OrdinalIgnoreCase);

    /// <summary>取得「FindStringValues」所代表的圖譜抽取或匯入工作。</summary>
    private static IEnumerable<string> FindStringValues(JsNode node)
    {
        foreach (var child in node.ChildNodes)
        {
            var value = GetStringValue(child);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value!;
            }

            foreach (var nested in FindStringValues(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>取得「GetStaticName」所代表的圖譜抽取或匯入工作。</summary>
    private static string? GetStaticName(JsNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var nodeType = node.GetType().Name;
        if (nodeType == "Identifier" || nodeType == "JsxIdentifier")
        {
            return GetStringValue(node);
        }

        if (nodeType is "StaticMemberExpression" or "MemberExpression" or "ComputedMemberExpression")
        {
            var left = GetStaticName(GetNodeProperty(node, "Object"));
            var right = GetStaticName(GetNodeProperty(node, "Property"));
            return string.IsNullOrWhiteSpace(left) ? right : string.IsNullOrWhiteSpace(right) ? left : left + "." + right;
        }

        return GetStringValue(node);
    }

    /// <summary>取得「GetStringValue」所代表的圖譜抽取或匯入工作。</summary>
    private static string? GetStringValue(JsNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var nodeType = node.GetType().Name;
        if (nodeType is "Identifier" or "JsxIdentifier")
        {
            return GetNodePropertyValue(node, "Name")?.ToString();
        }

        if (nodeType == "Literal")
        {
            return GetNodePropertyValue(node, "StringValue")?.ToString() ??
                   GetNodePropertyValue(node, "Value")?.ToString();
        }

        return null;
    }

    /// <summary>取得「GetNodeProperty」所代表的圖譜抽取或匯入工作。</summary>
    private static JsNode? GetNodeProperty(JsNode node, string name)
        => node.GetType().GetProperty(name)?.GetValue(node) as JsNode;

    /// <summary>取得「GetNodeList」所代表的圖譜抽取或匯入工作。</summary>
    private static IReadOnlyList<JsNode> GetNodeList(JsNode node, string name)
    {
        var value = node.GetType().GetProperty(name)?.GetValue(node) as System.Collections.IEnumerable;
        return value?.Cast<object>().OfType<JsNode>().ToArray() ?? Array.Empty<JsNode>();
    }

    /// <summary>取得「GetNodePropertyValue」所代表的圖譜抽取或匯入工作。</summary>
    private static object? GetNodePropertyValue(JsNode node, string name)
        => node.GetType().GetProperty(name)?.GetValue(node);

    /// <summary>取得「GetStartLine」所代表的圖譜抽取或匯入工作。</summary>
    private static int GetStartLine(JsNode node)
        => node.Location.Start.Line;

    /// <summary>取得「GetEndLine」所代表的圖譜抽取或匯入工作。</summary>
    private static int GetEndLine(JsNode node)
        => node.Location.End.Line;

    /// <summary>加入「AddAssetProperty」所代表的圖譜抽取或匯入工作。</summary>
    private static void AddAssetProperty(CodeGraphData graph, string assetId, string name, object value)
    {
        var node = graph.Nodes.FirstOrDefault(item => item.Id == assetId);
        if (node is not null)
        {
            node.Properties[name] = value;
        }
    }

    /// <summary>取得「GetProperty」所代表的圖譜抽取或匯入工作。</summary>
    private static string GetProperty(GraphNode node, string name)
        => node.Properties.TryGetValue(name, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    /// <summary>加入「AddDiagnostic」所代表的圖譜抽取或匯入工作。</summary>
    private void AddDiagnostic(string message)
    {
        lock (_diagnostics)
        {
            _diagnostics.Add(message);
        }
    }

    /// <summary>統計「CountNodes」所代表的圖譜抽取或匯入工作。</summary>
    private static int CountNodes(CodeGraphData graph, string label)
        => graph.Nodes.Count(node => node.Label.Equals(label, StringComparison.Ordinal));

    /// <summary>統計「CountRelationships」所代表的圖譜抽取或匯入工作。</summary>
    private static int CountRelationships(CodeGraphData graph, string type)
        => graph.Relationships.Count(relationship => relationship.Type.Equals(type, StringComparison.Ordinal));
}


