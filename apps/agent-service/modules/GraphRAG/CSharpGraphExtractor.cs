using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 使用 Roslyn SyntaxTree 與 SemanticModel 抽取 C# 的 type-level GraphRAG 關係。
/// 此 extractor 刻意不產生 method、property、field 節點；這些細節只保存在 Code node 或 CALLS edge evidence，
/// 讓 LLM 能定位應修改的型別與檔案，同時避免大型傳統 MVC 專案出現數萬個低價值節點。
/// </summary>
public sealed class CSharpGraphExtractor(ILogger<CSharpGraphExtractor> logger) : IGraphExtractor
{
    private const int MaximumEvidenceItems = 40;
    private static readonly SymbolDisplayFormat TypeDisplayFormat =
        new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <inheritdoc />
    public string Id => "csharp-roslyn-v3";

    /// <inheritdoc />
    public string Version => "3.8.0";

    /// <inheritdoc />
    public async Task<GraphFragment> ExtractAsync(
        string projectRoot,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(files);
        var normalizedRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException($"C# 專案根目錄不存在：{normalizedRoot}");

        var allCandidates = files
            .Where(file => string.Equals(Path.GetExtension(file), ".cs", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Where(file => IsInsideRoot(normalizedRoot, file))
            .Where(file => !ShouldIgnore(file, normalizedRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // 超大型 monorepo 若把演算法、第三方函式庫、DTO/value-object 全部建成
        // Code node，既拖慢索引也會提高檢索雜訊。C# extractor 只保留會形成
        // UI→Controller→BZ/Service→QR/DAL→Data 修改路徑的協調型檔案；
        // SQL/ORM extractor 仍會獨立掃描全部來源，資料關係不受此邊界影響。
        var candidates = allCandidates.Count > 2_000
            ? allCandidates.Where(file =>
                    IsLargeRepositoryCallPathFile(normalizedRoot, file))
                .ToList()
            : allCandidates;
        if (candidates.Count == 0) return new GraphFragment();

        var fragment = new GraphFragment();
        var parsedTrees = new ConcurrentBag<SyntaxTree>();
        var syntaxDiagnostics = new ConcurrentBag<GraphDiagnostic>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(
                    Environment.ProcessorCount, 2, 12),
            },
            async (file, token) =>
            {
                var source = await File.ReadAllTextAsync(file, token);
                if (LooksGenerated(source)) return;
                var tree = CSharpSyntaxTree.ParseText(
                    source,
                    CSharpParseOptions.Default.WithLanguageVersion(
                        LanguageVersion.Preview),
                    file,
                    Encoding.UTF8,
                    token);
                parsedTrees.Add(tree);
                foreach (var diagnostic in tree.GetDiagnostics(token)
                             .Where(item =>
                                 item.Severity == DiagnosticSeverity.Error))
                    syntaxDiagnostics.Add(new GraphDiagnostic(
                        "CSHARP_SYNTAX_PARTIAL",
                        GraphDiagnosticSeverity.Warning,
                        RelativePath(normalizedRoot, file),
                        $"C# 語法無法完整解析，該區段不會建立未證實關係：{BoundText(diagnostic.GetMessage(), 240)}",
                        false));
            });
        var syntaxTrees = parsedTrees
            .OrderBy(tree => tree.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        fragment.Diagnostics.AddRange(syntaxDiagnostics
            .OrderBy(item => item.Artifact, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)
            .Take(100));

        if (syntaxTrees.Count > 2_000)
        {
            ExtractLargeRepositorySyntax(
                fragment, syntaxTrees, normalizedRoot, cancellationToken);
            logger.LogInformation(
                "C# GraphRAG V3 bounded AST 抽取完成：候選 {CandidateCount}、實際 {FileCount} 個檔案、{NodeCount} 個節點、{EdgeCount} 條關係。",
                allCandidates.Count, syntaxTrees.Count, fragment.Nodes.Count, fragment.Edges.Count);
            return fragment;
        }

        var compilation = CSharpCompilation.Create(
            $"ModernWingmanGraph_{Guid.NewGuid():N}",
            syntaxTrees,
            ResolveFrameworkReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                concurrentBuild: true));
        var declaredTypes = CollectDeclaredTypes(
            compilation, syntaxTrees, normalizedRoot, cancellationToken);
        foreach (var type in declaredTypes.Values.OrderBy(item => item.NodeId, StringComparer.Ordinal))
            fragment.Nodes.Add(CreateCodeNode(type));
        var declaredMethodNames = declaredTypes.Values
            .SelectMany(info => info.Declarations)
            .SelectMany(declaration => declaration.Syntax.Members
                .OfType<MethodDeclarationSyntax>())
            .Select(method => method.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        var callOwnersByTree = BuildCallOwnersByTree(declaredTypes);

        ExtractControllerEntries(fragment, declaredTypes);
        ExtractScheduledTaskEntries(fragment, declaredTypes);
        var unresolvedInvocations = ExtractTypeCalls(
            fragment,
            compilation,
            callOwnersByTree,
            normalizedRoot,
            declaredTypes,
            declaredMethodNames,
            cancellationToken);
        if (unresolvedInvocations > 0)
        {
            // compilation.GetDiagnostics 會對整個傳統 trunk 做額外的全量 binding，
            // 但 CALLS 真正需要的是下方逐 invocation 的 SymbolInfo。彙總未解析數量
            // 能誠實標示 coverage，同時避免為重複的缺 DLL 訊息付出數分鐘成本。
            fragment.Diagnostics.Add(new GraphDiagnostic(
                "CSHARP_SEMANTIC_PARTIAL",
                GraphDiagnosticSeverity.Warning,
                "project:csharp",
                $"有 {unresolvedInvocations} 個 C# 呼叫因外部相依或不完整專案內容無法解析；已略過未證實的 CALLS。",
                true));
        }

        logger.LogInformation(
            "C# GraphRAG V3 抽取完成：候選 {CandidateCount}、實際 {FileCount} 個檔案、{NodeCount} 個節點、{EdgeCount} 條關係。",
            allCandidates.Count, syntaxTrees.Count, fragment.Nodes.Count, fragment.Edges.Count);
        return fragment;
    }

    private static void ExtractLargeRepositorySyntax(
        GraphFragment fragment,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        string root,
        CancellationToken cancellationToken)
    {
        var declarations = new Dictionary<string, SyntaxDeclaredTypeInfo>(
            StringComparer.Ordinal);
        foreach (var tree in syntaxTrees)
        {
            var relativePath = RelativePath(root, tree.FilePath);
            foreach (var syntax in tree.GetRoot(cancellationToken)
                         .DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var qualifiedName = SyntaxQualifiedName(syntax);
                if (!declarations.TryGetValue(qualifiedName, out var info))
                {
                    var name = syntax.Identifier.ValueText;
                    info = new SyntaxDeclaredTypeInfo(
                        GraphIdentity.CSharpCode(qualifiedName),
                        qualifiedName,
                        name,
                        DetermineSyntaxRole(syntax));
                    declarations.Add(qualifiedName, info);
                }
                var span = syntax.GetLocation().GetLineSpan();
                info.Declarations.Add(new SyntaxTypeDeclaration(
                    syntax,
                    relativePath,
                    span.StartLinePosition.Line + 1,
                    span.EndLinePosition.Line + 1));
            }
        }

        foreach (var info in declarations.Values.OrderBy(
                     item => item.NodeId, StringComparer.Ordinal))
            fragment.Nodes.Add(CreateSyntaxCodeNode(info));
        ExtractSyntaxControllerEntries(fragment, declarations.Values);
        ExtractSyntaxScheduledTaskEntries(fragment, declarations.Values);
        ExtractSyntaxTypeCalls(fragment, declarations.Values, cancellationToken);
    }

    private static GraphNode CreateSyntaxCodeNode(SyntaxDeclaredTypeInfo info)
    {
        var declarations = info.Declarations
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine)
            .ToList();
        var primary = declarations[0];
        var methods = declarations
            .SelectMany(item => item.Syntax.Members.OfType<MethodDeclarationSyntax>())
            .Where(method => method.Modifiers.Any(SyntaxKind.PublicKeyword))
            .Select(MethodSignature)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaximumEvidenceItems)
            .ToList();
        var baseTypes = declarations
            .SelectMany(item => item.Syntax.BaseList?.Types ?? [])
            .Select(item => item.Type.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaximumEvidenceItems)
            .ToList();
        var dependencies = declarations
            .SelectMany(item => item.Syntax.Members.OfType<ConstructorDeclarationSyntax>())
            .SelectMany(item => item.ParameterList.Parameters)
            .Select(item => item.Type?.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaximumEvidenceItems)
            .ToList();
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["declarationKind"] = primary.Syntax.Kind().ToString(),
            ["methods"] = string.Join(" | ", methods),
            ["baseTypes"] = string.Join(" | ", baseTypes),
            ["constructorDependencies"] = string.Join(" | ", dependencies),
        };
        var evidence = declarations.Select(item => new GraphEvidence(
            GraphEvidenceSource.Ast,
            GraphConfidence.Exact,
            item.RelativePath,
            "由 Roslyn AST 解析大型專案型別宣告；方法與繼承細節保留於 evidence。",
            item.StartLine,
            item.EndLine,
            details)).ToList();
        return new GraphNode(
            info.NodeId,
            GraphNodeKind.Code,
            info.Role,
            info.Name,
            string.Join(' ', new[] { info.QualifiedName, info.Name, info.Role }
                .Concat(methods).Concat(baseTypes).Concat(dependencies)),
            "csharp",
            DetermineSyntaxTechnology(primary.Syntax),
            "active",
            new[] { info.Name, RemoveCommonSuffix(info.Name) }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            primary.RelativePath,
            primary.StartLine,
            primary.EndLine,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["qualifiedName"] = info.QualifiedName,
                ["partialDeclarationCount"] = declarations.Count.ToString(),
            },
            evidence);
    }

    private static void ExtractSyntaxControllerEntries(
        GraphFragment fragment,
        IEnumerable<SyntaxDeclaredTypeInfo> declaredTypes)
    {
        foreach (var info in declaredTypes.Where(item =>
                     item.Role == GraphRoles.Controller).OrderBy(
                     item => item.NodeId, StringComparer.Ordinal))
        {
            var controllerName = RemoveControllerSuffix(info.Name);
            foreach (var declaration in info.Declarations)
            {
                var classRoutes = AttributeArguments(
                    declaration.Syntax.AttributeLists, "Route", "RoutePrefix");
                foreach (var action in declaration.Syntax.Members
                             .OfType<MethodDeclarationSyntax>()
                             .Where(IsControllerAction))
                {
                    var actionName = GetActionName(action);
                    var entryId = GraphIdentity.WebEntry(controllerName, actionName);
                    var span = action.GetLocation().GetLineSpan();
                    var routes = BuildRouteAliases(
                        controllerName,
                        actionName,
                        classRoutes,
                        AttributeArguments(
                            action.AttributeLists,
                            "Route", "HttpGet", "HttpPost", "HttpPut",
                            "HttpDelete", "HttpPatch"));
                    var evidence = new GraphEvidence(
                        GraphEvidenceSource.Framework,
                        GraphConfidence.Exact,
                        declaration.RelativePath,
                        "由 Controller 公開 Action 與 Route Attribute 解析 HTTP 入口。",
                        span.StartLinePosition.Line + 1,
                        span.EndLinePosition.Line + 1,
                        new SortedDictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["method"] = MethodSignature(action),
                            ["httpVerbs"] = string.Join(" | ", HttpVerbs(action)),
                            ["routes"] = string.Join(" | ", routes),
                        });
                    fragment.Nodes.Add(new GraphNode(
                        entryId,
                        GraphNodeKind.EntryPoint,
                        GraphRoles.ControllerAction,
                        $"{controllerName}/{actionName}",
                        string.Join(' ', routes.Prepend(
                            $"{controllerName} {actionName}")),
                        "csharp",
                        DetermineSyntaxTechnology(declaration.Syntax),
                        "active",
                        routes,
                        declaration.RelativePath,
                        span.StartLinePosition.Line + 1,
                        span.EndLinePosition.Line + 1,
                        new SortedDictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["controller"] = controllerName,
                            ["action"] = actionName,
                        },
                        [evidence]));
                    fragment.Edges.Add(CreateEdge(
                        entryId,
                        GraphEdgeKind.Handles,
                        info.NodeId,
                        evidence with
                        {
                            Reason = "由 Controller Action 宣告位置確認此入口由該 Controller 型別處理。",
                        }));
                }
            }
        }
    }

    private static void ExtractSyntaxScheduledTaskEntries(
        GraphFragment fragment,
        IEnumerable<SyntaxDeclaredTypeInfo> declaredTypes)
    {
        foreach (var info in declaredTypes.OrderBy(
                     item => item.NodeId, StringComparer.Ordinal))
        {
            foreach (var declaration in info.Declarations)
            {
                foreach (var candidate in SyntaxScheduledTaskNames(declaration.Syntax))
                {
                    var span = candidate.Member.GetLocation().GetLineSpan();
                    var entryId = GraphIdentity.TaskEntry(candidate.Name);
                    var evidence = new GraphEvidence(
                        GraphEvidenceSource.Framework,
                        GraphConfidence.Exact,
                        declaration.RelativePath,
                        "由靜態 TaskName literal 解析排程入口與實作型別。",
                        span.StartLinePosition.Line + 1,
                        span.EndLinePosition.Line + 1);
                    fragment.Nodes.Add(new GraphNode(
                        entryId,
                        GraphNodeKind.EntryPoint,
                        GraphRoles.ScheduledTask,
                        candidate.Name,
                        $"{candidate.Name} {info.Name} {info.QualifiedName} 排程任務",
                        "csharp",
                        "dotnet-schedule-task",
                        "active",
                        [candidate.Name, info.Name],
                        declaration.RelativePath,
                        span.StartLinePosition.Line + 1,
                        span.EndLinePosition.Line + 1,
                        new SortedDictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["taskName"] = candidate.Name,
                        },
                        [evidence]));
                    fragment.Edges.Add(CreateEdge(
                        entryId, GraphEdgeKind.Handles, info.NodeId, evidence));
                }
            }
        }
    }

    private static IEnumerable<ScheduledTaskName> SyntaxScheduledTaskNames(
        TypeDeclarationSyntax declaration)
    {
        foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!property.Identifier.ValueText.Equals(
                    "TaskName", StringComparison.OrdinalIgnoreCase) ||
                !property.Modifiers.Any(SyntaxKind.StaticKeyword))
                continue;
            var expressions = property.DescendantNodes()
                .OfType<LiteralExpressionSyntax>()
                .Where(item => item.IsKind(SyntaxKind.StringLiteralExpression));
            foreach (var literal in expressions)
                if (!string.IsNullOrWhiteSpace(literal.Token.ValueText))
                    yield return new ScheduledTaskName(
                        literal.Token.ValueText, property);
        }
        foreach (var field in declaration.Members.OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                !field.Modifiers.Any(SyntaxKind.ConstKeyword))
                continue;
            foreach (var variable in field.Declaration.Variables)
            {
                if (!variable.Identifier.ValueText.Equals(
                        "TaskName", StringComparison.OrdinalIgnoreCase) ||
                    variable.Initializer?.Value is not LiteralExpressionSyntax literal ||
                    !literal.IsKind(SyntaxKind.StringLiteralExpression) ||
                    string.IsNullOrWhiteSpace(literal.Token.ValueText))
                    continue;
                yield return new ScheduledTaskName(
                    literal.Token.ValueText, field);
            }
        }
    }

    private static void ExtractSyntaxTypeCalls(
        GraphFragment fragment,
        IEnumerable<SyntaxDeclaredTypeInfo> declaredTypes,
        CancellationToken cancellationToken)
    {
        var types = declaredTypes.ToList();
        var typeLookup = types.GroupBy(
                item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(
                    item => item.QualifiedName, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var methodNames = types.SelectMany(item => item.Declarations)
            .SelectMany(item => item.Syntax.Members.OfType<MethodDeclarationSyntax>())
            .Select(item => item.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        var edgeEvidence =
            new Dictionary<(string Source, string Target), List<GraphEvidence>>();
        foreach (var source in types.Where(IsSyntaxCallPathType)
                     .OrderBy(item => item.NodeId, StringComparer.Ordinal))
        {
            foreach (var declaration in source.Declarations)
            {
                foreach (var invocation in declaration.Syntax.DescendantNodes()
                             .OfType<InvocationExpressionSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var method = InvokedMethodName(invocation.Expression);
                    if (method is null || !methodNames.Contains(method))
                        continue;
                    var receiverType = ReceiverTypeName(
                        invocation, declaration.Syntax);
                    if (receiverType is null ||
                        !typeLookup.TryGetValue(receiverType, out var candidates))
                        continue;
                    var target = ResolveUniqueSyntaxTarget(source, candidates);
                    if (target is null ||
                        string.Equals(source.NodeId, target.NodeId, StringComparison.Ordinal))
                        continue;
                    var span = invocation.GetLocation().GetLineSpan();
                    var evidence = new GraphEvidence(
                        GraphEvidenceSource.Ast,
                        GraphConfidence.Resolved,
                        declaration.RelativePath,
                        "由 receiver 宣告型別與大型專案內唯一 type mapping 解析 bounded CALLS。",
                        span.StartLinePosition.Line + 1,
                        span.EndLinePosition.Line + 1,
                        new SortedDictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["sourceMethod"] =
                                invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>()
                                    ?.Identifier.ValueText ?? "(initializer)",
                            ["targetMethod"] = method,
                            ["syntaxKind"] = invocation.Expression.Kind().ToString(),
                        });
                    var key = (source.NodeId, target.NodeId);
                    if (!edgeEvidence.TryGetValue(key, out var evidenceItems))
                    {
                        evidenceItems = [];
                        edgeEvidence.Add(key, evidenceItems);
                    }
                    if (evidenceItems.Count < MaximumEvidenceItems)
                        evidenceItems.Add(evidence);
                }
            }
        }
        foreach (var (key, evidence) in edgeEvidence
                     .OrderBy(item => item.Key.Source, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Target, StringComparer.Ordinal))
            fragment.Edges.Add(new GraphEdge(
                GraphIdentity.Edge(key.Source, GraphEdgeKind.Calls, key.Target),
                key.Source,
                GraphEdgeKind.Calls,
                key.Target,
                evidence));
    }

    private static bool IsSyntaxCallPathType(SyntaxDeclaredTypeInfo info) =>
        info.Role is GraphRoles.Controller or GraphRoles.BusinessService or
            GraphRoles.Repository or GraphRoles.ReportPlugin or GraphRoles.Migration ||
        new[] { "Task", "Job", "Schedule", "Workflow", "Handler" }
            .Any(marker => info.Name.Contains(
                marker, StringComparison.OrdinalIgnoreCase));

    private static SyntaxDeclaredTypeInfo? ResolveUniqueSyntaxTarget(
        SyntaxDeclaredTypeInfo source,
        IReadOnlyList<SyntaxDeclaredTypeInfo> candidates)
    {
        if (candidates.Count == 1) return candidates[0];
        var sourceNamespace = source.QualifiedName[..Math.Max(
            0, source.QualifiedName.Length - source.Name.Length)].TrimEnd('.');
        var sameNamespace = candidates.Where(candidate =>
                candidate.QualifiedName.StartsWith(
                    sourceNamespace + ".", StringComparison.Ordinal))
            .ToList();
        return sameNamespace.Count == 1 ? sameNamespace[0] : null;
    }

    private static string SyntaxQualifiedName(TypeDeclarationSyntax syntax)
    {
        var namespaces = syntax.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Name.ToString());
        var containingTypes = syntax.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Identifier.ValueText);
        return string.Join(
            '.', namespaces.Concat(containingTypes)
                .Append(syntax.Identifier.ValueText));
    }

    private static string DetermineSyntaxRole(TypeDeclarationSyntax syntax)
    {
        var name = syntax.Identifier.ValueText;
        var bases = syntax.BaseList?.Types
            .Select(item => item.Type.ToString()).ToList() ?? [];
        if (name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ||
            bases.Any(item =>
                item.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ||
                item.EndsWith("ControllerBase", StringComparison.OrdinalIgnoreCase)))
            return GraphRoles.Controller;
        if (name.EndsWith("Repository", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("QR", StringComparison.Ordinal))
            return GraphRoles.Repository;
        if (name.EndsWith("Service", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("BZ", StringComparison.OrdinalIgnoreCase) ||
            bases.Any(item => item.EndsWith(
                "Service", StringComparison.OrdinalIgnoreCase)))
            return GraphRoles.BusinessService;
        if (name.EndsWith("Report", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("ReportPlugin", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.ReportPlugin;
        if (name.EndsWith("Entity", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Model", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Dto", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.DataModel;
        if (name.Contains("Migration", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.Migration;
        return GraphRoles.Type;
    }

    private static string DetermineSyntaxTechnology(TypeDeclarationSyntax syntax)
    {
        var attributes = syntax.AttributeLists.SelectMany(item => item.Attributes)
            .Select(AttributeName).ToList();
        var bases = syntax.BaseList?.Types.Select(item => item.Type.ToString())
            .ToList() ?? [];
        if (attributes.Any(item => item.Equals(
                "ApiController", StringComparison.OrdinalIgnoreCase)) ||
            bases.Any(item => item.EndsWith(
                "ControllerBase", StringComparison.OrdinalIgnoreCase)))
            return "aspnet-core";
        if (syntax.Identifier.ValueText.EndsWith(
                "Controller", StringComparison.OrdinalIgnoreCase) ||
            bases.Any(item => item.EndsWith(
                "Controller", StringComparison.OrdinalIgnoreCase)))
            return "aspnet-mvc";
        return "dotnet";
    }

    private static Dictionary<INamedTypeSymbol, DeclaredTypeInfo> CollectDeclaredTypes(
        CSharpCompilation compilation,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        string root,
        CancellationToken cancellationToken)
    {
        var candidates = new ConcurrentBag<TypeDeclarationCandidate>();
        Parallel.ForEach(
            syntaxTrees,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 12),
            },
            tree =>
            {
                var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                var relativePath = RelativePath(root, tree.FilePath);
                var declarations = tree.GetRoot(cancellationToken)
                    .DescendantNodes()
                    .OfType<TypeDeclarationSyntax>();
                foreach (var declaration in declarations)
                {
                    if (model.GetDeclaredSymbol(
                            declaration, cancellationToken) is not INamedTypeSymbol symbol)
                        continue;
                    var span = declaration.GetLocation().GetLineSpan();
                    candidates.Add(new TypeDeclarationCandidate(
                        symbol.OriginalDefinition,
                        declaration,
                        model,
                        relativePath,
                        span.StartLinePosition.Line + 1,
                        span.EndLinePosition.Line + 1));
                }
            });

        var result = new Dictionary<INamedTypeSymbol, DeclaredTypeInfo>(SymbolEqualityComparer.Default);
        foreach (var candidate in candidates
                     .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                     .ThenBy(item => item.StartLine))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.TryGetValue(candidate.Symbol, out var info))
            {
                var qualifiedName =
                    candidate.Symbol.ToDisplayString(TypeDisplayFormat);
                info = new DeclaredTypeInfo(
                    candidate.Symbol,
                    GraphIdentity.CSharpCode(qualifiedName),
                    qualifiedName,
                    DetermineRole(candidate.Symbol));
                result.Add(candidate.Symbol, info);
            }
            info.Declarations.Add(new TypeDeclarationInfo(
                candidate.Syntax,
                candidate.SemanticModel,
                candidate.RelativePath,
                candidate.StartLine,
                candidate.EndLine));
        }
        return result;
    }

    private static GraphNode CreateCodeNode(DeclaredTypeInfo info)
    {
        var declarations = info.Declarations
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.StartLine)
            .ToList();
        var primary = declarations[0];
        var methodSignatures = declarations
            .SelectMany(item => item.Syntax.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Modifiers.Any(SyntaxKind.PublicKeyword))
                .Select(method => MethodSignature(method)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaximumEvidenceItems)
            .ToList();
        var baseTypes = GetBaseTypes(info.Symbol);
        var dependencyTypes = declarations
            .SelectMany(item => item.Syntax.Members.OfType<ConstructorDeclarationSyntax>())
            .SelectMany(constructor => constructor.ParameterList.Parameters)
            .Select(parameter => parameter.Type?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaximumEvidenceItems)
            .ToList();
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["declarationKind"] = info.Symbol.TypeKind.ToString(),
            ["methods"] = string.Join(" | ", methodSignatures),
            ["baseTypes"] = string.Join(" | ", baseTypes),
            ["constructorDependencies"] = string.Join(" | ", dependencyTypes),
        };
        var evidence = declarations.Select(declaration => new GraphEvidence(
                GraphEvidenceSource.Compiler,
                GraphConfidence.Resolved,
                declaration.RelativePath,
                "由 Roslyn 型別符號解析，方法與繼承細節保留於 evidence，不建立細粒度節點。",
                declaration.StartLine,
                declaration.EndLine,
                details))
            .ToList();
        var aliases = new[]
            {
                info.Symbol.Name,
                RemoveCommonSuffix(info.Symbol.Name),
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var searchableText = string.Join(' ',
            new[] { info.QualifiedName, info.Symbol.Name, info.Role }
                .Concat(methodSignatures)
                .Concat(baseTypes)
                .Concat(dependencyTypes));

        return new GraphNode(
            info.NodeId,
            GraphNodeKind.Code,
            info.Role,
            info.Symbol.Name,
            searchableText,
            "csharp",
            DetermineTechnology(info.Symbol),
            "active",
            aliases,
            primary.RelativePath,
            primary.StartLine,
            primary.EndLine,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["qualifiedName"] = info.QualifiedName,
                ["partialDeclarationCount"] = declarations.Count.ToString(),
            },
            evidence);
    }

    private static void ExtractControllerEntries(
        GraphFragment fragment,
        IReadOnlyDictionary<INamedTypeSymbol, DeclaredTypeInfo> declaredTypes)
    {
        foreach (var info in declaredTypes.Values
                     .Where(info => info.Role == GraphRoles.Controller)
                     .OrderBy(info => info.NodeId, StringComparer.Ordinal))
        {
            var controllerName = RemoveControllerSuffix(info.Symbol.Name);
            foreach (var declaration in info.Declarations)
            {
                var classRoutes = AttributeArguments(
                    declaration.Syntax.AttributeLists, "Route", "RoutePrefix");
                var actions = declaration.Syntax.Members.OfType<MethodDeclarationSyntax>()
                    .Where(IsControllerAction);
                foreach (var action in actions)
                {
                    var actionName = GetActionName(action);
                    var entryId = GraphIdentity.WebEntry(controllerName, actionName);
                    var span = action.GetLocation().GetLineSpan();
                    var startLine = span.StartLinePosition.Line + 1;
                    var endLine = span.EndLinePosition.Line + 1;
                    var methodRoutes = AttributeArguments(
                        action.AttributeLists,
                        "Route", "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch");
                    var routeAliases = BuildRouteAliases(controllerName, actionName, classRoutes, methodRoutes);
                    var details = new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["method"] = MethodSignature(action),
                        ["httpVerbs"] = string.Join(" | ", HttpVerbs(action)),
                        ["routes"] = string.Join(" | ", routeAliases),
                    };
                    var evidence = new GraphEvidence(
                        GraphEvidenceSource.Framework,
                        GraphConfidence.Resolved,
                        declaration.RelativePath,
                        "由 Controller 公開 Action、ActionName 與 Route Attribute 解析 HTTP 入口。",
                        startLine,
                        endLine,
                        details);
                    fragment.Nodes.Add(new GraphNode(
                        entryId,
                        GraphNodeKind.EntryPoint,
                        GraphRoles.ControllerAction,
                        $"{controllerName}/{actionName}",
                        string.Join(' ', routeAliases.Prepend($"{controllerName} {actionName}")),
                        "csharp",
                        DetermineTechnology(info.Symbol),
                        "active",
                        routeAliases,
                        declaration.RelativePath,
                        startLine,
                        endLine,
                        new SortedDictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["controller"] = controllerName,
                            ["action"] = actionName,
                        },
                        [evidence]));
                    fragment.Edges.Add(CreateEdge(
                        entryId,
                        GraphEdgeKind.Handles,
                        info.NodeId,
                        evidence with
                        {
                            Reason = "由 Controller Action 的宣告位置確認此入口由該 Controller 型別處理。",
                        }));
                }
            }
        }
    }

    /// <summary>
    /// 傳統排程框架以靜態 <c>TaskName</c> 將 tblScheduleTask.Name 綁到實作型別。
    /// TaskName 本身是跨資料庫與程式碼的共享入口，因此只建立一個 EntryPoint，
    /// 再以 HANDLES 指向可修改的 type-level Code；不建立 property／field 節點。
    /// </summary>
    private static void ExtractScheduledTaskEntries(
        GraphFragment fragment,
        IReadOnlyDictionary<INamedTypeSymbol, DeclaredTypeInfo> declaredTypes)
    {
        foreach (var info in declaredTypes.Values.OrderBy(item => item.NodeId, StringComparer.Ordinal))
        {
            foreach (var declaration in info.Declarations
                         .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                         .ThenBy(item => item.StartLine))
            {
                foreach (var candidate in ScheduledTaskNames(declaration))
                {
                    var taskName = candidate.Name.Trim();
                    if (taskName.Length == 0) continue;

                    var span = candidate.Member.GetLocation().GetLineSpan();
                    var startLine = span.StartLinePosition.Line + 1;
                    var endLine = span.EndLinePosition.Line + 1;
                    var taskId = GraphIdentity.TaskEntry(taskName);
                    var details = new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["taskName"] = taskName,
                        ["implementationType"] = info.QualifiedName,
                        ["declaration"] = candidate.Member is PropertyDeclarationSyntax
                            ? "static-property"
                            : "static-field",
                    };
                    var evidence = new GraphEvidence(
                        GraphEvidenceSource.Framework,
                        GraphConfidence.Resolved,
                        declaration.RelativePath,
                        "由排程實作型別的靜態 TaskName 常數解析共享任務名稱，並連回實際可修改的 C# 型別。",
                        startLine,
                        endLine,
                        details);
                    var aliases = new[]
                        {
                            taskName,
                            info.Symbol.Name,
                            RemoveCommonSuffix(info.Symbol.Name),
                        }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    fragment.Nodes.Add(new GraphNode(
                        taskId,
                        GraphNodeKind.EntryPoint,
                        GraphRoles.ScheduledTask,
                        taskName,
                        $"{taskName} {info.Symbol.Name} {info.QualifiedName} 排程任務 Scheduled Task",
                        "csharp",
                        "dotnet-schedule-task",
                        "active",
                        aliases,
                        declaration.RelativePath,
                        startLine,
                        endLine,
                        new SortedDictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["taskName"] = taskName,
                        },
                        [evidence]));
                    fragment.Edges.Add(CreateEdge(
                        taskId,
                        GraphEdgeKind.Handles,
                        info.NodeId,
                        evidence with
                        {
                            Reason = "由靜態 TaskName 的宣告位置確認此排程入口由該 C# 型別處理。",
                        }));
                }
            }
        }
    }

    private static IEnumerable<ScheduledTaskName> ScheduledTaskNames(TypeDeclarationInfo declaration)
    {
        foreach (var property in declaration.Syntax.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!property.Identifier.ValueText.Equals("TaskName", StringComparison.OrdinalIgnoreCase) ||
                !property.Modifiers.Any(SyntaxKind.StaticKeyword))
                continue;

            var expressions = new List<ExpressionSyntax>();
            if (property.ExpressionBody?.Expression is { } expressionBody)
                expressions.Add(expressionBody);
            if (property.AccessorList?.Accessors.FirstOrDefault(
                    accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration)) is { } getter)
            {
                if (getter.ExpressionBody?.Expression is { } getterExpression)
                    expressions.Add(getterExpression);
                expressions.AddRange(getter.Body?.Statements
                    .OfType<ReturnStatementSyntax>()
                    .Select(statement => statement.Expression)
                    .OfType<ExpressionSyntax>() ?? []);
            }

            foreach (var expression in expressions)
                if (StringConstant(declaration.SemanticModel, expression) is { Length: > 0 } value)
                    yield return new ScheduledTaskName(value, property);
        }

        foreach (var field in declaration.Syntax.Members.OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                !field.Modifiers.Any(SyntaxKind.ConstKeyword))
                continue;
            foreach (var variable in field.Declaration.Variables)
            {
                if (!variable.Identifier.ValueText.Equals("TaskName", StringComparison.OrdinalIgnoreCase) ||
                    variable.Initializer?.Value is not { } expression)
                    continue;
                if (StringConstant(declaration.SemanticModel, expression) is { Length: > 0 } value)
                    yield return new ScheduledTaskName(value, field);
            }
        }
    }

    private static string? StringConstant(SemanticModel model, ExpressionSyntax expression)
    {
        var constant = model.GetConstantValue(expression);
        return constant.HasValue && constant.Value is string value
            ? value
            : expression is LiteralExpressionSyntax literal &&
              literal.IsKind(SyntaxKind.StringLiteralExpression)
                ? literal.Token.ValueText
                : null;
    }

    private static int ExtractTypeCalls(
        GraphFragment fragment,
        CSharpCompilation compilation,
        IReadOnlyDictionary<SyntaxTree, IReadOnlyDictionary<(int Start, int Length), DeclaredTypeInfo>>
            callOwnersByTree,
        string root,
        IReadOnlyDictionary<INamedTypeSymbol, DeclaredTypeInfo> declaredTypes,
        IReadOnlySet<string> declaredMethodNames,
        CancellationToken cancellationToken)
    {
        var fileResults = new ConcurrentBag<TypeCallFileResult>();
        Parallel.ForEach(
            callOwnersByTree,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 12),
            },
            item =>
            {
                var tree = item.Key;
                var owners = item.Value;
                var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                var relativePath = RelativePath(root, tree.FilePath);
                var syntaxRoot = tree.GetRoot(cancellationToken);
                var localEdges =
                    new Dictionary<(string Source, string Target), List<GraphEvidence>>();
                var unresolved = 0;
                foreach (var invocation in syntaxRoot.DescendantNodes()
                             .OfType<InvocationExpressionSyntax>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceDeclaration =
                        invocation.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                    if (sourceDeclaration is null ||
                        !owners.TryGetValue(
                            (sourceDeclaration.SpanStart, sourceDeclaration.Span.Length),
                            out var sourceInfo))
                        continue;

                    // 只可能建立「專案內 type → type」CALLS。若 invocation 名稱未在
                    // 任何專案型別宣告，Roslyn 最終只會解析到 BCL／外部套件或無法解析，
                    // 而這些本來就不會產生 domain edge；先用完整 declaration set
                    // 做 exact prefilter，可省下大量 GetSymbolInfo，且不會漏掉專案內呼叫。
                    var invokedName = InvokedMethodName(invocation.Expression);
                    if (invokedName is null ||
                        !declaredMethodNames.Contains(invokedName))
                        continue;

                    var symbolInfo = model.GetSymbolInfo(invocation, cancellationToken);
                    var calledMethod = symbolInfo.Symbol as IMethodSymbol ??
                        symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (calledMethod is null)
                    {
                        unresolved++;
                        continue;
                    }
                    var targetSymbol = calledMethod.ContainingType?.OriginalDefinition;
                    if (targetSymbol is null ||
                        !declaredTypes.TryGetValue(targetSymbol, out var targetInfo) ||
                        string.Equals(
                            sourceInfo.NodeId, targetInfo.NodeId,
                            StringComparison.Ordinal))
                        continue;

                    var ownerMethod =
                        invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                    var span = invocation.GetLocation().GetLineSpan();
                    var details = new SortedDictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["sourceMethod"] =
                            ownerMethod?.Identifier.ValueText ?? "(initializer)",
                        ["targetMethod"] = calledMethod.Name,
                        // receiver 可能含 email/token literal；只保存 syntax kind 與 method pair。
                        ["syntaxKind"] = invocation.Expression.Kind().ToString(),
                    };
                    var evidence = new GraphEvidence(
                        GraphEvidenceSource.Compiler,
                        symbolInfo.Symbol is null
                            ? GraphConfidence.Heuristic
                            : GraphConfidence.Resolved,
                        relativePath,
                        "由 Roslyn 呼叫符號解析後聚合為型別到型別的 CALLS；方法配對保留於 evidence。",
                        span.StartLinePosition.Line + 1,
                        span.EndLinePosition.Line + 1,
                        details);
                    var key = (sourceInfo.NodeId, targetInfo.NodeId);
                    if (!localEdges.TryGetValue(key, out var values))
                    {
                        values = [];
                        localEdges.Add(key, values);
                    }
                    if (values.Count < MaximumEvidenceItems)
                        values.Add(evidence);
                }
                fileResults.Add(new TypeCallFileResult(
                    relativePath, localEdges, unresolved));
            });

        var edgeEvidence =
            new Dictionary<(string Source, string Target), List<GraphEvidence>>();
        var unresolvedInvocations = 0;
        foreach (var result in fileResults.OrderBy(
                     result => result.RelativePath, StringComparer.Ordinal))
        {
            unresolvedInvocations += result.UnresolvedInvocations;
            foreach (var (key, evidence) in result.Edges
                         .OrderBy(item => item.Key.Source, StringComparer.Ordinal)
                         .ThenBy(item => item.Key.Target, StringComparer.Ordinal))
            {
                if (!edgeEvidence.TryGetValue(key, out var values))
                {
                    values = [];
                    edgeEvidence.Add(key, values);
                }
                values.AddRange(evidence.Take(
                    Math.Max(0, MaximumEvidenceItems - values.Count)));
            }
        }

        foreach (var (key, evidence) in edgeEvidence.OrderBy(item => item.Key.Source, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Target, StringComparer.Ordinal))
            fragment.Edges.Add(new GraphEdge(
                GraphIdentity.Edge(key.Source, GraphEdgeKind.Calls, key.Target),
                key.Source,
                GraphEdgeKind.Calls,
                key.Target,
                evidence));
        return unresolvedInvocations;
    }

    private static string? ReceiverTypeName(
        InvocationExpressionSyntax invocation,
        TypeDeclarationSyntax sourceDeclaration)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax call)
            return null;
        if (call.Expression is ObjectCreationExpressionSyntax creation)
            return SimpleTypeName(creation.Type);

        var receiverName = RightmostIdentifier(call.Expression);
        if (receiverName is null) return null;
        var member = invocation.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        if (member is not null)
        {
            var parameter = member.DescendantNodes()
                .OfType<ParameterSyntax>()
                .LastOrDefault(item =>
                    item.SpanStart < invocation.SpanStart &&
                    item.Identifier.ValueText.Equals(receiverName, StringComparison.Ordinal));
            if (parameter?.Type is not null) return SimpleTypeName(parameter.Type);
            var local = member.DescendantNodes()
                .OfType<VariableDeclarationSyntax>()
                .Where(item => item.SpanStart < invocation.SpanStart &&
                               item.Variables.Any(variable =>
                                   variable.Identifier.ValueText.Equals(
                                       receiverName, StringComparison.Ordinal)))
                .LastOrDefault();
            if (local is not null) return SimpleTypeName(local.Type);
        }
        var field = sourceDeclaration.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(item => item.Declaration.Variables.Any(variable =>
                variable.Identifier.ValueText.Equals(receiverName, StringComparison.Ordinal)));
        if (field is not null) return SimpleTypeName(field.Declaration.Type);
        var property = sourceDeclaration.Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(item => item.Identifier.ValueText.Equals(
                receiverName, StringComparison.Ordinal));
        if (property is not null) return SimpleTypeName(property.Type);

        // TypeName.StaticMethod()：receiver 本身就是專案內型別名稱。
        return receiverName;
    }

    private static string? RightmostIdentifier(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null,
        };

    private static string? SimpleTypeName(TypeSyntax type) =>
        type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            QualifiedNameSyntax qualified => SimpleTypeName(qualified.Right),
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            NullableTypeSyntax nullable => SimpleTypeName(nullable.ElementType),
            ArrayTypeSyntax array => SimpleTypeName(array.ElementType),
            _ => null,
        };

    /// <summary>
    /// CALLS 只保留能協助修改定位的協調型程式碼，不重建整個 IDE call hierarchy。
    /// 所有 type node 仍會建立；只有低價值的演算法／DTO／value-object 內部呼叫不做昂貴
    /// semantic binding。owner 直接沿用宣告收集階段的結果，避免第二次 GetDeclaredSymbol。
    /// </summary>
    private static IReadOnlyDictionary<SyntaxTree, IReadOnlyDictionary<(int Start, int Length), DeclaredTypeInfo>>
        BuildCallOwnersByTree(
            IReadOnlyDictionary<INamedTypeSymbol, DeclaredTypeInfo> declaredTypes)
    {
        var result =
            new Dictionary<SyntaxTree, Dictionary<(int Start, int Length), DeclaredTypeInfo>>();
        // 小型專案完整保留 type-level CALLS；大型 monorepo 才啟用 bounded call path，
        // 避免為數千個演算法／DTO 型別建立幾乎不會用於變更定位的 IDE hierarchy。
        var candidateInfos = declaredTypes.Count <= 500
            ? declaredTypes.Values
            : declaredTypes.Values.Where(IsCallPathType);
        foreach (var info in candidateInfos)
        {
            foreach (var declaration in info.Declarations)
            {
                var tree = declaration.Syntax.SyntaxTree;
                if (!result.TryGetValue(tree, out var owners))
                {
                    owners = [];
                    result.Add(tree, owners);
                }
                owners[(declaration.Syntax.SpanStart, declaration.Syntax.Span.Length)] = info;
            }
        }
        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlyDictionary<(int Start, int Length), DeclaredTypeInfo>)item.Value);
    }

    private static bool IsCallPathType(DeclaredTypeInfo info)
    {
        if (info.Role is GraphRoles.Controller or GraphRoles.BusinessService or
            GraphRoles.Repository or GraphRoles.ReportPlugin or GraphRoles.Migration)
            return true;
        var name = info.Symbol.Name;
        return new[]
        {
            "DAL", "DAO", "Handler", "Manager", "Provider", "Utility",
            "Task", "Job", "Schedule", "Workflow", "Processor", "Adapter", "Factory",
        }.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string? InvokedMethodName(ExpressionSyntax expression) =>
        expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => null,
        };

    private static ImmutableArray<MetadataReference> ResolveFrameworkReferences()
    {
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedAssemblies)) return [];
        // GraphRAG 只解析同一 source compilation 內的 type-to-type CALLS；
        // Controller/Route 等框架事實由 syntax 與 attribute 抽取，不需要把整個
        // ASP.NET/EF/AgentService runtime 的數百個 assembly 載入大型 monorepo compilation。
        // 核心 BCL 足以讓 Roslyn 正確處理常見語言型別、泛型、LINQ 與 Task。
        var coreReferences = new HashSet<string>(
            [
                "System.Private.CoreLib.dll",
                "System.Runtime.dll",
                "netstandard.dll",
                "System.Collections.dll",
                "System.Collections.Concurrent.dll",
                "System.Linq.dll",
                "System.Threading.dll",
                "System.Threading.Tasks.dll",
                "System.ComponentModel.Primitives.dll",
                "Microsoft.CSharp.dll",
            ],
            StringComparer.OrdinalIgnoreCase);
        return trustedAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => coreReferences.Contains(Path.GetFileName(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    private static bool IsControllerAction(MethodDeclarationSyntax method)
    {
        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            method.AttributeLists.SelectMany(list => list.Attributes)
                .Any(attribute => AttributeName(attribute).Equals("NonAction", StringComparison.OrdinalIgnoreCase)))
            return false;
        return method.ExplicitInterfaceSpecifier is null;
    }

    private static string GetActionName(MethodDeclarationSyntax method)
    {
        var actionName = method.AttributeLists.SelectMany(list => list.Attributes)
            .FirstOrDefault(attribute =>
                AttributeName(attribute).Equals("ActionName", StringComparison.OrdinalIgnoreCase));
        return FirstStringArgument(actionName) ?? method.Identifier.ValueText;
    }

    private static IReadOnlyList<string> BuildRouteAliases(
        string controller,
        string action,
        IReadOnlyList<string> classRoutes,
        IReadOnlyList<string> methodRoutes)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"/{controller}/{action}",
        };
        foreach (var methodRoute in methodRoutes.DefaultIfEmpty(string.Empty))
        {
            foreach (var classRoute in classRoutes.DefaultIfEmpty(string.Empty))
            {
                var route = string.Join('/',
                    new[] { classRoute, methodRoute }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim('/')));
                if (route.Length == 0) continue;
                route = route
                    .Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
                    .Replace("[action]", action, StringComparison.OrdinalIgnoreCase);
                result.Add($"/{route.TrimStart('/')}");
            }
        }
        return result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> AttributeArguments(
        SyntaxList<AttributeListSyntax> lists,
        params string[] names) =>
        lists.SelectMany(list => list.Attributes)
            .Where(attribute => names.Contains(AttributeName(attribute), StringComparer.OrdinalIgnoreCase))
            .Select(FirstStringArgument)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> HttpVerbs(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(list => list.Attributes)
            .Select(AttributeName)
            .Where(name => name.StartsWith("Http", StringComparison.OrdinalIgnoreCase))
            .Select(name => name[4..].ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;
    }

    private static string? FirstStringArgument(AttributeSyntax? attribute)
    {
        var expression = attribute?.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        return expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) =>
                literal.Token.ValueText,
            _ => null,
        };
    }

    private static string DetermineRole(INamedTypeSymbol symbol)
    {
        var name = symbol.Name;
        if (name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ||
            InheritsNamed(symbol, "Controller") ||
            InheritsNamed(symbol, "ControllerBase"))
            return GraphRoles.Controller;
        if (name.EndsWith("Repository", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("QR", StringComparison.Ordinal))
            return GraphRoles.Repository;
        if (name.EndsWith("Service", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("BZ", StringComparison.OrdinalIgnoreCase) ||
            symbol.AllInterfaces.Any(item => item.Name.EndsWith("Service", StringComparison.OrdinalIgnoreCase)))
            return GraphRoles.BusinessService;
        if (name.EndsWith("Report", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("ReportPlugin", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.ReportPlugin;
        if (name.EndsWith("Entity", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Model", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Dto", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.DataModel;
        if (name.Contains("Migration", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.Migration;
        return GraphRoles.Type;
    }

    private static string DetermineTechnology(INamedTypeSymbol symbol)
    {
        var attributes = symbol.GetAttributes().Select(item => item.AttributeClass?.Name ?? string.Empty).ToList();
        if (attributes.Any(name => name.StartsWith("ApiController", StringComparison.OrdinalIgnoreCase)) ||
            InheritsNamed(symbol, "ControllerBase"))
            return "aspnet-core";
        if (symbol.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ||
            InheritsNamed(symbol, "Controller"))
            return "aspnet-mvc";
        return "dotnet";
    }

    private static bool InheritsNamed(INamedTypeSymbol symbol, string name)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
            if (current.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static IReadOnlyList<string> GetBaseTypes(INamedTypeSymbol symbol)
    {
        var values = new List<string>();
        if (symbol.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            values.Add(baseType.ToDisplayString(TypeDisplayFormat));
        values.AddRange(symbol.Interfaces.Select(item => item.ToDisplayString(TypeDisplayFormat)));
        return values.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(MaximumEvidenceItems)
            .ToList();
    }

    private static string MethodSignature(MethodDeclarationSyntax method) =>
        $"{method.ReturnType} {method.Identifier.ValueText}(" +
        string.Join(", ", method.ParameterList.Parameters.Select(parameter =>
            $"{parameter.Type} {parameter.Identifier.ValueText}")) + ")";

    private static string RemoveCommonSuffix(string value)
    {
        foreach (var suffix in new[] { "Controller", "Repository", "Service", "ReportPlugin", "Report", "Model", "Dto" })
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && value.Length > suffix.Length)
                return value[..^suffix.Length];
        return value;
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

    private static bool ShouldIgnore(string path, string root)
    {
        var relative = RelativePath(root, path);
        var segments = relative.Split('/');
        if (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                                    segment.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                    segment.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                                    segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                                    segment.Equals("vendor", StringComparison.OrdinalIgnoreCase)))
            return true;
        return relative.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
               relative.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               relative.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
               relative.Contains("/migrations/", StringComparison.OrdinalIgnoreCase) &&
               relative.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 大型 repository 僅保留可能參與畫面、流程、登入、服務與批次協調的 C# 檔案。
    /// 這是效能與召回率的保守白名單，不代表檔案已被確認屬於某項業務功能；
    /// SQL 與前端 extractor 仍各自處理其完整來源範圍。
    /// </summary>
    /// <param name="root">專案根目錄，用來產生不含外部路徑的相對名稱。</param>
    /// <param name="path">待判斷的 C# 實體檔案路徑。</param>
    /// <returns>應納入大型專案 C# type graph 時為 true。</returns>
    internal static bool IsLargeRepositoryCallPathFile(string root, string path)
    {
        var relative = RelativePath(root, path);
        var searchable = relative.Replace('/', ' ');
        return new[]
        {
            "controller", "service", "business", "report",
            "schedule", "task", "workflow", "handler", "maintain", "confirm",
            "login", "auth", "password", "account", "security", "rmbz",
        }.Any(marker => searchable.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksGenerated(string source)
    {
        var prefix = source.AsSpan(0, Math.Min(source.Length, 2048));
        return prefix.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase) ||
               prefix.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string RelativePath(string root, string path) =>
        GraphIdentity.NormalizePath(Path.GetRelativePath(root, path));

    private static string BoundText(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

    private sealed class DeclaredTypeInfo(
        INamedTypeSymbol symbol,
        string nodeId,
        string qualifiedName,
        string role)
    {
        internal INamedTypeSymbol Symbol { get; } = symbol;
        internal string NodeId { get; } = nodeId;
        internal string QualifiedName { get; } = qualifiedName;
        internal string Role { get; } = role;
        internal List<TypeDeclarationInfo> Declarations { get; } = [];
    }

    private sealed record TypeDeclarationInfo(
        TypeDeclarationSyntax Syntax,
        SemanticModel SemanticModel,
        string RelativePath,
        int StartLine,
        int EndLine);

    private sealed record TypeDeclarationCandidate(
        INamedTypeSymbol Symbol,
        TypeDeclarationSyntax Syntax,
        SemanticModel SemanticModel,
        string RelativePath,
        int StartLine,
        int EndLine);

    private sealed record TypeCallFileResult(
        string RelativePath,
        IReadOnlyDictionary<
            (string Source, string Target),
            List<GraphEvidence>> Edges,
        int UnresolvedInvocations);

    private sealed class SyntaxDeclaredTypeInfo(
        string nodeId,
        string qualifiedName,
        string name,
        string role)
    {
        internal string NodeId { get; } = nodeId;
        internal string QualifiedName { get; } = qualifiedName;
        internal string Name { get; } = name;
        internal string Role { get; } = role;
        internal List<SyntaxTypeDeclaration> Declarations { get; } = [];
    }

    private sealed record SyntaxTypeDeclaration(
        TypeDeclarationSyntax Syntax,
        string RelativePath,
        int StartLine,
        int EndLine);

    private sealed record ScheduledTaskName(string Name, MemberDeclarationSyntax Member);
}
