namespace AgentService.Modules.GraphRAG.ParallelExtractor;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

/// <summary>平行版後端程式碼圖抽取器，負責以 Roslyn 抽取專案、檔案、型別、方法與呼叫關係。</summary>
sealed class BackendGraphExtractor
{
    private const int MaxChunkCharacters = 50_000;
    private readonly bool _includeCodeChunkText;

    private CodeGraphData _graph = new();
    private readonly ConcurrentBag<string> _diagnostics = new();
    private IReadOnlyDictionary<ProjectId, string> _projectIds = new Dictionary<ProjectId, string>();
    private IReadOnlyDictionary<string, string> _assemblyToProjectId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, List<string>> _pathToProjectIds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, Project> _projectById = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _typeIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _methodIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externalSymbolIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _callRelationshipIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _codeChunkIds = new(StringComparer.Ordinal);

    /// <summary>建立後端圖抽取器，可注入共用的 Solution 專案索引。</summary>
    public BackendGraphExtractor(bool includeCodeChunkText = true, ProjectMapSnapshot? sharedProjectMaps = null)
    {
        _includeCodeChunkText = includeCodeChunkText;
        if (sharedProjectMaps is not null)
        {
            _projectIds = sharedProjectMaps.ProjectIds;
            _assemblyToProjectId = sharedProjectMaps.AssemblyToProjectId;
            _pathToProjectIds = sharedProjectMaps.PathToProjectIds;
            _projectById = sharedProjectMaps.ProjectById;
        }
    }

    /// <summary>抽取「ExtractAsync」所代表的圖譜抽取或匯入工作。</summary>
    public async Task<ParallelGraphRunResult> ExtractAsync(
        string solutionPath,
        Func<CodeGraphData, Task> graphSink,
        int maxDegreeOfParallelism = 2,
        CancellationToken cancellationToken = default)
    {
        if (maxDegreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        }

        var totalTimer = Stopwatch.StartNew();
        using var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e => _diagnostics.Add(e.Diagnostic.Message));

        var loadTimer = Stopwatch.StartNew();
        var solution = await workspace.OpenSolutionAsync(solutionPath, progress: null, cancellationToken: cancellationToken);
        loadTimer.Stop();

        BuildProjectMaps(solution);
        var sharedProjectMaps = new ProjectMapSnapshot(
            _projectIds,
            _assemblyToProjectId,
            _pathToProjectIds,
            _projectById);
        var solutionId = GraphIds.Solution(solutionPath);
        _graph = new CodeGraphData();
        _graph.AddNode("Solution", solutionId, new Dictionary<string, object?>
        {
            ["name"] = Path.GetFileNameWithoutExtension(solutionPath),
            ["path"] = GraphIds.NormalizePath(solutionPath),
            ["projectCount"] = solution.ProjectIds.Count,
            ["indexedAtUtc"] = DateTime.UtcNow.ToString("O")
        });

        foreach (var project in solution.Projects)
        {
            AddProject(solutionId, project);
        }

        var expected = new ParallelGraphManifest();
        expected.Add(_graph);
        var initialWriteTimer = Stopwatch.StartNew();
        await graphSink(_graph);
        initialWriteTimer.Stop();

        var extractionTimer = Stopwatch.StartNew();
        var totalDocuments = 0;
        var totalSourceDocuments = 0;
        var totalSyntaxTrees = 0;
        var totalSemanticDocuments = 0;
        var projectNumber = 0;
        var writeGate = new SemaphoreSlim(1, 1);
        var diagnostics = new ConcurrentBag<string>(_diagnostics);
        long projectExtractionMicroseconds = 0;
        long neo4jWriteMicroseconds = (long)(initialWriteTimer.Elapsed.TotalMilliseconds * 1000);

