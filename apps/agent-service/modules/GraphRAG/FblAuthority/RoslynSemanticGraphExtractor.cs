using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>保存 Roslyn semantic graph 與不阻擋發布的載入診斷。</summary>
public sealed record RoslynSemanticExtractionResult(
    GraphDocument Document,
    IReadOnlyList<PreflightIssue> Issues);

/// <summary>
/// 將 ParallelExtractor 的 Solution／Project／File／Type／Method 語意抽取演算法，
/// 轉換為 Modern Wingman 的強型別 <see cref="GraphDocument"/>。
/// 本類別只讀取原始碼，不接觸資料庫與 Neo4j；發布仍由既有 staging/promotion 流程負責。
/// </summary>
public sealed class RoslynSemanticGraphExtractor
{
    private const int MaximumParallelProjects = 4;
    private static readonly Regex SolutionProjectPattern = new(
        "^Project\\(\\\"[^\\\"]+\\\"\\)\\s*=\\s*\\\"(?<name>[^\\\"]+)\\\",\\s*\\\"(?<path>[^\\\"]+\\.csproj)\\\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly TimeSpan SolutionLoadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProjectCompilationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DocumentSyntaxTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 探索專案根目錄中的主要 Solution，使用 MSBuildWorkspace 建立語意圖；
    /// 若舊式 Solution 無法載入，會降級成 repository compilation，既有 FBL 圖仍可繼續發布。
    /// </summary>
    public async Task<RoslynSemanticExtractionResult> ExtendAsync(
        GraphDocument document,
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var rootPath = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"原始碼根目錄不存在：{rootPath}");
        }

        var diagnostics = new ConcurrentBag<string>();
        var solutionPath = DiscoverPrimarySolution(rootPath);
        if (solutionPath is not null)
        {
            try
            {
                MsBuildRuntimeRegistration.EnsureRegistered();
                return await ExtractWithMsBuildAsync(
                        document,
                        rootPath,
                        solutionPath,
                        diagnostics,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                diagnostics.Add($"MSBuild solution 載入失敗：{SanitizeDiagnostic(exception.Message)}");
            }
        }
        else
        {
            diagnostics.Add("專案根目錄找不到 .sln 或 .slnx，改用 repository compilation。");
        }

        try
        {
            using var fallback = await CreateRepositorySolutionAsync(
                    rootPath,
                    solutionPath,
                    diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
            return await ExtractSolutionAsync(
                    document,
                    fallback.Solution,
                    rootPath,
                    solutionPath is null ? "repository" : Path.GetFileNameWithoutExtension(solutionPath),
                    solutionPath is null ? null : RelativePath(rootPath, solutionPath),
                    diagnostics,
                    degraded: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Semantic graph 是 domain authority graph 的加強層；降級失敗不得破壞既有索引能力。
            var messages = diagnostics
                .Append($"Repository compilation 失敗：{SanitizeDiagnostic(exception.Message)}")
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToArray();
            return new RoslynSemanticExtractionResult(
                document,
                [CreateDegradedIssue(messages)]);
        }
    }

    /// <summary>由已建立的 Roslyn Solution 抽取圖，供整合測試與不依賴 MSBuild 的呼叫端使用。</summary>
    public async Task<RoslynSemanticExtractionResult> ExtendSolutionAsync(
        GraphDocument document,
        Solution solution,
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        return await ExtractSolutionAsync(
                document,
                solution,
                Path.GetFullPath(sourceRoot),
                solution.FilePath is null ? "solution" : Path.GetFileNameWithoutExtension(solution.FilePath),
                solution.FilePath is null ? null : RelativePath(sourceRoot, solution.FilePath),
                new ConcurrentBag<string>(),
                degraded: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>MSBuildWorkspace 必須在 Locator 註冊完成後才建立，因此獨立成不內嵌的方法。</summary>
    private async Task<RoslynSemanticExtractionResult> ExtractWithMsBuildAsync(
        GraphDocument document,
        string rootPath,
        string solutionPath,
        ConcurrentBag<string> diagnostics,
        CancellationToken cancellationToken)
    {
        using var workspace = MSBuildWorkspace.Create();
        workspace.SkipUnrecognizedProjects = true;
        workspace.LoadMetadataForReferencedProjects = false;
        workspace.WorkspaceFailed += (_, eventArgs) =>
            diagnostics.Add(SanitizeDiagnostic(eventArgs.Diagnostic.Message));
        using var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loadCancellation.CancelAfter(SolutionLoadTimeout);
        var loadTask = workspace.OpenSolutionAsync(
            solutionPath,
            progress: null,
            cancellationToken: loadCancellation.Token);
        Solution solution;
        try
        {
            solution = await loadTask
                .WaitAsync(SolutionLoadTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MSBuild solution 載入超過 {SolutionLoadTimeout.TotalMinutes:0} 分鐘。");
        }
        catch (TimeoutException)
        {
            loadCancellation.Cancel();
            _ = loadTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException(
                $"MSBuild solution 載入超過 {SolutionLoadTimeout.TotalMinutes:0} 分鐘。");
        }
        return await ExtractSolutionAsync(
                document,
                solution,
                rootPath,
                Path.GetFileNameWithoutExtension(solutionPath),
                RelativePath(rootPath, solutionPath),
                diagnostics,
                degraded: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>把 Solution 內各 Project 平行抽取成 fragment，再依 project key 確定性合併。</summary>
    private async Task<RoslynSemanticExtractionResult> ExtractSolutionAsync(
        GraphDocument document,
        Solution solution,
        string rootPath,
        string solutionName,
        string? solutionRelativePath,
        ConcurrentBag<string> diagnostics,
        bool degraded,
        CancellationToken cancellationToken)
    {
        var maps = RoslynProjectMaps.Create(solution, rootPath);
        var builder = GraphDocumentBuilder.FromDocument(document, document.Metadata.BuildStage);
        var solutionKey = RoslynGraphKeys.Solution(solutionRelativePath ?? solutionName);
        builder.AddNode(
            GraphNodeKind.Solution,
            solutionKey,
            new Dictionary<string, object?>
            {
                ["name"] = solutionName,
                ["solution_file"] = solutionRelativePath,
                ["project_count"] = maps.Projects.Count,
                ["semantic_mode"] = degraded ? "repository-fallback" : "msbuild",
            });

        foreach (var info in maps.Projects.Values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.AddNode(
                GraphNodeKind.Project,
                info.Key,
                new Dictionary<string, object?>
                {
                    ["name"] = info.Project.Name,
                    ["project_file"] = info.RelativeProjectPath,
                    ["language"] = info.Project.Language,
                    ["assembly_name"] = info.Project.AssemblyName ?? info.Project.Name,
                });
            builder.AddRelationship(
                GraphRelationshipKind.ContainsProject,
                solutionKey,
                info.Key,
                ProjectEvidence(info.RelativeProjectPath));

            foreach (var reference in info.Project.ProjectReferences)
            {
                if (!maps.Projects.TryGetValue(reference.ProjectId, out var target))
                {
                    continue;
                }

                builder.AddRelationship(
                    GraphRelationshipKind.ReferencesProject,
                    info.Key,
                    target.Key,
                    ProjectEvidence(info.RelativeProjectPath));
            }
        }

        var fragments = new ConcurrentBag<SemanticGraphFragment>();
        await Parallel.ForEachAsync(
            maps.Projects.Values,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, MaximumParallelProjects),
            },
            async (projectInfo, token) =>
            {
                var fragment = await ExtractProjectAsync(projectInfo, maps, rootPath, diagnostics, token)
                    .ConfigureAwait(false);
                fragments.Add(fragment);
            }).ConfigureAwait(false);

        foreach (var fragment in fragments.OrderBy(item => item.ProjectKey, StringComparer.Ordinal))
        {
            foreach (var node in fragment.Nodes
                         .OrderBy(item => item.Key, StringComparer.Ordinal)
                         .ThenBy(item => item.Kind))
            {
                builder.AddNode(node.Kind, node.Key, node.Properties);
            }

            foreach (var edge in fragment.Relationships
                         .OrderBy(item => item.SourceKey, StringComparer.Ordinal)
                         .ThenBy(item => item.Kind)
                         .ThenBy(item => item.TargetKey, StringComparer.Ordinal)
                         .ThenBy(item => item.Evidence.SourceFile, StringComparer.Ordinal)
                         .ThenBy(item => item.Evidence.SourceLine))
            {
                builder.AddRelationship(
                    edge.Kind,
                    edge.SourceKey,
                    edge.TargetKey,
                    edge.Evidence,
                    edge.Properties);
            }
        }

        var connected = ConnectDomainOverlay(builder.Build());
        var diagnosticMessages = diagnostics
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(message => message, StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        var issues = new List<PreflightIssue>();
        if (degraded || diagnosticMessages.Length > 0)
        {
            issues.Add(CreateDegradedIssue(diagnosticMessages));
        }
        else
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Information,
                PreflightReasonCode.SemanticExtractionCompleted,
                $"Roslyn semantic graph 已完成：{maps.Projects.Count} 個專案。"));
        }

        return new RoslynSemanticExtractionResult(connected, issues);
    }

    /// <summary>抽取單一專案；Compilation 失敗時仍保留 syntax declaration 與來源邊界。</summary>
    private static async Task<SemanticGraphFragment> ExtractProjectAsync(
        RoslynProjectInfo projectInfo,
        RoslynProjectMaps maps,
        string rootPath,
        ConcurrentBag<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var fragment = new SemanticGraphFragment(projectInfo.Key);
        Compilation? compilation = null;
        try
        {
            using var compilationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            compilationCancellation.CancelAfter(ProjectCompilationTimeout);
            compilation = await projectInfo.Project
                .GetCompilationAsync(compilationCancellation.Token)
                .WaitAsync(ProjectCompilationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(
                $"{projectInfo.Project.Name} compilation 超過 {ProjectCompilationTimeout.TotalMinutes:0} 分鐘，改用 syntax declaration。");
        }
        catch (TimeoutException)
        {
            diagnostics.Add(
                $"{projectInfo.Project.Name} compilation 超過 {ProjectCompilationTimeout.TotalMinutes:0} 分鐘，改用 syntax declaration。");
        }
        catch (Exception exception)
        {
            diagnostics.Add($"{projectInfo.Project.Name} compilation 失敗：{SanitizeDiagnostic(exception.Message)}");
        }

        foreach (var document in projectInfo.Project.Documents
                     .Where(item => item.SourceCodeKind == SourceCodeKind.Regular && item.FilePath is not null)
                     .OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = Path.GetFullPath(document.FilePath!);
            if (!IsWithinRoot(rootPath, filePath) ||
                !RepositoryPathPolicy.IsIncludedSourceFile(rootPath, filePath))
            {
                continue;
            }

            SyntaxNode? root;
            try
            {
                using var syntaxCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                syntaxCancellation.CancelAfter(DocumentSyntaxTimeout);
                root = await document
                    .GetSyntaxRootAsync(syntaxCancellation.Token)
                    .WaitAsync(DocumentSyntaxTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                diagnostics.Add(
                    $"{RelativePath(rootPath, filePath)} 語法載入超過 {DocumentSyntaxTimeout.TotalSeconds:0} 秒。");
                continue;
            }
            catch (TimeoutException)
            {
                diagnostics.Add(
                    $"{RelativePath(rootPath, filePath)} 語法載入超過 {DocumentSyntaxTimeout.TotalSeconds:0} 秒。");
                continue;
            }
            catch (Exception exception)
            {
                diagnostics.Add($"{RelativePath(rootPath, filePath)} 語法載入失敗：{SanitizeDiagnostic(exception.Message)}");
                continue;
            }

            if (root is null)
            {
                continue;
            }

            SemanticModel? semanticModel = null;
            if (compilation is not null)
            {
                try
                {
                    semanticModel = compilation.GetSemanticModel(root.SyntaxTree);
                }
                catch (Exception exception)
                {
                    diagnostics.Add($"{RelativePath(rootPath, filePath)} semantic model 失敗：{SanitizeDiagnostic(exception.Message)}");
                }
            }

            ProcessDocument(fragment, projectInfo, maps, rootPath, filePath, root, semanticModel);
        }

        return fragment;
    }

    /// <summary>抽取檔案、namespace、type、method 與語意關係。</summary>
    private static void ProcessDocument(
        SemanticGraphFragment fragment,
        RoslynProjectInfo projectInfo,
        RoslynProjectMaps maps,
        string rootPath,
        string filePath,
        SyntaxNode root,
        SemanticModel? semanticModel)
    {
        var relativePath = RelativePath(rootPath, filePath);
        var fileKey = RoslynGraphKeys.SourceFile(relativePath);
        var text = root.ToFullString();
        var lineSpan = root.SyntaxTree.GetLineSpan(root.FullSpan);
        fragment.AddNode(
            GraphNodeKind.SourceFile,
            fileKey,
            new Dictionary<string, object?>
            {
                ["name"] = Path.GetFileName(relativePath),
                ["file_path"] = relativePath,
                ["extension"] = Path.GetExtension(relativePath),
                ["line_count"] = lineSpan.EndLinePosition.Line + 1,
                ["source_hash"] = RoslynGraphKeys.Hash(text),
                ["language"] = "csharp",
            });
        fragment.AddRelationship(
            GraphRelationshipKind.ContainsFile,
            projectInfo.Key,
            fileKey,
            SourceEvidence(relativePath, 1));

        AddNamespaces(fragment, fileKey, relativePath, root);
        var typeIdsBySpan = new Dictionary<int, string>();
        foreach (var declaration in root.DescendantNodes()
                     .OfType<BaseTypeDeclarationSyntax>()
                     .Where(IsSupportedType)
                     .OrderBy(item => item.SpanStart))
        {
            var typeInfo = AddType(
                fragment,
                projectInfo,
                maps,
                fileKey,
                relativePath,
                declaration,
                semanticModel,
                typeIdsBySpan);
            typeIdsBySpan[declaration.SpanStart] = typeInfo.Key;
            AddMethods(fragment, projectInfo, maps, fileKey, relativePath, declaration, typeInfo, semanticModel);
        }
    }

    /// <summary>建立 type node、宣告邊界與繼承／介面關係。</summary>
    private static SemanticTypeInfo AddType(
        SemanticGraphFragment fragment,
        RoslynProjectInfo projectInfo,
        RoslynProjectMaps maps,
        string fileKey,
        string relativePath,
        BaseTypeDeclarationSyntax declaration,
        SemanticModel? semanticModel,
        IReadOnlyDictionary<int, string> typeIdsBySpan)
    {
        var symbol = semanticModel?.GetDeclaredSymbol(declaration) as INamedTypeSymbol;
        var fullName = symbol is null ? FallbackTypeName(declaration) : TypeName(symbol);
        var typeKey = RoslynGraphKeys.CodeType(projectInfo.Key, fullName);
        var span = declaration.GetLocation().GetLineSpan();
        fragment.AddNode(
            GraphNodeKind.CodeType,
            typeKey,
            new Dictionary<string, object?>
            {
                ["name"] = declaration.Identifier.ValueText,
                ["full_name"] = fullName,
                ["type_kind"] = TypeKind(declaration),
                ["project_name"] = projectInfo.Project.Name,
                ["assembly_name"] = projectInfo.Project.AssemblyName ?? projectInfo.Project.Name,
                ["accessibility"] = symbol?.DeclaredAccessibility.ToString(),
                ["arity"] = symbol?.Arity ?? 0,
                ["is_abstract"] = symbol?.IsAbstract ?? false,
                ["is_sealed"] = symbol?.IsSealed ?? false,
                ["is_static"] = symbol?.IsStatic ?? false,
                ["is_partial"] = declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)),
                ["source_files"] = new[] { relativePath },
                ["file_path"] = relativePath,
                ["start_line"] = span.StartLinePosition.Line + 1,
                ["end_line"] = span.EndLinePosition.Line + 1,
            });
        fragment.AddRelationship(
            GraphRelationshipKind.DeclaresType,
            fileKey,
            typeKey,
            SourceEvidence(relativePath, span.StartLinePosition.Line + 1));

        var containingType = declaration.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(IsSupportedType);
        if (containingType is not null && typeIdsBySpan.TryGetValue(containingType.SpanStart, out var parentTypeKey))
        {
            fragment.AddRelationship(
                GraphRelationshipKind.ContainsType,
                parentTypeKey,
                typeKey,
                SourceEvidence(relativePath, span.StartLinePosition.Line + 1));
        }
        else
        {
            var namespaceName = symbol is null
                ? FallbackNamespaceName(declaration)
                : NamespaceName(symbol.ContainingNamespace);
            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                var namespaceKey = AddNamespaceNode(fragment, namespaceName);
                fragment.AddRelationship(
                    GraphRelationshipKind.ContainsType,
                    namespaceKey,
                    typeKey,
                    SourceEvidence(relativePath, span.StartLinePosition.Line + 1));
            }
        }

        AddChunk(fragment, typeKey, fileKey, relativePath, declaration, TypeKind(declaration));
        if (symbol is not null)
        {
            if (symbol.BaseType is not null && !IsFrameworkRootType(symbol.BaseType))
            {
                fragment.AddRelationship(
                    GraphRelationshipKind.DerivesFrom,
                    typeKey,
                    ResolveTypeTarget(fragment, maps, projectInfo, symbol.BaseType),
                    SourceEvidence(relativePath, span.StartLinePosition.Line + 1));
            }

            foreach (var interfaceType in symbol.Interfaces)
            {
                fragment.AddRelationship(
                    GraphRelationshipKind.ImplementsType,
                    typeKey,
                    ResolveTypeTarget(fragment, maps, projectInfo, interfaceType),
                    SourceEvidence(relativePath, span.StartLinePosition.Line + 1));
            }
        }
        else if (declaration.BaseList is not null)
        {
            foreach (var baseType in declaration.BaseList.Types)
            {
                var external = AddExternal(fragment, "type", "unresolved", baseType.Type.ToString());
                fragment.AddRelationship(
                    GraphRelationshipKind.DerivesFrom,
                    typeKey,
                    external,
                    SourceEvidence(relativePath, baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
        }

        return new SemanticTypeInfo(typeKey, fullName, symbol);
    }

    /// <summary>抽取目前型別直接擁有的方法，避免把 nested type 或 local function 的呼叫掛錯 owner。</summary>
    private static void AddMethods(
        SemanticGraphFragment fragment,
        RoslynProjectInfo projectInfo,
        RoslynProjectMaps maps,
        string fileKey,
        string relativePath,
        BaseTypeDeclarationSyntax declaration,
        SemanticTypeInfo typeInfo,
        SemanticModel? semanticModel)
    {
        var methodNodes = declaration.DescendantNodes()
            .Where(IsMethodLike)
            .Where(node => node.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                .FirstOrDefault(IsSupportedType)?.SpanStart == declaration.SpanStart)
            .OrderBy(node => node.SpanStart);
        foreach (var methodNode in methodNodes)
        {
            AddMethod(fragment, projectInfo, maps, fileKey, relativePath, typeInfo, methodNode, semanticModel);
        }
    }

    /// <summary>建立 method node，並以 SemanticModel 抽取 overload-safe call、new、override 與 interface edge。</summary>
    private static void AddMethod(
        SemanticGraphFragment fragment,
        RoslynProjectInfo projectInfo,
        RoslynProjectMaps maps,
        string fileKey,
        string relativePath,
        SemanticTypeInfo typeInfo,
        SyntaxNode methodNode,
        SemanticModel? semanticModel)
    {
        var symbol = semanticModel?.GetDeclaredSymbol(methodNode) as IMethodSymbol;
        var methodName = MethodName(methodNode, typeInfo.FullName);
        var signature = symbol is null
            ? $"{typeInfo.FullName}.{methodName}({ParameterCount(methodNode)} parameters)"
            : MethodSignature(symbol.OriginalDefinition);
        var methodKey = RoslynGraphKeys.CodeMethod(projectInfo.Key, typeInfo.FullName, signature, methodNode.SpanStart);
        var span = methodNode.GetLocation().GetLineSpan();
        fragment.AddNode(
            GraphNodeKind.CodeMethod,
            methodKey,
            new Dictionary<string, object?>
            {
                ["name"] = methodName,
                ["full_name"] = $"{typeInfo.FullName}.{methodName}",
                ["containing_type_full_name"] = typeInfo.FullName,
                ["signature"] = signature,
                ["method_kind"] = MethodKindName(methodNode),
                ["project_name"] = projectInfo.Project.Name,
                ["accessibility"] = symbol?.DeclaredAccessibility.ToString(),
                ["return_type"] = symbol?.ReturnsVoid == true ? "void" : symbol?.ReturnType.ToDisplayString(),
                ["modifiers"] = symbol is null ? string.Empty : MethodModifiers(symbol),
                ["is_constructor"] = symbol?.MethodKind == Microsoft.CodeAnalysis.MethodKind.Constructor,
                ["is_async"] = symbol?.IsAsync ?? false,
                ["file_path"] = relativePath,
                ["start_line"] = span.StartLinePosition.Line + 1,
                ["end_line"] = span.EndLinePosition.Line + 1,
            });
        fragment.AddRelationship(
            GraphRelationshipKind.DeclaresMethod,
            typeInfo.Key,
            methodKey,
            SourceEvidence(relativePath, span.StartLinePosition.Line + 1));
        AddChunk(fragment, methodKey, fileKey, relativePath, methodNode, "method");

        if (symbol is null || semanticModel is null)
        {
            return;
        }

        if (symbol.OverriddenMethod is not null)
        {
            fragment.AddRelationship(
                GraphRelationshipKind.OverridesMethod,
                methodKey,
                ResolveMethodTarget(fragment, maps, projectInfo, symbol.OverriddenMethod),
                SourceEvidence(relativePath, span.StartLinePosition.Line + 1));
        }

        foreach (var interfaceMethod in InterfaceMethodsImplementedBy(symbol))
        {
            fragment.AddRelationship(
                GraphRelationshipKind.ImplementsMethod,
                methodKey,
                ResolveMethodTarget(fragment, maps, projectInfo, interfaceMethod),
                SourceEvidence(relativePath, span.StartLinePosition.Line + 1));
        }

        foreach (var invocation in methodNode.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>()
                     .Where(invocation => IsOwnedBy(invocation, methodNode)))
        {
            var target = ResolveUniqueMethodSymbol(semanticModel.GetSymbolInfo(invocation));
            if (target is null)
            {
                continue;
            }

            var callLine = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            fragment.AddRelationship(
                GraphRelationshipKind.CallsMethod,
                methodKey,
                ResolveMethodTarget(fragment, maps, projectInfo, target),
                SourceEvidence(relativePath, callLine, invocation.Expression.ToString()),
                SemanticRelationshipProperties(relativePath, callLine));
        }

        foreach (var creation in methodNode.DescendantNodes()
                     .OfType<ObjectCreationExpressionSyntax>()
                     .Where(creation => IsOwnedBy(creation, methodNode)))
        {
            if (semanticModel.GetTypeInfo(creation).Type is not INamedTypeSymbol targetType)
            {
                continue;
            }

            var creationLine = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            fragment.AddRelationship(
                GraphRelationshipKind.Instantiates,
                methodKey,
                ResolveTypeTarget(fragment, maps, projectInfo, targetType),
                SourceEvidence(relativePath, creationLine, $"new {creation.Type}"),
                SemanticRelationshipProperties(relativePath, creationLine));
        }
    }

    /// <summary>建立 namespace declaration 與 using import。</summary>
    private static void AddNamespaces(
        SemanticGraphFragment fragment,
        string fileKey,
        string relativePath,
        SyntaxNode root)
    {
        foreach (var declaration in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var name = declaration.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var line = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            fragment.AddRelationship(
                GraphRelationshipKind.DeclaresNamespace,
                fileKey,
                AddNamespaceNode(fragment, name),
                SourceEvidence(relativePath, line));
        }

        foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (usingDirective.Alias is not null || usingDirective.Name is null)
            {
                continue;
            }

            var name = usingDirective.Name.ToString();
            var line = usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            fragment.AddRelationship(
                GraphRelationshipKind.ImportsNamespace,
                fileKey,
                AddNamespaceNode(fragment, name),
                SourceEvidence(relativePath, line));
        }
    }

    /// <summary>加入不含完整原始碼的 CodeChunk，以行號及內容雜湊保留檢索邊界。</summary>
    private static void AddChunk(
        SemanticGraphFragment fragment,
        string ownerKey,
        string fileKey,
        string relativePath,
        SyntaxNode node,
        string kind)
    {
        var span = node.GetLocation().GetLineSpan();
        var chunkKey = RoslynGraphKeys.CodeChunk(ownerKey, fileKey, node.SpanStart);
        fragment.AddNode(
            GraphNodeKind.CodeChunk,
            chunkKey,
            new Dictionary<string, object?>
            {
                ["name"] = $"{kind}@{span.StartLinePosition.Line + 1}",
                ["chunk_kind"] = kind,
                ["owner_key"] = ownerKey,
                ["file_key"] = fileKey,
                ["file_path"] = relativePath,
                ["start_line"] = span.StartLinePosition.Line + 1,
                ["end_line"] = span.EndLinePosition.Line + 1,
                ["language"] = "csharp",
                ["source_hash"] = RoslynGraphKeys.Hash(node.ToFullString()),
                ["contains_source_text"] = false,
            });
        fragment.AddRelationship(
            GraphRelationshipKind.HasChunk,
            ownerKey,
            chunkKey,
            SourceEvidence(relativePath, span.StartLinePosition.Line + 1));
    }

    /// <summary>將目前方案內的型別解析成 CodeType，否則建立 ExternalSymbol。</summary>
    private static string ResolveTypeTarget(
        SemanticGraphFragment fragment,
        RoslynProjectMaps maps,
        RoslynProjectInfo currentProject,
        INamedTypeSymbol symbol)
    {
        var normalized = symbol.OriginalDefinition;
        var fullName = TypeName(normalized);
        if (maps.TryResolveProject(normalized, currentProject, out var targetProject))
        {
            var targetKey = RoslynGraphKeys.CodeType(targetProject.Key, fullName);
            fragment.AddNode(
                GraphNodeKind.CodeType,
                targetKey,
                new Dictionary<string, object?>
                {
                    ["name"] = normalized.Name,
                    ["full_name"] = fullName,
                    ["project_name"] = targetProject.Project.Name,
                    ["assembly_name"] = targetProject.Project.AssemblyName ?? targetProject.Project.Name,
                    ["external"] = false,
                });
            return targetKey;
        }

        return AddExternal(
            fragment,
            "type",
            normalized.ContainingAssembly?.Identity.Name ?? "unknown",
            fullName);
    }

    /// <summary>將目前方案內的方法解析成 overload-safe CodeMethod，否則建立 ExternalSymbol。</summary>
    private static string ResolveMethodTarget(
        SemanticGraphFragment fragment,
        RoslynProjectMaps maps,
        RoslynProjectInfo currentProject,
        IMethodSymbol symbol)
    {
        var normalized = (symbol.ReducedFrom ?? symbol).OriginalDefinition;
        if (normalized.ContainingType is not null && maps.TryResolveProject(normalized, currentProject, out var targetProject))
        {
            var typeName = TypeName(normalized.ContainingType.OriginalDefinition);
            var signature = MethodSignature(normalized);
            var targetKey = RoslynGraphKeys.CodeMethod(targetProject.Key, typeName, signature, 0);
            fragment.AddNode(
                GraphNodeKind.CodeMethod,
                targetKey,
                new Dictionary<string, object?>
                {
                    ["name"] = normalized.Name,
                    ["full_name"] = $"{typeName}.{normalized.Name}",
                    ["containing_type_full_name"] = typeName,
                    ["signature"] = signature,
                    ["method_kind"] = normalized.MethodKind.ToString().ToLowerInvariant(),
                    ["project_name"] = targetProject.Project.Name,
                    ["external"] = false,
                });
            return targetKey;
        }

        return AddExternal(
            fragment,
            "method",
            normalized.ContainingAssembly?.Identity.Name ?? "unknown",
            MethodSignature(normalized));
    }

    /// <summary>加入外部型別或方法，stable key 不包含本機絕對路徑。</summary>
    private static string AddExternal(
        SemanticGraphFragment fragment,
        string symbolKind,
        string assemblyName,
        string displayName)
    {
        var key = RoslynGraphKeys.ExternalSymbol(symbolKind, assemblyName, displayName);
        fragment.AddNode(
            GraphNodeKind.ExternalSymbol,
            key,
            new Dictionary<string, object?>
            {
                ["name"] = displayName.Split('.').LastOrDefault() ?? displayName,
                ["display_name"] = displayName,
                ["symbol_kind"] = symbolKind,
                ["assembly_name"] = assemblyName,
                ["external"] = true,
            });
        return key;
    }

    /// <summary>加入 namespace node。</summary>
    private static string AddNamespaceNode(SemanticGraphFragment fragment, string name)
    {
        var key = RoslynGraphKeys.Namespace(name);
        fragment.AddNode(
            GraphNodeKind.Namespace,
            key,
            new Dictionary<string, object?>
            {
                ["name"] = name.Split('.').LastOrDefault() ?? name,
                ["full_name"] = name,
            });
        return key;
    }

    /// <summary>把既有 FBL CodeClass／WebAction 接到 Roslyn 型別與方法，形成單一可走訪圖。</summary>
    private static GraphDocument ConnectDomainOverlay(GraphDocument document)
    {
        var builder = GraphDocumentBuilder.FromDocument(document, document.Metadata.BuildStage);
        var typesByFullName = document.Nodes
            .Where(node => node.Kind == GraphNodeKind.CodeType)
            .Select(node => new { Node = node, FullName = StringProperty(node, "full_name") })
            .Where(item => !string.IsNullOrWhiteSpace(item.FullName))
            .GroupBy(item => item.FullName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Node).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var codeClass in document.Nodes.Where(node => node.Kind == GraphNodeKind.CodeClass))
        {
            var fullName = StringProperty(codeClass, "full_name");
            if (fullName is null || !typesByFullName.TryGetValue(fullName, out var matches) || matches.Length != 1)
            {
                continue;
            }

            builder.AddRelationship(
                GraphRelationshipKind.RepresentsType,
                codeClass.Key,
                matches[0].Key,
                NodeEvidence(matches[0]));
        }

        var methods = document.Nodes
            .Where(node => node.Kind == GraphNodeKind.CodeMethod)
            .GroupBy(
                node => StringProperty(node, "containing_type_full_name") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var action in document.Nodes.Where(node => node.Kind == GraphNodeKind.WebAction))
        {
            var controller = StringProperty(action, "declaring_controller");
            if (controller is null || !methods.TryGetValue(controller, out var candidates))
            {
                continue;
            }

            var names = StringValues(action.Properties.GetValueOrDefault("method_names"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var method in candidates.Where(candidate =>
                         names.Contains(StringProperty(candidate, "name") ?? string.Empty)))
            {
                builder.AddRelationship(
                    GraphRelationshipKind.ImplementedByMethod,
                    action.Key,
                    method.Key,
                    NodeEvidence(method));
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// 建立不執行 MSBuild evaluation 的 repository solution。
    /// 能讀取 .sln 與 .csproj 靜態結構時保留多專案、Compile items 及 ProjectReference；
    /// 完全沒有專案描述時才退回單一 Repository project。
    /// </summary>
    private static async Task<RepositorySolutionLease> CreateRepositorySolutionAsync(
        string rootPath,
        string? solutionPath,
        ConcurrentBag<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var workspace = new AdhocWorkspace();
        var descriptors = LoadFallbackProjectDescriptors(rootPath, solutionPath, diagnostics);
        if (descriptors.Count == 0)
        {
            descriptors =
            [
                new FallbackProjectDescriptor(
                    "Repository",
                    "Repository",
                    null,
                    RepositoryPathPolicy.EnumerateFiles(rootPath, ".cs").ToArray(),
                    [],
                    []),
            ];
        }

        var platformReferences = TrustedPlatformReferences();
        var projectIds = descriptors.ToDictionary(
            descriptor => descriptor.Identity,
            descriptor => ProjectId.CreateNewId(descriptor.Name),
            StringComparer.OrdinalIgnoreCase);
        var solution = workspace.CurrentSolution;
        foreach (var descriptor in descriptors)
        {
            var projectId = projectIds[descriptor.Identity];
            var parseOptions = CSharpParseOptions.Default
                .WithLanguageVersion(LanguageVersion.Latest)
                .WithPreprocessorSymbols(descriptor.PreprocessorSymbols);
            solution = solution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                descriptor.Name,
                descriptor.AssemblyName,
                LanguageNames.CSharp,
                filePath: descriptor.ProjectPath,
                outputFilePath: null,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: parseOptions,
                metadataReferences: platformReferences));
        }

        foreach (var descriptor in descriptors)
        {
            var projectId = projectIds[descriptor.Identity];
            foreach (var path in descriptor.SourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    var documentId = DocumentId.CreateNewId(projectId, debugName: RelativePath(rootPath, path));
                    solution = solution.AddDocument(
                        documentId,
                        Path.GetFileName(path),
                        SourceText.From(text, Encoding.UTF8),
                        filePath: path);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    diagnostics.Add($"{RelativePath(rootPath, path)} fallback 讀取失敗：{SanitizeDiagnostic(exception.Message)}");
                }
            }
        }

        foreach (var descriptor in descriptors)
        {
            var sourceId = projectIds[descriptor.Identity];
            foreach (var referencePath in descriptor.ProjectReferences)
            {
                if (!projectIds.TryGetValue(referencePath, out var targetId) || sourceId == targetId)
                {
                    continue;
                }

                try
                {
                    solution = solution.AddProjectReference(sourceId, new ProjectReference(targetId));
                }
                catch (InvalidOperationException exception)
                {
                    diagnostics.Add(
                        $"{descriptor.Name} fallback project reference 已略過：{SanitizeDiagnostic(exception.Message)}");
                }
            }
        }

        return new RepositorySolutionLease(workspace, solution);
    }

    /// <summary>從 Solution 或根目錄探索 C# project，僅以 XML 讀取，不載入 targets 或執行 task。</summary>
    private static IReadOnlyList<FallbackProjectDescriptor> LoadFallbackProjectDescriptors(
        string rootPath,
        string? solutionPath,
        ConcurrentBag<string> diagnostics)
    {
        var projectPaths = new List<(string Name, string Path)>();
        if (!string.IsNullOrWhiteSpace(solutionPath) &&
            solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(solutionPath))
            {
                var match = SolutionProjectPattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var projectPath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(solutionPath)!,
                    match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar)));
                if (File.Exists(projectPath) && IsWithinRoot(rootPath, projectPath))
                {
                    projectPaths.Add((match.Groups["name"].Value, projectPath));
                }
            }
        }

        if (projectPaths.Count == 0)
        {
            projectPaths.AddRange(Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories)
                .Where(path => RepositoryPathPolicy.IsIncludedSourceFile(rootPath, path))
                .Select(path => (Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path))));
        }

        var result = new List<FallbackProjectDescriptor>();
        foreach (var (name, projectPath) in projectPaths
                     .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.Add(ReadFallbackProjectDescriptor(rootPath, name, projectPath));
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"{RelativePath(rootPath, projectPath)} fallback project 解析失敗：{SanitizeDiagnostic(exception.Message)}");
            }
        }
        return result;
    }

    /// <summary>讀取 csproj 的靜態 Compile／ProjectReference 與基本編譯屬性。</summary>
    private static FallbackProjectDescriptor ReadFallbackProjectDescriptor(
        string rootPath,
        string projectName,
        string projectPath)
    {
        var document = XDocument.Load(projectPath, LoadOptions.None);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        string? Property(string name) => document.Descendants()
            .Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var assemblyName = Property("AssemblyName") ?? projectName;
        var symbols = (Property("DefineConstants") ?? string.Empty)
            .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(symbol => Regex.IsMatch(symbol, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var compileItems = document.Descendants()
            .Where(element => element.Name.LocalName.Equals("Compile", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        var sourceFiles = ResolveCompileItems(rootPath, projectDirectory, compileItems);
        var hasUnresolvedCompilePattern = compileItems.Any(include =>
            include.Contains("$(", StringComparison.Ordinal) || include.IndexOfAny(['*', '?']) >= 0);
        if (sourceFiles.Count == 0 || hasUnresolvedCompilePattern)
        {
            sourceFiles = sourceFiles
                .Concat(Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                    .Where(path => IsWithinRoot(rootPath, path) && RepositoryPathPolicy.IsIncludedSourceFile(rootPath, path))
                    .Select(Path.GetFullPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        var references = document.Descendants()
            .Where(element => element.Name.LocalName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value) && !value.Contains("$(", StringComparison.Ordinal))
            .Select(value => Path.GetFullPath(Path.Combine(
                projectDirectory,
                value!.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(path => File.Exists(path) && IsWithinRoot(rootPath, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new FallbackProjectDescriptor(
            projectName,
            assemblyName,
            projectPath,
            sourceFiles,
            references,
            symbols);
    }

    /// <summary>解析明確 Compile Include；無法安全展開的 MSBuild expression 留給目錄 fallback。</summary>
    private static IReadOnlyList<string> ResolveCompileItems(
        string rootPath,
        string projectDirectory,
        IReadOnlyList<string> compileItems)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var include in compileItems)
        {
            if (include.Contains("$(", StringComparison.Ordinal) ||
                include.IndexOfAny(['*', '?']) >= 0)
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(
                projectDirectory,
                include.Replace('\\', Path.DirectorySeparatorChar)));
            if (File.Exists(path) &&
                IsWithinRoot(rootPath, path) &&
                RepositoryPathPolicy.IsIncludedSourceFile(rootPath, path))
            {
                result.Add(path);
            }
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>取得目前 runtime 已載入的受信任平台 assemblies，供 fallback semantic binding。</summary>
    private static IReadOnlyList<MetadataReference> TrustedPlatformReferences()
    {
        var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? Array.Empty<string>();
        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    /// <summary>優先選根目錄最接近的 Solution；同層時以路徑排序，確保每次索引一致。</summary>
    private static string? DiscoverPrimarySolution(string rootPath)
    {
        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Where(path => RepositoryPathPolicy.IsIncludedSourceFile(rootPath, path))
            .OrderBy(path => RelativePath(rootPath, path).Count(character => character == '/'))
            .ThenBy(path => RelativePath(rootPath, path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsSupportedType(BaseTypeDeclarationSyntax node) =>
        node is ClassDeclarationSyntax or InterfaceDeclarationSyntax or StructDeclarationSyntax
            or RecordDeclarationSyntax or EnumDeclarationSyntax;

    private static bool IsMethodLike(SyntaxNode node) =>
        node is MethodDeclarationSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax;

    private static bool IsOwnedBy(SyntaxNode child, SyntaxNode methodNode) =>
        child.Ancestors().FirstOrDefault(IsMethodBoundary)?.SpanStart == methodNode.SpanStart;

    private static bool IsMethodBoundary(SyntaxNode node) =>
        IsMethodLike(node) || node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax;

    private static IMethodSymbol? ResolveUniqueMethodSymbol(SymbolInfo symbolInfo)
    {
        if (symbolInfo.Symbol is IMethodSymbol exact)
        {
            return exact;
        }

        var candidates = symbolInfo.CandidateSymbols
            .OfType<IMethodSymbol>()
            .Select(symbol => (symbol.ReducedFrom ?? symbol).OriginalDefinition)
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static IEnumerable<IMethodSymbol> InterfaceMethodsImplementedBy(IMethodSymbol method)
    {
        var result = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var explicitMethod in method.ExplicitInterfaceImplementations)
        {
            result.Add(explicitMethod.OriginalDefinition);
        }

        var containingType = method.ContainingType;
        foreach (var interfaceType in containingType.AllInterfaces)
        {
            foreach (var interfaceMethod in interfaceType.GetMembers().OfType<IMethodSymbol>())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol;
                if (implementation is not null && SymbolEqualityComparer.Default.Equals(
                        implementation.OriginalDefinition,
                        method.OriginalDefinition))
                {
                    result.Add(interfaceMethod.OriginalDefinition);
                }
            }
        }

        return result;
    }

    private static string TypeName(INamedTypeSymbol symbol) => StripGlobalPrefix(
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    private static string MethodSignature(IMethodSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static string NamespaceName(INamespaceSymbol symbol) =>
        symbol.IsGlobalNamespace ? string.Empty : symbol.ToDisplayString();

    private static string StripGlobalPrefix(string value) =>
        value.StartsWith("global::", StringComparison.Ordinal) ? value[8..] : value;

    private static string FallbackNamespaceName(SyntaxNode node) => string.Join(
        '.',
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(item => item.Name.ToString())
            .Reverse());

    private static string FallbackTypeName(BaseTypeDeclarationSyntax node)
    {
        var typeName = string.Join(
            '.',
            node.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                .Where(IsSupportedType)
                .Select(item => item.Identifier.ValueText)
                .Reverse()
                .Append(node.Identifier.ValueText));
        var namespaceName = FallbackNamespaceName(node);
        return string.IsNullOrWhiteSpace(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
    }

    private static string TypeKind(BaseTypeDeclarationSyntax node) => node switch
    {
        ClassDeclarationSyntax => "class",
        InterfaceDeclarationSyntax => "interface",
        StructDeclarationSyntax => "struct",
        RecordDeclarationSyntax => "record",
        EnumDeclarationSyntax => "enum",
        _ => "type",
    };

    private static string MethodKindName(SyntaxNode node) => node switch
    {
        ConstructorDeclarationSyntax => "constructor",
        DestructorDeclarationSyntax => "destructor",
        OperatorDeclarationSyntax => "operator",
        ConversionOperatorDeclarationSyntax => "conversion_operator",
        _ => "method",
    };

    private static string MethodName(SyntaxNode node, string containingTypeName) => node switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax => ".ctor",
        DestructorDeclarationSyntax => ".dtor",
        OperatorDeclarationSyntax operation => $"operator {operation.OperatorToken.ValueText}",
        ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
        _ => containingTypeName.Split('.').LastOrDefault() ?? "method",
    };

    private static int ParameterCount(SyntaxNode node) => node is BaseMethodDeclarationSyntax method
        ? method.ParameterList.Parameters.Count
        : 0;

    private static string MethodModifiers(IMethodSymbol symbol)
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

    private static bool IsFrameworkRootType(INamedTypeSymbol symbol) => TypeName(symbol) is
        "System.Object" or "System.ValueType" or "System.Enum" or "System.Delegate" or "System.MulticastDelegate";

    private static GraphEvidence ProjectEvidence(string? relativeProjectPath) => new()
    {
        SourceKind = GraphSourceKind.SourceCode,
        SourceFile = relativeProjectPath,
        SourceLine = relativeProjectPath is null ? null : 1,
    };

    private static GraphEvidence SourceEvidence(string relativePath, int line, string? sourceText = null) => new()
    {
        SourceKind = GraphSourceKind.SourceCode,
        SourceFile = relativePath,
        SourceLine = line,
        SourceText = string.IsNullOrWhiteSpace(sourceText)
            ? null
            : sourceText.Length <= 240 ? sourceText : sourceText[..240],
    };

    private static GraphEvidence NodeEvidence(GraphNode node) => SourceEvidence(
        StringProperty(node, "file_path") ?? StringValues(node.Properties.GetValueOrDefault("source_files")).FirstOrDefault() ?? string.Empty,
        IntProperty(node, "start_line") ?? 1);

    private static IReadOnlyDictionary<string, object?> SemanticRelationshipProperties(string path, int line) =>
        new Dictionary<string, object?>
        {
            ["occurrence_count"] = 1,
            ["locations"] = new[] { $"{path}:{line}" },
        };

    private static string? StringProperty(GraphNode node, string key) =>
        node.Properties.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int? IntProperty(GraphNode node, string key) =>
        node.Properties.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var result)
            ? result
            : null;

    private static IEnumerable<string> StringValues(object? value) => value switch
    {
        string text when !string.IsNullOrWhiteSpace(text) => [text],
        IEnumerable<string> values => values,
        _ => Array.Empty<string>(),
    };

    private static PreflightIssue CreateDegradedIssue(IReadOnlyList<string> diagnostics) => new(
        PreflightSeverity.Warning,
        PreflightReasonCode.SemanticExtractionDegraded,
        diagnostics.Count == 0
            ? "Roslyn semantic graph 使用 repository fallback 完成。"
            : $"Roslyn semantic graph 已降級；前 {diagnostics.Count} 筆診斷：{string.Join(" | ", diagnostics)}");

    private static string SanitizeDiagnostic(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 500 ? singleLine : singleLine[..500];
    }

    private static string RelativePath(string rootPath, string path) =>
        Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsWithinRoot(string rootPath, string path)
    {
        var relative = Path.GetRelativePath(rootPath, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private sealed record SemanticTypeInfo(string Key, string FullName, INamedTypeSymbol? Symbol);

    /// <summary>確保 fallback AdhocWorkspace 在抽取完成後立即釋放。</summary>
    private sealed class RepositorySolutionLease(AdhocWorkspace workspace, Solution solution) : IDisposable
    {
        public Solution Solution { get; } = solution;

        public void Dispose() => workspace.Dispose();
    }

    /// <summary>不經 MSBuild evaluation 取得的最小 project 描述。</summary>
    private sealed record FallbackProjectDescriptor(
        string Name,
        string AssemblyName,
        string? ProjectPath,
        IReadOnlyList<string> SourceFiles,
        IReadOnlyList<string> ProjectReferences,
        IReadOnlyList<string> PreprocessorSymbols)
    {
        public string Identity => ProjectPath ?? Name;
    }
}

/// <summary>
/// 註冊目前機器可用的完整 Visual Studio MSBuild；若只有 Build Tools 才使用其最高版本。
/// 路徑與版本一律由 Locator 探索，不寫死安裝位置。
/// </summary>
internal static class MsBuildRuntimeRegistration
{
    private static readonly object Gate = new();

    /// <summary>MSBuildLocator 全程序只能註冊一次。</summary>
    public static void EnsureRegistered()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        lock (Gate)
        {
            if (MSBuildLocator.IsRegistered)
            {
                return;
            }

            var instance = MSBuildLocator.QueryVisualStudioInstances()
                // 舊式 Web/.NET Framework 專案經常需要完整 Visual Studio 安裝隨附的 targets。
                // 只按版本排序可能誤選較新的精簡 Build Tools，導致 solution evaluation 卡住後降級。
                .OrderByDescending(IsFullVisualStudioInstallation)
                .ThenByDescending(item => item.Version)
                .FirstOrDefault();
            if (instance is not null)
            {
                ConfigureVisualStudioEnvironment(instance);
                MSBuildLocator.RegisterInstance(instance);
            }
            else
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    /// <summary>辨識帶完整 IDE workload 的 Visual Studio，而不是精簡 Build Tools／SDK。</summary>
    private static bool IsFullVisualStudioInstallation(VisualStudioInstance instance) =>
        instance.Name.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase) &&
        !instance.Name.Contains("Build Tools", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 由 Locator 探索結果設定舊式 .NET Framework project evaluation 所需環境，
    /// 不假設 Community／Professional／Enterprise 安裝路徑或 Visual Studio 版本。
    /// </summary>
    private static void ConfigureVisualStudioEnvironment(VisualStudioInstance instance)
    {
        if (!instance.Name.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var executable = Path.Combine(instance.MSBuildPath, "MSBuild.exe");
        if (File.Exists(executable))
        {
            Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", executable);
        }

        var msBuildDirectory = Directory.GetParent(instance.MSBuildPath)?.Parent;
        var visualStudioRoot = msBuildDirectory?.Parent?.FullName;
        if (!string.IsNullOrWhiteSpace(visualStudioRoot) && Directory.Exists(visualStudioRoot))
        {
            Environment.SetEnvironmentVariable(
                "VSINSTALLDIR",
                visualStudioRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        }

        Environment.SetEnvironmentVariable(
            "VisualStudioVersion",
            $"{instance.Version.Major}.0");
    }
}

/// <summary>集中產生不依賴本機絕對路徑的 stable graph key。</summary>
internal static class RoslynGraphKeys
{
    public static string Solution(string identity) => Create("solution", identity);
    public static string Project(string identity, string assemblyName) => Create("project", identity, assemblyName);
    public static string SourceFile(string relativePath) => Create("source-file", Normalize(relativePath));
    public static string Namespace(string fullName) => Create("namespace", fullName);
    public static string CodeType(string projectKey, string fullName) => Create("code-type", projectKey, fullName);

    public static string CodeMethod(string projectKey, string containingType, string signature, int fallbackSpan) =>
        string.IsNullOrWhiteSpace(signature)
            ? Create("code-method", projectKey, containingType, fallbackSpan.ToString())
            : Create("code-method", projectKey, containingType, signature);

    public static string CodeChunk(string ownerKey, string fileKey, int spanStart) =>
        Create("code-chunk", ownerKey, fileKey, spanStart.ToString());

    public static string ExternalSymbol(string kind, string assemblyName, string displayName) =>
        Create("external-symbol", kind, assemblyName, displayName);

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Create(string prefix, params string[] values)
    {
        var canonical = string.Join('\u001f', values.Select(Normalize));
        return $"{prefix}:{Hash(canonical)[..32]}";
    }

    private static string Normalize(string value) => value.Trim().Replace('\\', '/');
}

/// <summary>保存 Solution 中 project、assembly 與來源檔的確定性對照。</summary>
internal sealed class RoslynProjectMaps
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<RoslynProjectInfo>> _projectsByAssembly;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<RoslynProjectInfo>> _projectsByFile;

    private RoslynProjectMaps(
        IReadOnlyDictionary<ProjectId, RoslynProjectInfo> projects,
        IReadOnlyDictionary<string, IReadOnlyList<RoslynProjectInfo>> projectsByAssembly,
        IReadOnlyDictionary<string, IReadOnlyList<RoslynProjectInfo>> projectsByFile)
    {
        Projects = projects;
        _projectsByAssembly = projectsByAssembly;
        _projectsByFile = projectsByFile;
    }

    public IReadOnlyDictionary<ProjectId, RoslynProjectInfo> Projects { get; }

    public static RoslynProjectMaps Create(Solution solution, string rootPath)
    {
        var includedProjects = solution.Projects
            .Where(project => project.Documents.Any(document =>
                document.SourceCodeKind == SourceCodeKind.Regular &&
                document.FilePath is not null &&
                RoslynSemanticGraphExtractorPath.IsWithin(rootPath, document.FilePath) &&
                RepositoryPathPolicy.IsIncludedSourceFile(rootPath, document.FilePath)))
            .ToArray();
        var projects = includedProjects.ToDictionary(
            project => project.Id,
            project =>
            {
                var relativePath = project.FilePath is null || !RoslynSemanticGraphExtractorPath.IsWithin(rootPath, project.FilePath)
                    ? project.Name
                    : RoslynSemanticGraphExtractorPath.Relative(rootPath, project.FilePath);
                return new RoslynProjectInfo(
                    project,
                    RoslynGraphKeys.Project(relativePath, project.AssemblyName ?? project.Name),
                    project.FilePath is null ? null : relativePath);
            });
        var byAssembly = projects.Values
            .GroupBy(item => item.Project.AssemblyName ?? item.Project.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RoslynProjectInfo>)group.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var byFile = projects.Values
            .SelectMany(item => item.Project.Documents
                .Where(document => document.FilePath is not null)
                .Select(document => new { Path = Path.GetFullPath(document.FilePath!), Project = item }))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RoslynProjectInfo>)group.Select(item => item.Project)
                    .DistinctBy(item => item.Key)
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        return new RoslynProjectMaps(projects, byAssembly, byFile);
    }

    public bool TryResolveProject(ISymbol symbol, RoslynProjectInfo current, out RoslynProjectInfo project)
    {
        var assemblyName = symbol.ContainingAssembly?.Identity.Name;
        if (!string.IsNullOrWhiteSpace(assemblyName) &&
            _projectsByAssembly.TryGetValue(assemblyName, out var assemblyMatches))
        {
            if (assemblyMatches.Count == 1)
            {
                project = assemblyMatches[0];
                return true;
            }

            var sourceMatches = symbol.Locations
                .Where(location => location.IsInSource && location.SourceTree?.FilePath is not null)
                .SelectMany(location => _projectsByFile.GetValueOrDefault(Path.GetFullPath(location.SourceTree!.FilePath))
                    ?? Array.Empty<RoslynProjectInfo>())
                .DistinctBy(item => item.Key)
                .ToArray();
            if (sourceMatches.Length == 1)
            {
                project = sourceMatches[0];
                return true;
            }

            var currentMatch = assemblyMatches.FirstOrDefault(item => item.Key == current.Key);
            if (currentMatch is not null)
            {
                project = currentMatch;
                return true;
            }
        }

        project = null!;
        return false;
    }
}

/// <summary>單一 Roslyn Project 的 stable identity 與來源位置。</summary>
internal sealed record RoslynProjectInfo(Project Project, string Key, string? RelativeProjectPath);

/// <summary>平行 worker 的記憶體 fragment；不直接寫入共享 builder 或 Neo4j。</summary>
internal sealed class SemanticGraphFragment
{
    private readonly Dictionary<string, SemanticNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemanticRelationship> _relationships = new(StringComparer.Ordinal);

    public SemanticGraphFragment(string projectKey) => ProjectKey = projectKey;

    public string ProjectKey { get; }
    public IReadOnlyCollection<SemanticNode> Nodes => _nodes.Values;
    public IReadOnlyCollection<SemanticRelationship> Relationships => _relationships.Values;

    public void AddNode(GraphNodeKind kind, string key, IReadOnlyDictionary<string, object?> properties)
    {
        if (!_nodes.TryGetValue(key, out var existing))
        {
            _nodes.Add(key, new SemanticNode(kind, key, properties));
            return;
        }

        if (existing.Kind != kind)
        {
            throw new InvalidOperationException(
                $"Semantic fragment 節點 '{key}' 同時宣告為 {existing.Kind} 與 {kind}。");
        }

        _nodes[key] = existing with { Properties = MergeProperties(existing.Properties, properties) };
    }

    public void AddRelationship(
        GraphRelationshipKind kind,
        string sourceKey,
        string targetKey,
        GraphEvidence evidence,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var identity = $"{kind}|{sourceKey}|{targetKey}";
        var incoming = new SemanticRelationship(
            kind,
            sourceKey,
            targetKey,
            evidence,
            properties ?? new Dictionary<string, object?>());
        if (!_relationships.TryGetValue(identity, out var existing))
        {
            _relationships.Add(identity, incoming);
            return;
        }

        var merged = new Dictionary<string, object?>(
            MergeProperties(existing.Properties, incoming.Properties),
            StringComparer.Ordinal);
        var existingCount = PositiveInt(existing.Properties.GetValueOrDefault("occurrence_count")) ?? 1;
        var incomingCount = PositiveInt(incoming.Properties.GetValueOrDefault("occurrence_count")) ?? 1;
        merged["occurrence_count"] = checked(existingCount + incomingCount);
        var locations = LocationValues(existing.Properties.GetValueOrDefault("locations"))
            .Concat(LocationValues(incoming.Properties.GetValueOrDefault("locations")))
            .Concat(EvidenceLocation(existing.Evidence))
            .Concat(EvidenceLocation(incoming.Evidence))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        if (locations.Length > 0)
        {
            merged["locations"] = locations;
        }

        _relationships[identity] = existing with { Properties = merged };
    }

    private static IReadOnlyDictionary<string, object?> MergeProperties(
        IReadOnlyDictionary<string, object?> existing,
        IReadOnlyDictionary<string, object?> incoming)
    {
        var merged = new Dictionary<string, object?>(existing, StringComparer.Ordinal);
        foreach (var (key, value) in incoming)
        {
            if (value is null)
            {
                continue;
            }

            if (!merged.TryGetValue(key, out var current) || current is null ||
                current is string text && string.IsNullOrWhiteSpace(text))
            {
                merged[key] = value;
            }
            else if (current is IEnumerable<string> currentValues && value is IEnumerable<string> incomingValues)
            {
                merged[key] = currentValues.Concat(incomingValues)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
            }
        }
        return merged;
    }

    private static int? PositiveInt(object? value) =>
        value is not null && int.TryParse(value.ToString(), out var number) && number > 0
            ? number
            : null;

    private static IEnumerable<string> LocationValues(object? value) => value switch
    {
        string text when !string.IsNullOrWhiteSpace(text) => [text],
        IEnumerable<string> values => values,
        _ => Array.Empty<string>(),
    };

    private static IEnumerable<string> EvidenceLocation(GraphEvidence evidence) =>
        string.IsNullOrWhiteSpace(evidence.SourceFile)
            ? Array.Empty<string>()
            : [evidence.SourceLine is > 0
                ? $"{evidence.SourceFile}:{evidence.SourceLine.Value}"
                : evidence.SourceFile];
}

internal sealed record SemanticNode(
    GraphNodeKind Kind,
    string Key,
    IReadOnlyDictionary<string, object?> Properties);

internal sealed record SemanticRelationship(
    GraphRelationshipKind Kind,
    string SourceKey,
    string TargetKey,
    GraphEvidence Evidence,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>供 project map 使用的路徑 helper，避免暴露主抽取器私有方法。</summary>
internal static class RoslynSemanticGraphExtractorPath
{
    public static string Relative(string rootPath, string path) =>
        Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/');

    public static bool IsWithin(string rootPath, string path)
    {
        var relative = Path.GetRelativePath(rootPath, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}