        try
        {
            await Parallel.ForEachAsync(
                solution.Projects,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                },
                async (project, token) =>
                {
                    var ordinal = Interlocked.Increment(ref projectNumber);
                    var worker = new BackendGraphExtractor(_includeCodeChunkText, sharedProjectMaps);
                    var projectExtractionTimer = Stopwatch.StartNew();
                    var result = await worker.ExtractProjectAsync(project, ordinal, solution.ProjectIds.Count, token);
                    projectExtractionTimer.Stop();
                    Interlocked.Add(ref projectExtractionMicroseconds, (long)(projectExtractionTimer.Elapsed.TotalMilliseconds * 1000));

                    Interlocked.Add(ref totalDocuments, result.DocumentCount);
                    Interlocked.Add(ref totalSourceDocuments, result.SourceDocumentCount);
                    Interlocked.Add(ref totalSyntaxTrees, result.SyntaxTreeCount);
                    Interlocked.Add(ref totalSemanticDocuments, result.SemanticDocumentCount);
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        diagnostics.Add(diagnostic);
                    }

                    lock (expected)
                    {
                        expected.Add(result.Graph);
                    }

                    await writeGate.WaitAsync(token);
                    var writeTimer = Stopwatch.StartNew();
                    try
                    {
                        await graphSink(result.Graph);
                    }
                    finally
                    {
                        writeTimer.Stop();
                        writeGate.Release();
                    }
                    Interlocked.Add(ref neo4jWriteMicroseconds, (long)(writeTimer.Elapsed.TotalMilliseconds * 1000));
                });
        }
        finally
        {
            writeGate.Dispose();
        }

        extractionTimer.Stop();
        var summary = new GraphExtractionResult(
            new CodeGraphData(),
            solution.ProjectIds.Count,
            totalDocuments,
            totalSourceDocuments,
            totalSyntaxTrees,
            expected.Count("Type"),
            expected.Count("Method"),
            expected.CountRelationships("CALLS"),
            expected.Count("ExternalSymbol"),
            expected.Count("CodeChunk"),
            totalSemanticDocuments,
            diagnostics.ToArray(),
            loadTimer.Elapsed.TotalMilliseconds,
            extractionTimer.Elapsed.TotalMilliseconds);

        totalTimer.Stop();
        return new ParallelGraphRunResult(
            summary,
            expected,
            projectExtractionMicroseconds / 1000d,
            neo4jWriteMicroseconds / 1000d,
            totalTimer.Elapsed.TotalMilliseconds);
    }

    /// <summary>抽取「ExtractProjectAsync」所代表的圖譜抽取或匯入工作。</summary>
    private async Task<ParallelProjectResult> ExtractProjectAsync(
        Project project,
        int ordinal,
        int totalProjects,
        CancellationToken cancellationToken)
    {
        _graph = new CodeGraphData();
        var documentCount = 0;
        var sourceDocumentCount = 0;
        var syntaxTreeCount = 0;
        var semanticDocumentCount = 0;
        Console.WriteLine($"平行圖譜抽取：專案 {ordinal}/{totalProjects}－{project.Name}");

        Compilation? compilation = null;
        try
        {
            compilation = await project.GetCompilationAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _diagnostics.Add($"Compilation failed for {project.Name}: {exception.Message}");
        }

        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documentCount++;
            if (document.SourceCodeKind != SourceCodeKind.Regular || string.IsNullOrWhiteSpace(document.FilePath))
            {
                continue;
            }

            sourceDocumentCount++;
            SyntaxNode? root;
            SyntaxTree? syntaxTree;
            try
            {
                root = await document.GetSyntaxRootAsync(cancellationToken);
                syntaxTree = root?.SyntaxTree;
            }
            catch (Exception exception)
            {
                _diagnostics.Add($"Syntax load failed for {document.FilePath}: {exception.Message}");
                continue;
            }

            if (root is null || syntaxTree is null)
            {
                continue;
            }

            syntaxTreeCount++;
            SemanticModel? semanticModel = null;
            if (compilation is not null)
            {
                try
                {
                    semanticModel = compilation.GetSemanticModel(syntaxTree);
                    semanticDocumentCount++;
                }
                catch (Exception exception)
                {
                    _diagnostics.Add($"Semantic model failed for {document.FilePath}: {exception.Message}");
                }
            }

            ProcessDocument(project, document.FilePath, root, semanticModel, GraphIds.Solution(project.Solution.FilePath ?? string.Empty));
        }

        return new ParallelProjectResult(
            _graph,
            documentCount,
            sourceDocumentCount,
            syntaxTreeCount,
            semanticDocumentCount,
            _diagnostics.ToArray());
    }

    /// <summary>建立「BuildProjectMaps」所代表的圖譜抽取或匯入工作。</summary>
    private void BuildProjectMaps(Solution solution)
    {
        var projectIds = new Dictionary<ProjectId, string>();
        var assemblyToProjectId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathToProjectIds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var projectById = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            var projectId = GraphIds.Project(project);
            projectIds[project.Id] = projectId;
            projectById[project.Id.Id.ToString()] = project;

            var assemblyName = project.AssemblyName ?? project.Name;
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                assemblyToProjectId[assemblyName] = projectId;
            }

            foreach (var document in project.Documents)
            {
                if (document.SourceCodeKind != SourceCodeKind.Regular || string.IsNullOrWhiteSpace(document.FilePath))
                {
                    continue;
                }

                var normalizedPath = GraphIds.NormalizePath(document.FilePath);
                if (!pathToProjectIds.TryGetValue(normalizedPath, out var projectIdsForPath))
                {
                    projectIdsForPath = new List<string>();
                    pathToProjectIds[normalizedPath] = projectIdsForPath;
                }

                if (!projectIdsForPath.Contains(projectId, StringComparer.Ordinal))
                {
                    projectIdsForPath.Add(projectId);
                }
            }
        }

        _projectIds = projectIds;
        _assemblyToProjectId = assemblyToProjectId;
        _pathToProjectIds = pathToProjectIds;
        _projectById = projectById;
    }

    /// <summary>加入「AddProject」所代表的圖譜抽取或匯入工作。</summary>
    private void AddProject(string solutionId, Project project)
    {
        var projectId = _projectIds[project.Id];
        _graph.AddNode("Project", projectId, new Dictionary<string, object?>
        {
            ["name"] = project.Name,
            ["path"] = project.FilePath is null ? string.Empty : GraphIds.NormalizePath(project.FilePath),
            ["language"] = project.Language,
            ["assemblyName"] = project.AssemblyName ?? project.Name,
            ["projectGuid"] = project.Id.Id.ToString()
        });
        _graph.AddRelationship("CONTAINS_PROJECT", solutionId, projectId);

        foreach (var projectReference in project.ProjectReferences)
        {
            if (_projectIds.TryGetValue(projectReference.ProjectId, out var targetProjectId))
            {
                _graph.AddRelationship("REFERENCES_PROJECT", projectId, targetProjectId);
            }
        }
    }

    /// <summary>處理「ProcessDocument」所代表的圖譜抽取或匯入工作。</summary>
    private void ProcessDocument(
        Project project,
        string filePath,
        SyntaxNode root,
        SemanticModel? semanticModel,
        string solutionId)
    {
        var projectId = _projectIds[project.Id];
        var fileId = GraphIds.File(filePath);
        var sourceText = root.ToFullString();
        var lineCount = root.SyntaxTree.GetLineSpan(root.FullSpan).EndLinePosition.Line + 1;
        var relativePath = project.Solution.FilePath is null
            ? filePath
            : Path.GetRelativePath(Path.GetDirectoryName(project.Solution.FilePath)!, filePath);

        _graph.AddNode("File", fileId, new Dictionary<string, object?>
        {
            ["path"] = GraphIds.NormalizePath(filePath),
            ["relativePath"] = relativePath,
            ["name"] = Path.GetFileName(filePath),
            ["extension"] = Path.GetExtension(filePath),
            ["lineCount"] = lineCount,
            ["sourceHash"] = GraphIds.HashText(sourceText),
            ["language"] = "csharp"
        });
        _graph.AddRelationship("CONTAINS_FILE", projectId, fileId);

        AddNamespaceDeclarations(root, fileId);
        AddUsingRelationships(root, fileId);

        var typeDeclarations = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(IsSupportedType)
            .OrderBy(node => node.SpanStart)
            .ToList();
        var typeIdsBySpan = new Dictionary<int, string>();

        foreach (var typeDeclaration in typeDeclarations)
        {
            var typeInfo = AddType(project, fileId, typeDeclaration, semanticModel, typeIdsBySpan);
            typeIdsBySpan[typeDeclaration.SpanStart] = typeInfo.TypeId;
            AddTypeMembers(project, fileId, typeDeclaration, typeInfo, semanticModel, typeIdsBySpan);
        }
    }

    /// <summary>加入「AddType」所代表的圖譜抽取或匯入工作。</summary>
    private TypeExtractionInfo AddType(
        Project project,
        string fileId,
        BaseTypeDeclarationSyntax typeDeclaration,
        SemanticModel? semanticModel,
        IReadOnlyDictionary<int, string> typeIdsBySpan)
    {
        var projectId = _projectIds[project.Id];
        var symbol = semanticModel?.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
        var typeName = symbol is null
            ? GetFallbackTypeName(typeDeclaration)
            : GraphSymbolFormatting.TypeName(symbol);
        var kind = GetTypeKind(typeDeclaration);
        var typeId = GraphIds.Type(projectId, typeName);
        _typeIds.Add(typeId);
        var namespaceName = symbol is null
            ? GetFallbackNamespaceName(typeDeclaration)
            : GraphSymbolFormatting.NamespaceName(symbol.ContainingNamespace);

        _graph.AddNode("Type", typeId, new Dictionary<string, object?>
        {
            ["name"] = typeDeclaration.Identifier.ValueText,
            ["fullName"] = typeName,
            ["kind"] = kind,
            ["projectId"] = projectId,
            ["accessibility"] = symbol is null ? string.Empty : GraphSymbolFormatting.Accessibility(symbol),
            ["arity"] = symbol?.Arity ?? 0,
            ["isAbstract"] = symbol?.IsAbstract ?? false,
            ["isSealed"] = symbol?.IsSealed ?? false,
            ["isStatic"] = symbol?.IsStatic ?? false,
            ["isPartial"] = typeDeclaration.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)),
            ["startLine"] = GetLineSpan(typeDeclaration).StartLinePosition.Line + 1,
            ["endLine"] = GetLineSpan(typeDeclaration).EndLinePosition.Line + 1
        });
        _graph.AddRelationship("DECLARES_TYPE", fileId, typeId, LocationProperties(typeDeclaration));

        var containingType = typeDeclaration.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(IsSupportedType);
        if (containingType is not null && typeIdsBySpan.TryGetValue(containingType.SpanStart, out var containingTypeId))
        {
            _graph.AddRelationship("CONTAINS_TYPE", containingTypeId, typeId);
        }
        else if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            var namespaceId = AddNamespace(namespaceName);
            _graph.AddRelationship("CONTAINS_TYPE", namespaceId, typeId);
        }

        AddChunk(typeId, fileId, typeDeclaration, kind);

        if (symbol is not null)
        {
            AddTypeSemanticRelationships(projectId, typeId, symbol);
        }
        else if (typeDeclaration.BaseList is not null)
        {
            foreach (var baseType in typeDeclaration.BaseList.Types)
            {
                var externalId = AddExternalType(baseType.Type.ToString(), "unresolved");
                _graph.AddRelationship("UNRESOLVED_BASE", typeId, externalId, LocationProperties(baseType.Type));
            }
        }

        return new TypeExtractionInfo(typeId, typeName, symbol);
    }

    /// <summary>加入「AddTypeMembers」所代表的圖譜抽取或匯入工作。</summary>
    private void AddTypeMembers(
        Project project,
        string fileId,
        BaseTypeDeclarationSyntax typeDeclaration,
        TypeExtractionInfo typeInfo,
        SemanticModel? semanticModel,
        IReadOnlyDictionary<int, string> typeIdsBySpan)
    {
        var methodNodes = typeDeclaration.DescendantNodes()
            .Where(IsMethodLike)
            .Where(node => node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(IsSupportedType)?.SpanStart == typeDeclaration.SpanStart)
            .OrderBy(node => node.SpanStart);

        foreach (var methodNode in methodNodes)
        {
            AddMethod(project, fileId, typeInfo, methodNode, semanticModel);
        }
    }

    /// <summary>加入「AddMethod」所代表的圖譜抽取或匯入工作。</summary>
    private void AddMethod(
        Project project,
        string fileId,
        TypeExtractionInfo typeInfo,
        SyntaxNode methodNode,
        SemanticModel? semanticModel)
    {
        var projectId = _projectIds[project.Id];
        var methodSymbol = semanticModel?.GetDeclaredSymbol(methodNode) as IMethodSymbol;
        var methodName = GetMethodName(methodNode, typeInfo.TypeName);
        var signature = methodSymbol is null
            ? $"{methodName}({GetParameterCount(methodNode)} parameters)"
            : GraphSymbolFormatting.MethodSignature(methodSymbol);
        var methodId = methodSymbol is null
            ? GraphIds.FallbackMethod(projectId, fileId, methodNode.SpanStart, methodName)
            : GraphIds.Method(projectId, typeInfo.TypeName, signature);
        _methodIds.Add(methodId);

        _graph.AddNode("Method", methodId, new Dictionary<string, object?>
        {
            ["name"] = methodName,
            ["fullName"] = $"{typeInfo.TypeName}.{methodName}",
            ["signature"] = signature,
            ["kind"] = GetMethodKind(methodNode),
            ["projectId"] = projectId,
            ["fileId"] = fileId,
            ["accessibility"] = methodSymbol is null ? string.Empty : GraphSymbolFormatting.Accessibility(methodSymbol),
            ["returnType"] = methodSymbol?.ReturnsVoid == true ? "void" : methodSymbol?.ReturnType.ToDisplayString(),
            ["modifiers"] = methodSymbol is null ? string.Empty : GraphSymbolFormatting.Modifiers(methodSymbol),
            ["isConstructor"] = methodSymbol?.MethodKind == MethodKind.Constructor,
            ["isAsync"] = methodSymbol?.IsAsync ?? false,
            ["startLine"] = GetLineSpan(methodNode).StartLinePosition.Line + 1,
            ["endLine"] = GetLineSpan(methodNode).EndLinePosition.Line + 1
        });
        _graph.AddRelationship("DECLARES_METHOD", typeInfo.TypeId, methodId, LocationProperties(methodNode));
        AddChunk(methodId, fileId, methodNode, "method");

        if (methodSymbol is null)
        {
            return;
        }

        if (methodSymbol.OverriddenMethod is not null)
        {
            var targetId = ResolveMethodTarget(methodSymbol.OverriddenMethod, projectId);
            _graph.AddRelationship("OVERRIDES", methodId, targetId);
        }

        foreach (var interfaceMethod in methodSymbol.ExplicitInterfaceImplementations)
        {
            var targetId = ResolveMethodTarget(interfaceMethod, projectId);
            _graph.AddRelationship("IMPLEMENTS_METHOD", methodId, targetId);
        }

        if (semanticModel is null)
        {
            return;
        }

        foreach (var invocation in methodNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var targetSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol
                ?? semanticModel.GetSymbolInfo(invocation).CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (targetSymbol is null)
            {
                continue;
            }

            var targetId = ResolveMethodTarget(targetSymbol, projectId);
            _graph.AddRelationship(
                "CALLS",
                methodId,
                targetId,
                new Dictionary<string, object?>
                {
                    ["locations"] = new[] { FormatLocation(invocation) },
                    ["callText"] = invocation.Expression.ToString()
                });
            _callRelationshipIds.Add($"{methodId}|{targetId}");
        }

        foreach (var creation in methodNode.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var targetType = semanticModel.GetTypeInfo(creation).Type as INamedTypeSymbol;
            if (targetType is null)
            {
                continue;
            }

            var targetId = ResolveTypeTarget(targetType, projectId);
            _graph.AddRelationship(
                "INSTANTIATES",
                methodId,
                targetId,
                new Dictionary<string, object?>
                {
                    ["locations"] = new[] { FormatLocation(creation) }
                });
        }
    }

    /// <summary>加入「AddTypeSemanticRelationships」所代表的圖譜抽取或匯入工作。</summary>
    private void AddTypeSemanticRelationships(string projectId, string typeId, INamedTypeSymbol symbol)
    {
        if (symbol.BaseType is not null && !IsFrameworkRootType(symbol.BaseType))
        {
            var baseId = ResolveTypeTarget(symbol.BaseType, projectId);
            _graph.AddRelationship("DERIVES_FROM", typeId, baseId);
        }

        foreach (var interfaceType in symbol.Interfaces)
        {
            var interfaceId = ResolveTypeTarget(interfaceType, projectId);
            _graph.AddRelationship("IMPLEMENTS", typeId, interfaceId);
        }
    }

    /// <summary>取得「ResolveTypeTarget」所代表的圖譜抽取或匯入工作。</summary>
    private string ResolveTypeTarget(INamedTypeSymbol symbol, string currentProjectId)
    {
        var typeName = GraphSymbolFormatting.TypeName(symbol);
        var assemblyName = symbol.ContainingAssembly?.Identity.Name ?? "unknown";
        if (TryResolveProjectId(symbol, currentProjectId, out var targetProjectId))
        {
            var targetId = GraphIds.Type(targetProjectId, typeName);
            _typeIds.Add(targetId);
            var targetNode = _graph.AddNode("Type", targetId, new Dictionary<string, object?>
            {
                ["name"] = symbol.Name,
                ["fullName"] = typeName,
                ["projectId"] = targetProjectId,
                ["external"] = false
            });
            // A source declaration has the authoritative syntax kind (especially record).
            // Only fill kind for a semantic target stub that has not been declared yet.
            targetNode.Properties.TryAdd("kind", symbol.TypeKind.ToString().ToLowerInvariant());
            return targetId;
        }

        return AddExternalType(typeName, assemblyName);
    }

    /// <summary>取得「ResolveMethodTarget」所代表的圖譜抽取或匯入工作。</summary>
    private string ResolveMethodTarget(IMethodSymbol symbol, string currentProjectId)
    {
        var containingType = symbol.ContainingType;
        var signature = GraphSymbolFormatting.MethodSignature(symbol);
        var assemblyName = symbol.ContainingAssembly?.Identity.Name ?? "unknown";
        if (containingType is not null && TryResolveProjectId(symbol, currentProjectId, out var targetProjectId))
        {
            var containingTypeName = GraphSymbolFormatting.TypeName(containingType);
            var targetId = GraphIds.Method(targetProjectId, containingTypeName, signature);
            _methodIds.Add(targetId);
            _graph.AddNode("Method", targetId, new Dictionary<string, object?>
            {
                ["name"] = symbol.Name,
                ["fullName"] = $"{containingTypeName}.{symbol.Name}",
                ["signature"] = signature,
                ["kind"] = symbol.MethodKind.ToString().ToLowerInvariant(),
                ["projectId"] = targetProjectId,
                ["external"] = false
            });
            return targetId;
        }

        var externalId = GraphIds.External("method", assemblyName, signature);
        _externalSymbolIds.Add(externalId);
        _graph.AddNode("ExternalSymbol", externalId, new Dictionary<string, object?>
        {
            ["name"] = symbol.Name,
            ["displayName"] = signature,
            ["kind"] = "method",
            ["assemblyName"] = assemblyName,
            ["external"] = true
        });
        return externalId;
    }

    /// <summary>判斷「TryResolveProjectId」所代表的圖譜抽取或匯入工作。</summary>
    private bool TryResolveProjectId(ISymbol symbol, string currentProjectId, out string projectId)
    {
        var assemblyName = symbol.ContainingAssembly?.Identity.Name;
        if (!string.IsNullOrWhiteSpace(assemblyName) && _assemblyToProjectId.TryGetValue(assemblyName, out projectId!))
        {
            return true;
        }

        projectId = currentProjectId;
        return symbol.ContainingAssembly is not null
            && string.Equals(assemblyName, GetAssemblyName(currentProjectId), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取得「GetAssemblyName」所代表的圖譜抽取或匯入工作。</summary>
    private string GetAssemblyName(string projectId)
        => _projectById.Values.FirstOrDefault(project => _projectIds[project.Id] == projectId)?.AssemblyName
            ?? _projectById.Values.FirstOrDefault(project => _projectIds[project.Id] == projectId)?.Name
            ?? string.Empty;

    /// <summary>加入「AddExternalType」所代表的圖譜抽取或匯入工作。</summary>
    private string AddExternalType(string displayName, string assemblyName)
    {
        var externalId = GraphIds.External("type", assemblyName, displayName);
        _externalSymbolIds.Add(externalId);
        _graph.AddNode("ExternalSymbol", externalId, new Dictionary<string, object?>
        {
            ["name"] = displayName.Split('.').LastOrDefault() ?? displayName,
            ["displayName"] = displayName,
            ["kind"] = "type",
            ["assemblyName"] = assemblyName,
            ["external"] = true
        });
        return externalId;
    }

    /// <summary>加入「AddNamespace」所代表的圖譜抽取或匯入工作。</summary>
    private string AddNamespace(string namespaceName)
    {
        var namespaceId = GraphIds.Namespace(namespaceName);
        _graph.AddNode("Namespace", namespaceId, new Dictionary<string, object?>
        {
            ["name"] = namespaceName,
            ["fullName"] = namespaceName
        });
        return namespaceId;
    }

    /// <summary>加入「AddNamespaceDeclarations」所代表的圖譜抽取或匯入工作。</summary>
    private void AddNamespaceDeclarations(SyntaxNode root, string fileId)
    {
        foreach (var namespaceDeclaration in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var namespaceName = namespaceDeclaration.Name.ToString();
            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                _graph.AddRelationship("DECLARES_NAMESPACE", fileId, AddNamespace(namespaceName), LocationProperties(namespaceDeclaration));
            }
        }
    }

    /// <summary>加入「AddUsingRelationships」所代表的圖譜抽取或匯入工作。</summary>
    private void AddUsingRelationships(SyntaxNode root, string fileId)
    {
        foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (usingDirective.Alias is not null || usingDirective.Name is null)
            {
                continue;
            }

            var namespaceName = usingDirective.Name.ToString();
            var namespaceId = AddNamespace(namespaceName);
            _graph.AddRelationship("IMPORTS_NAMESPACE", fileId, namespaceId, LocationProperties(usingDirective));
        }
    }

    /// <summary>加入「AddChunk」所代表的圖譜抽取或匯入工作。</summary>
    private void AddChunk(string ownerId, string fileId, SyntaxNode node, string kind)
    {
        var fullText = node.ToFullString();
        var chunkId = GraphIds.Chunk(ownerId, fileId, node.SpanStart);
        _codeChunkIds.Add(chunkId);
        var properties = new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["ownerId"] = ownerId,
            ["fileId"] = fileId,
            ["startLine"] = GetLineSpan(node).StartLinePosition.Line + 1,
            ["endLine"] = GetLineSpan(node).EndLinePosition.Line + 1,
            ["language"] = "csharp"
        };

        if (_includeCodeChunkText)
        {
            var text = fullText.Length > MaxChunkCharacters ? fullText[..MaxChunkCharacters] : fullText;
            properties["text"] = text;
            properties["textHash"] = GraphIds.HashText(fullText);
            properties["truncated"] = fullText.Length > MaxChunkCharacters;
        }

        _graph.AddNode("CodeChunk", chunkId, properties);
        _graph.AddRelationship("HAS_CHUNK", ownerId, chunkId);
    }

    /// <summary>判斷「IsSupportedType」所代表的圖譜抽取或匯入工作。</summary>
    private static bool IsSupportedType(BaseTypeDeclarationSyntax node)
        => node is ClassDeclarationSyntax
            or InterfaceDeclarationSyntax
            or StructDeclarationSyntax
            or RecordDeclarationSyntax
            or EnumDeclarationSyntax;

    /// <summary>判斷「IsMethodLike」所代表的圖譜抽取或匯入工作。</summary>
    private static bool IsMethodLike(SyntaxNode node)
        => node is MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax;

    /// <summary>取得「GetTypeKind」所代表的圖譜抽取或匯入工作。</summary>
    private static string GetTypeKind(BaseTypeDeclarationSyntax node)
        => node switch
        {
            ClassDeclarationSyntax => "class",
            InterfaceDeclarationSyntax => "interface",
            StructDeclarationSyntax => "struct",
            RecordDeclarationSyntax => "record",
            EnumDeclarationSyntax => "enum",
            _ => "type"
        };

    /// <summary>取得「GetMethodKind」所代表的圖譜抽取或匯入工作。</summary>
    private static string GetMethodKind(SyntaxNode node)
        => node switch
        {
            ConstructorDeclarationSyntax => "constructor",
            DestructorDeclarationSyntax => "destructor",
            OperatorDeclarationSyntax => "operator",
            ConversionOperatorDeclarationSyntax => "conversion_operator",
            _ => "method"
        };

    /// <summary>取得「GetMethodName」所代表的圖譜抽取或匯入工作。</summary>
    private static string GetMethodName(SyntaxNode node, string containingTypeName)
        => node switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            DestructorDeclarationSyntax => ".dtor",
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.ValueText}",
            ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
            _ => containingTypeName.Split('.').LastOrDefault() ?? "method"
        };

    /// <summary>取得「GetParameterCount」所代表的圖譜抽取或匯入工作。</summary>
    private static int GetParameterCount(SyntaxNode node)
        => node switch
        {
            BaseMethodDeclarationSyntax method => method.ParameterList.Parameters.Count,
            _ => 0
        };

    /// <summary>取得「GetFallbackNamespaceName」所代表的圖譜抽取或匯入工作。</summary>
    private static string GetFallbackNamespaceName(SyntaxNode node)
    {
        var parts = node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(namespaceDeclaration => namespaceDeclaration.Name.ToString())
            .Reverse()
            .ToList();
        return string.Join('.', parts);
    }

    /// <summary>取得「GetFallbackTypeName」所代表的圖譜抽取或匯入工作。</summary>
    private static string GetFallbackTypeName(BaseTypeDeclarationSyntax node)
    {
        var typeParts = node.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(IsSupportedType)
            .Select(type => type.Identifier.ValueText)
            .Reverse()
            .Append(node.Identifier.ValueText);
        var namespaceName = GetFallbackNamespaceName(node);
        var typeName = string.Join('.', typeParts);
        return string.IsNullOrWhiteSpace(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
    }

    /// <summary>判斷「IsFrameworkRootType」所代表的圖譜抽取或匯入工作。</summary>
    private static bool IsFrameworkRootType(INamedTypeSymbol symbol)
    {
        var fullName = GraphSymbolFormatting.TypeName(symbol);
        return fullName is "System.Object"
            or "System.ValueType"
            or "System.Enum"
            or "System.Delegate"
            or "System.MulticastDelegate";
    }

    /// <summary>執行「LocationProperties」所代表的圖譜抽取或匯入工作。</summary>
    private static Dictionary<string, object?> LocationProperties(SyntaxNode node)
    {
        var span = GetLineSpan(node);
        return new Dictionary<string, object?>
        {
            ["startLine"] = span.StartLinePosition.Line + 1,
            ["endLine"] = span.EndLinePosition.Line + 1,
            ["startColumn"] = span.StartLinePosition.Character + 1,
            ["endColumn"] = span.EndLinePosition.Character + 1
        };
    }

    /// <summary>格式化「FormatLocation」所代表的圖譜抽取或匯入工作。</summary>
    private static string FormatLocation(SyntaxNode node)
    {
        var span = GetLineSpan(node);
        return $"{span.Path}:{span.StartLinePosition.Line + 1}";
    }

    /// <summary>取得「GetLineSpan」所代表的圖譜抽取或匯入工作。</summary>
    private static FileLinePositionSpan GetLineSpan(SyntaxNode node)
        => node.GetLocation().GetLineSpan();

    /// <summary>定義「TypeExtractionInfo」資料結構或服務職責，供圖譜抽取流程使用。</summary>
    private sealed record TypeExtractionInfo(string TypeId, string TypeName, INamedTypeSymbol? Symbol);
}
