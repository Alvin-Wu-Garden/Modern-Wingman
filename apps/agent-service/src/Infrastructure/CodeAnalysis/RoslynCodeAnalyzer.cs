using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace AgentService.Infrastructure.CodeAnalysis;

internal sealed record IncrementalRoslynAnalysis(
    bool Applied,
    string? EscalationReason,
    CodeAnalysisResult? Graph);

/// <summary>
/// Roslyn based C# static analyzer.
///
/// The analyzer deliberately builds one compilation for all supplied C# files.  This is
/// important: a file-by-file semantic model cannot resolve project-local calls,
/// overloads, interface members, partial types, or inheritance reliably.
/// </summary>
public sealed class RoslynCodeAnalyzer(ILogger<RoslynCodeAnalyzer> logger) : ICodeAnalyzer
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.Parse, SourceCodeKind.Regular);
    private static readonly SymbolDisplayFormat GraphSymbolFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeParameters |
                       SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public string Language => "csharp";
    public IReadOnlyList<string> FileExtensions => [".cs"];

    public async Task<CodeAnalysisResult> AnalyzeAsync(
        string projectRoot,
        IReadOnlyList<string> files,
        CancellationToken ct = default)
    {
        var result = new CodeAnalysisResult();
        var sources = new List<(string FilePath, string RelativePath, string Text, string ContentHash)>();

        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                using var reader = new StreamReader(
                    new MemoryStream(bytes, writable: false),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(ct);
                var relativePath = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                sources.Add((
                    file,
                    relativePath,
                    text,
                    contentHash));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "解析失敗，略過: {File}", file);
            }
        }

        var workspaceInputFingerprint = CSharpWorkspaceLoader.ComputeInputFingerprint(
            projectRoot,
            sources.Select(source => (source.RelativePath, source.ContentHash)));
        var workspaceSnapshot = await CSharpWorkspaceLoader.TryLoadAsync(
            projectRoot, files, workspaceInputFingerprint, logger, ct);
        var workspaceDocuments = workspaceSnapshot?.Documents
            .GroupBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, CSharpAnalysisDocument>(StringComparer.OrdinalIgnoreCase);
        var requestedRelativePaths = sources.Select(source => source.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workspaceComplete = workspaceSnapshot is not null &&
            requestedRelativePaths.All(workspaceDocuments.ContainsKey);
        List<SyntaxTree> fallbackTrees = workspaceComplete
            ? []
            : sources.Select(source => CSharpSyntaxTree.ParseText(
                    source.Text, ParseOptions, source.FilePath, cancellationToken: ct))
                .ToList();
        CSharpCompilation? fallbackCompilation = workspaceComplete
            ? null
            : CSharpCompilation.Create(
                "WingmanAnalysis",
                fallbackTrees,
                GetPlatformReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var fallbackTreesByPath = fallbackTrees.ToDictionary(
            tree => Path.GetFullPath(tree.FilePath),
            tree => tree,
            StringComparer.OrdinalIgnoreCase);

        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var seenEdges = new HashSet<(string Source, string Target, CodeEdgeKind Kind)>();
        var nodeArtifacts = new Dictionary<string, string?>(StringComparer.Ordinal);
        var seenMethodLocations = new HashSet<(string FilePath, int StartLine)>(
            MethodLocationComparer.Instance);

        void AddNode(CodeNode node)
        {
            if (seenNodes.Add(node.Key))
            {
                result.Nodes.Add(node);
                nodeArtifacts[node.Key] = node.FilePath;
                if (node.Kind == CodeNodeKind.Method &&
                    node.FilePath is not null &&
                    node.StartLine is not null)
                {
                    seenMethodLocations.Add((node.FilePath, node.StartLine.Value));
                }
            }
        }

        void AddEdge(string source, string target, CodeEdgeKind kind)
        {
            if (!string.Equals(source, target, StringComparison.Ordinal) &&
                seenEdges.Add((source, target, kind)))
            {
                result.Edges.Add(new CodeEdge
                {
                    SourceKey = source, TargetKey = target, Kind = kind,
                    SourceKind = GraphSourceKind.Compiler, Confidence = GraphConfidence.Exact,
                    ExtractorId = "roslyn", ExtractorVersion = "1.0.0",
                    ArtifactPath = nodeArtifacts.GetValueOrDefault(source),
                });
            }
        }

        var documents = new List<CSharpAnalysisDocument>(sources.Count);
        foreach (var (filePath, relativePath, _, contentHash) in sources)
        {
            ct.ThrowIfCancellationRequested();
            var fileKey = $"file:{relativePath}";
            AddNode(new CodeNode
            {
                Key = fileKey,
                Kind = CodeNodeKind.File,
                Name = Path.GetFileName(relativePath),
                FilePath = relativePath,
                Language = Language,
                SourceKind = GraphSourceKind.Compiler,
                Confidence = GraphConfidence.Exact,
                ExtractorId = "roslyn",
                ExtractorVersion = "1.0.0",
                ContentHash = contentHash,
            });
            if (workspaceDocuments.TryGetValue(relativePath, out var workspaceDocument))
            {
                documents.Add(workspaceDocument);
            }
            else
            {
                var tree = fallbackTreesByPath[Path.GetFullPath(filePath)];
                var root = await tree.GetRootAsync(ct);
                var model = fallbackCompilation!.GetSemanticModel(tree);
                documents.Add(new CSharpAnalysisDocument(root, model, relativePath, fileKey));
            }
        }

        if (workspaceSnapshot is null && fallbackCompilation is not null)
        {
            await CSharpWorkspaceLoader.RememberSyntheticAsync(
                projectRoot,
                workspaceInputFingerprint,
                documents,
                fallbackCompilation,
                ct);
        }

        // First pass declares every local type.  All following relationships can now
        // safely point across files regardless of source-file ordering.
        foreach (var document in documents)
        {
            foreach (var declaration in EnumerateTypeDeclarations(document.Root))
            {
                ct.ThrowIfCancellationRequested();
                if (document.Model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol typeSymbol)
                    continue;

                AddType(typeSymbol, declaration, document, AddNode, AddEdge);
            }
        }

        // Second pass only processes direct members.  DescendantNodes() here would
        // accidentally attach nested-type members to the enclosing type.
        foreach (var document in documents)
        {
            foreach (var declaration in EnumerateTypeDeclarations(document.Root))
            {
                ct.ThrowIfCancellationRequested();
                if (document.Model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol typeSymbol)
                    continue;

                var typeKey = SymbolKey(typeSymbol);
                AnalyzeDirectMembers(document.Model, declaration, typeSymbol, typeKey, document.RelativePath, AddNode, AddEdge, ct);
            }
        }

        // A declaration-first pass is the authority for callables.  Besides being
        // naturally overload-safe, it avoids relying on TypeDeclaration.Members for
        // explicit-interface and compiler-recovered declarations.
        foreach (var document in documents)
        {
            foreach (var declaration in document.Root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                ct.ThrowIfCancellationRequested();
                var methodSymbol = document.Model.GetDeclaredSymbol(declaration, ct) as IMethodSymbol
                    ?? FindDeclaredMethod(document.Model.Compilation, declaration);
                if (methodSymbol?.ContainingType is null)
                    continue;

                AnalyzeCallable(
                    document.Model,
                    declaration,
                    methodSymbol,
                    SymbolKey(methodSymbol.ContainingType),
                    document.RelativePath,
                    AddNode,
                    AddEdge,
                    ct);
            }
        }

        // Retain a syntax-derived local declaration when Roslyn cannot bind a member
        // because the workspace has incomplete references.  The key deliberately
        // follows Roslyn's fully-qualified C# display format for normal methods, so
        // already-resolved CALLS edges still attach to the declaration.
        foreach (var document in documents)
        {
            foreach (var declaration in document.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var ownerDeclaration = declaration.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                var owner = ownerDeclaration is null
                    ? null
                    : document.Model.GetDeclaredSymbol(ownerDeclaration, ct) as INamedTypeSymbol;
                if (owner is null)
                    continue;

                var parameters = string.Join(", ", declaration.ParameterList.Parameters
                    .Select(parameter => parameter.Type?.ToString() ?? "object"));
                var key = $"{SymbolKey(owner)}.{declaration.Identifier.ValueText}({parameters})";
                var span = declaration.GetLocation().GetLineSpan();
                if (seenMethodLocations.Contains((
                    document.RelativePath,
                    span.StartLinePosition.Line + 1)))
                {
                    continue;
                }
                AddNode(new CodeNode
                {
                    Key = key,
                    Kind = CodeNodeKind.Method,
                    Name = declaration.Identifier.ValueText,
                    Signature = key,
                    FilePath = document.RelativePath,
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1,
                    Language = Language,
                    SourceKind = GraphSourceKind.Compiler,
                    Confidence = GraphConfidence.Resolved,
                    ExtractorId = "roslyn-fallback",
                    ExtractorVersion = "1.0.0",
                    Reason = "Roslyn recovered a source declaration while references were incomplete",
                });
                AddEdge(SymbolKey(owner), key, CodeEdgeKind.Contains);
            }
        }

        var semanticCompilations = workspaceSnapshot?.Compilations ?? [fallbackCompilation!];
        foreach (var semanticCompilation in semanticCompilations)
            AddDispatchRelationships(semanticCompilation, AddEdge, ct);

        Merge(CSharpProjectGraphExtractor.Extract(
            projectRoot, sources.Select(source => source.FilePath).ToArray()));
        Merge(CSharpFrameworkGraphExtractor.Extract(documents));

        foreach (var edge in result.Edges)
            edge.ArtifactPath ??= nodeArtifacts.GetValueOrDefault(edge.SourceKey)
                ?? nodeArtifacts.GetValueOrDefault(edge.TargetKey);

        // Neo4j creates relationships with MATCH at both endpoints.  Keeping an edge
        // to an unresolved framework/external symbol is therefore misleading and has
        // no persisted effect.  Retain only graph-local, verifiable relationships.
        result.Edges.RemoveAll(edge => !seenNodes.Contains(edge.SourceKey) || !seenNodes.Contains(edge.TargetKey));

        if (logger.IsEnabled(LogLevel.Debug))
        {
            var errors = semanticCompilations.Sum(item =>
                item.GetDiagnostics(ct).Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            if (errors > 0)
            {
                logger.LogDebug("C# compilation has {ErrorCount} diagnostics; graph contains resolved local symbols only.", errors);
            }
        }

        // A content-complete cached snapshot can still be the restore-free synthetic
        // compilation created by the previous run.  Cache reuse must not silently
        // upgrade cross-project edges from heuristic to exact.
        if (!workspaceComplete || workspaceSnapshot?.IsSynthetic == true)
            CSharpCompilationTrustPolicy.Apply(result);
        CodeAnalysisProvenance.StampEdges(result, DateTimeOffset.UtcNow);

        logger.LogInformation(
            "Roslyn 分析完成: {Files} 檔案 → {Nodes} 節點, {Edges} 邊",
            sources.Count, result.Nodes.Count, result.Edges.Count);

        return result;

        void Merge(CodeAnalysisResult extracted)
        {
            foreach (var node in extracted.Nodes) AddNode(node);
            foreach (var edge in extracted.Edges)
            {
                if (edge.SourceKey == edge.TargetKey || !seenEdges.Add((edge.SourceKey, edge.TargetKey, edge.Kind))) continue;
                result.Edges.Add(edge);
            }
        }
    }

    internal async Task<IncrementalRoslynAnalysis> AnalyzeBodyChangesAsync(
        string projectRoot,
        IReadOnlyList<string> changedFiles,
        IReadOnlyDictionary<string, string> allCSharpContentHashes,
        IReadOnlySet<string> activeNodeKeys,
        CancellationToken ct = default)
    {
        var changes = new List<CSharpBodyChange>(changedFiles.Count);
        var changedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(file, ct);
            using var reader = new StreamReader(
                new MemoryStream(bytes, writable: false),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(ct);
            var relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            changes.Add(new(file, relative, text));
            changedHashes[relative] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        var inputFingerprint = CSharpWorkspaceLoader.ComputeInputFingerprint(
            projectRoot,
            allCSharpContentHashes.Select(pair => (pair.Key, pair.Value)));
        var update = await CSharpWorkspaceLoader.TryApplyBodyChangesAsync(
            projectRoot,
            changes,
            inputFingerprint,
            logger,
            ct);
        if (!update.Applied)
            return new(false, update.EscalationReason, null);

        var result = new CodeAnalysisResult();
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var seenEdges = new HashSet<(string Source, string Target, CodeEdgeKind Kind)>();
        var nodeArtifacts = new Dictionary<string, string?>(StringComparer.Ordinal);
        var seenMethodLocations = new HashSet<(string FilePath, int StartLine)>(
            MethodLocationComparer.Instance);

        void AddNode(CodeNode node)
        {
            if (!seenNodes.Add(node.Key)) return;
            result.Nodes.Add(node);
            nodeArtifacts[node.Key] = node.FilePath;
            if (node.Kind == CodeNodeKind.Method && node.FilePath is not null && node.StartLine is not null)
                seenMethodLocations.Add((node.FilePath, node.StartLine.Value));
        }

        void AddEdge(string source, string target, CodeEdgeKind kind)
        {
            if (string.Equals(source, target, StringComparison.Ordinal) ||
                !seenEdges.Add((source, target, kind))) return;
            result.Edges.Add(new CodeEdge
            {
                SourceKey = source,
                TargetKey = target,
                Kind = kind,
                SourceKind = GraphSourceKind.Compiler,
                Confidence = GraphConfidence.Exact,
                ExtractorId = "roslyn",
                ExtractorVersion = "1.0.0",
                ArtifactPath = nodeArtifacts.GetValueOrDefault(source),
            });
        }

        foreach (var document in update.Documents)
        {
            AddNode(new CodeNode
            {
                Key = document.FileKey,
                Kind = CodeNodeKind.File,
                Name = Path.GetFileName(document.RelativePath),
                FilePath = document.RelativePath,
                Language = Language,
                SourceKind = GraphSourceKind.Compiler,
                Confidence = GraphConfidence.Exact,
                ExtractorId = "roslyn",
                ExtractorVersion = "1.0.0",
                ContentHash = changedHashes[document.RelativePath],
            });
        }

        foreach (var document in update.Documents)
        foreach (var declaration in EnumerateTypeDeclarations(document.Root))
        {
            ct.ThrowIfCancellationRequested();
            if (document.Model.GetDeclaredSymbol(declaration, ct) is INamedTypeSymbol type)
                AddType(type, declaration, document, AddNode, AddEdge);
        }

        foreach (var document in update.Documents)
        foreach (var declaration in EnumerateTypeDeclarations(document.Root))
        {
            ct.ThrowIfCancellationRequested();
            if (document.Model.GetDeclaredSymbol(declaration, ct) is INamedTypeSymbol type)
                AnalyzeDirectMembers(
                    document.Model,
                    declaration,
                    type,
                    SymbolKey(type),
                    document.RelativePath,
                    AddNode,
                    AddEdge,
                    ct);
        }

        foreach (var document in update.Documents)
        foreach (var declaration in document.Root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            var method = document.Model.GetDeclaredSymbol(declaration, ct) as IMethodSymbol
                ?? FindDeclaredMethod(document.Model.Compilation, declaration);
            if (method?.ContainingType is null) continue;
            AnalyzeCallable(
                document.Model,
                declaration,
                method,
                SymbolKey(method.ContainingType),
                document.RelativePath,
                AddNode,
                AddEdge,
                ct);
        }

        foreach (var document in update.Documents)
        foreach (var declaration in document.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var ownerDeclaration = declaration.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            var owner = ownerDeclaration is null
                ? null
                : document.Model.GetDeclaredSymbol(ownerDeclaration, ct) as INamedTypeSymbol;
            if (owner is null) continue;
            var parameters = string.Join(", ", declaration.ParameterList.Parameters
                .Select(parameter => parameter.Type?.ToString() ?? "object"));
            var key = $"{SymbolKey(owner)}.{declaration.Identifier.ValueText}({parameters})";
            var span = declaration.GetLocation().GetLineSpan();
            if (seenMethodLocations.Contains((document.RelativePath, span.StartLinePosition.Line + 1)))
                continue;
            AddNode(new CodeNode
            {
                Key = key,
                Kind = CodeNodeKind.Method,
                Name = declaration.Identifier.ValueText,
                Signature = key,
                FilePath = document.RelativePath,
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
                Language = Language,
                SourceKind = GraphSourceKind.Compiler,
                Confidence = GraphConfidence.Resolved,
                ExtractorId = "roslyn-fallback",
                ExtractorVersion = "1.0.0",
                Reason = "Roslyn recovered a source declaration while references were incomplete",
            });
            AddEdge(SymbolKey(owner), key, CodeEdgeKind.Contains);
        }

        foreach (var compilation in update.AffectedCompilations)
            AddDispatchRelationships(
                compilation,
                (source, target, kind) =>
                {
                    if (seenNodes.Contains(source)) AddEdge(source, target, kind);
                },
                ct);

        Merge(CSharpFrameworkGraphExtractor.Extract(update.Documents));
        foreach (var edge in result.Edges)
            edge.ArtifactPath ??= nodeArtifacts.GetValueOrDefault(edge.SourceKey)
                ?? nodeArtifacts.GetValueOrDefault(edge.TargetKey);
        result.Edges.RemoveAll(edge =>
            !seenNodes.Contains(edge.SourceKey) ||
            (!seenNodes.Contains(edge.TargetKey) && !activeNodeKeys.Contains(edge.TargetKey)));
        CodeAnalysisProvenance.StampEdges(result, DateTimeOffset.UtcNow);
        if (update.IsSynthetic)
            CSharpCompilationTrustPolicy.Apply(result);
        return new(true, null, result);

        void Merge(CodeAnalysisResult extracted)
        {
            foreach (var node in extracted.Nodes) AddNode(node);
            foreach (var edge in extracted.Edges)
            {
                if (edge.SourceKey == edge.TargetKey ||
                    !seenEdges.Add((edge.SourceKey, edge.TargetKey, edge.Kind))) continue;
                edge.ArtifactPath ??= nodeArtifacts.GetValueOrDefault(edge.SourceKey)
                    ?? nodeArtifacts.GetValueOrDefault(edge.TargetKey);
                result.Edges.Add(edge);
            }
        }
    }

    private sealed class MethodLocationComparer : IEqualityComparer<(string FilePath, int StartLine)>
    {
        public static readonly MethodLocationComparer Instance = new();

        public bool Equals(
            (string FilePath, int StartLine) left,
            (string FilePath, int StartLine) right) =>
            left.StartLine == right.StartLine &&
            string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string FilePath, int StartLine) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.FilePath),
                value.StartLine);
    }

    private void AddType(
        INamedTypeSymbol typeSymbol,
        MemberDeclarationSyntax declaration,
        CSharpAnalysisDocument document,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge)
    {
        var typeKey = SymbolKey(typeSymbol);
        var span = declaration.GetLocation().GetLineSpan();
        addNode(new CodeNode
        {
            Key = typeKey,
            Kind = CodeNodeKind.Type,
            Name = typeSymbol.Name,
            Signature = typeSymbol.ToDisplayString(),
            FilePath = document.RelativePath,
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
            Language = Language,
            DocComment = ExtractDocSummary(typeSymbol),
            SourceKind = GraphSourceKind.Compiler,
            Confidence = GraphConfidence.Exact,
            ExtractorId = "roslyn",
            ExtractorVersion = "1.0.0",
        });
        addEdge(typeKey, document.FileKey, CodeEdgeKind.DeclaredIn);

        if (typeSymbol.ContainingType is not null)
        {
            addEdge(SymbolKey(typeSymbol.ContainingType), typeKey, CodeEdgeKind.Contains);
        }
        else if (typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            var namespaceKey = $"ns:{ns.ToDisplayString()}";
            addNode(new CodeNode
            {
                Key = namespaceKey,
                Kind = CodeNodeKind.Namespace,
                Name = ns.ToDisplayString(),
                Language = Language,
                SourceKind = GraphSourceKind.Compiler,
                Confidence = GraphConfidence.Exact,
                ExtractorId = "roslyn",
                ExtractorVersion = "1.0.0",
            });
            addEdge(namespaceKey, typeKey, CodeEdgeKind.Contains);
        }

        if (typeSymbol.BaseType is { SpecialType: SpecialType.None } baseType)
            addEdge(typeKey, SymbolKey(baseType), CodeEdgeKind.Inherits);

        foreach (var iface in typeSymbol.Interfaces)
            addEdge(typeKey, SymbolKey(iface), CodeEdgeKind.Implements);
    }

    private void AnalyzeDirectMembers(
        SemanticModel model,
        MemberDeclarationSyntax declaration,
        INamedTypeSymbol typeSymbol,
        string typeKey,
        string relativePath,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge,
        CancellationToken ct)
    {
        if (declaration is DelegateDeclarationSyntax delegateDeclaration)
        {
            if (model.GetDeclaredSymbol(delegateDeclaration, ct) is INamedTypeSymbol delegateSymbol)
            {
                foreach (var parameter in delegateSymbol.DelegateInvokeMethod?.Parameters ?? [])
                    AddTypeReference(typeKey, parameter.Type, addEdge);
                AddTypeReference(typeKey, delegateSymbol.DelegateInvokeMethod?.ReturnType, addEdge);
            }
            return;
        }

        if (declaration is not TypeDeclarationSyntax typeDeclaration)
            return;

        // Register all declared callable symbols before walking bodies.  This keeps
        // call targets available even when a body cannot be bound (for example an
        // incomplete dependency restore) and gives overloads distinct graph nodes.
        foreach (var methodSymbol in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (methodSymbol.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor))
                continue;

            var sourceLocation = methodSymbol.Locations.FirstOrDefault(location => location.IsInSource);
            if (sourceLocation is not null)
                AddCallableNode(methodSymbol, sourceLocation, typeKey, relativePath, addNode, addEdge);
        }

        foreach (var member in typeDeclaration.Members)
        {
            ct.ThrowIfCancellationRequested();
            switch (member)
            {
                case PropertyDeclarationSyntax property:
                    AnalyzeProperty(model, property, typeKey, relativePath, addNode, addEdge, ct);
                    break;
                case IndexerDeclarationSyntax indexer:
                    AnalyzeProperty(model, indexer, typeKey, relativePath, addNode, addEdge, ct);
                    break;
                case FieldDeclarationSyntax field:
                    AnalyzeFields(model, field, typeKey, relativePath, addNode, addEdge, ct);
                    break;
                case EventFieldDeclarationSyntax eventField:
                    AnalyzeEventFields(model, eventField, typeKey, relativePath, addNode, addEdge, ct);
                    break;
            }
        }
    }

    private void AnalyzeCallable(
        SemanticModel model,
        BaseMethodDeclarationSyntax declaration,
        IMethodSymbol methodSymbol,
        string typeKey,
        string relativePath,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge,
        CancellationToken ct)
    {
        var methodKey = AddCallableNode(
            methodSymbol,
            declaration.GetLocation(),
            typeKey,
            relativePath,
            addNode,
            addEdge);

        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            var symbolInfo = model.GetSymbolInfo(invocation, ct);
            // An incomplete source-only compilation can still expose one overload-safe
            // candidate even when Roslyn cannot promote it to Symbol (for example while
            // a project reference has diagnostics).  A single candidate is evidence;
            // multiple candidates remain intentionally unresolved rather than guessing.
            var uniqueCandidates = symbolInfo.CandidateSymbols
                .OfType<IMethodSymbol>()
                .Select(candidate => candidate.OriginalDefinition)
                .GroupBy(SymbolKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(2)
                .ToArray();
            var calledSymbol = symbolInfo.Symbol as IMethodSymbol
                ?? (uniqueCandidates.Length == 1 ? uniqueCandidates[0] : null);
            if (calledSymbol is not null)
            {
                addEdge(methodKey, SymbolKey(calledSymbol.OriginalDefinition), CodeEdgeKind.Calls);
            }
        }

        foreach (var creation in declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (model.GetSymbolInfo(creation, ct).Symbol is IMethodSymbol constructor)
                addEdge(methodKey, SymbolKey(constructor.OriginalDefinition), CodeEdgeKind.Calls);

            AddTypeReference(methodKey, model.GetTypeInfo(creation, ct).Type, addEdge);
        }
    }

    private string AddCallableNode(
        IMethodSymbol methodSymbol,
        Location location,
        string typeKey,
        string relativePath,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge)
    {
        var methodKey = SymbolKey(methodSymbol);
        var span = location.GetLineSpan();
        addNode(new CodeNode
        {
            Key = methodKey,
            Kind = CodeNodeKind.Method,
            Name = methodSymbol.MethodKind == MethodKind.Constructor ? methodSymbol.ContainingType.Name : methodSymbol.Name,
            Signature = methodSymbol.ToDisplayString(),
            FilePath = relativePath,
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
            Language = Language,
            DocComment = ExtractDocSummary(methodSymbol),
            SourceKind = GraphSourceKind.Compiler,
            Confidence = GraphConfidence.Exact,
            ExtractorId = "roslyn",
            ExtractorVersion = "1.0.0",
        });
        addEdge(typeKey, methodKey, CodeEdgeKind.Contains);

        AddTypeReference(methodKey, methodSymbol.ReturnType, addEdge);
        foreach (var parameter in methodSymbol.Parameters)
            AddTypeReference(methodKey, parameter.Type, addEdge);
        return methodKey;
    }

    private void AnalyzeProperty(
        SemanticModel model,
        BasePropertyDeclarationSyntax declaration,
        string typeKey,
        string relativePath,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge,
        CancellationToken ct)
    {
        if (model.GetDeclaredSymbol(declaration, ct) is not IPropertySymbol propertySymbol)
            return;

        var propertyKey = SymbolKey(propertySymbol);
        var span = declaration.GetLocation().GetLineSpan();
        addNode(new CodeNode
        {
            Key = propertyKey,
            Kind = CodeNodeKind.Property,
            Name = propertySymbol.Name,
            Signature = propertySymbol.ToDisplayString(),
            FilePath = relativePath,
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
            Language = Language,
            DocComment = ExtractDocSummary(propertySymbol),
            SourceKind = GraphSourceKind.Compiler,
            Confidence = GraphConfidence.Exact,
            ExtractorId = "roslyn",
            ExtractorVersion = "1.0.0",
        });
        addEdge(typeKey, propertyKey, CodeEdgeKind.Contains);
        AddTypeReference(propertyKey, propertySymbol.Type, addEdge);
    }

    private void AnalyzeFields(
        SemanticModel model,
        FieldDeclarationSyntax declaration,
        string typeKey,
        string relativePath,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge,
        CancellationToken ct)
    {
        foreach (var variable in declaration.Declaration.Variables)
        {
            if (model.GetDeclaredSymbol(variable, ct) is not IFieldSymbol fieldSymbol)
                continue;

            AddField(fieldSymbol, variable.GetLocation(), typeKey, relativePath, addNode, addEdge);
        }
    }

    private void AnalyzeEventFields(
        SemanticModel model,
        EventFieldDeclarationSyntax declaration,
        string typeKey,
        string relativePath,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge,
        CancellationToken ct)
    {
        foreach (var variable in declaration.Declaration.Variables)
        {
            if (model.GetDeclaredSymbol(variable, ct) is not IEventSymbol eventSymbol)
                continue;

            // The shared model has no Event node yet.  Keeping the event as a Field
            // preserves its declaration and type reference without inventing a kind.
            var span = variable.GetLocation().GetLineSpan();
            var eventKey = SymbolKey(eventSymbol);
            addNode(new CodeNode
            {
                Key = eventKey,
                Kind = CodeNodeKind.Field,
                Name = eventSymbol.Name,
                Signature = eventSymbol.ToDisplayString(),
                FilePath = relativePath,
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
                Language = Language,
                DocComment = ExtractDocSummary(eventSymbol),
                SourceKind = GraphSourceKind.Compiler,
                Confidence = GraphConfidence.Exact,
                ExtractorId = "roslyn",
                ExtractorVersion = "1.0.0",
            });
            addEdge(typeKey, eventKey, CodeEdgeKind.Contains);
            AddTypeReference(eventKey, eventSymbol.Type, addEdge);
        }
    }

    private void AddField(
        IFieldSymbol fieldSymbol,
        Location location,
        string typeKey,
        string relativePath,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge)
    {
        var span = location.GetLineSpan();
        var fieldKey = SymbolKey(fieldSymbol);
        addNode(new CodeNode
        {
            Key = fieldKey,
            Kind = CodeNodeKind.Field,
            Name = fieldSymbol.Name,
            Signature = fieldSymbol.ToDisplayString(),
            FilePath = relativePath,
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
            Language = Language,
            DocComment = ExtractDocSummary(fieldSymbol),
            SourceKind = GraphSourceKind.Compiler,
            Confidence = GraphConfidence.Exact,
            ExtractorId = "roslyn",
            ExtractorVersion = "1.0.0",
        });
        addEdge(typeKey, fieldKey, CodeEdgeKind.Contains);
        AddTypeReference(fieldKey, fieldSymbol.Type, addEdge);
    }

    private static void AddTypeReference(
        string sourceKey,
        ITypeSymbol? type,
        Action<string, string, CodeEdgeKind> addEdge)
    {
        if (type is not INamedTypeSymbol { SpecialType: SpecialType.None } namedType)
            return;

        addEdge(sourceKey, SymbolKey(namedType.OriginalDefinition), CodeEdgeKind.References);
        foreach (var typeArgument in namedType.TypeArguments)
            AddTypeReference(sourceKey, typeArgument, addEdge);
    }

    private static IMethodSymbol? FindDeclaredMethod(
        Compilation compilation,
        BaseMethodDeclarationSyntax declaration)
    {
        var name = declaration switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var matches = compilation.GetSymbolsWithName(name, SymbolFilter.Member)
            .OfType<IMethodSymbol>()
            .Where(symbol => symbol.DeclaringSyntaxReferences.Any(reference =>
                reference.Span.Start == declaration.SpanStart &&
                string.Equals(reference.SyntaxTree.FilePath, declaration.SyntaxTree.FilePath, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(SymbolKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static void AddDispatchRelationships(
        Compilation compilation,
        Action<string, string, CodeEdgeKind> addEdge,
        CancellationToken ct)
    {
        foreach (var type in EnumerateNamedTypes(compilation.Assembly.GlobalNamespace))
        {
            ct.ThrowIfCancellationRequested();
            if (!type.Locations.Any(location => location.IsInSource)) continue;
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!method.Locations.Any(location => location.IsInSource)) continue;
                var methodKey = SymbolKey(method);
                if (method.OverriddenMethod is { } overridden)
                    addEdge(methodKey, SymbolKey(overridden.OriginalDefinition), CodeEdgeKind.Overrides);
                foreach (var implemented in method.ExplicitInterfaceImplementations)
                {
                    var interfaceKey = SymbolKey(implemented.OriginalDefinition);
                    addEdge(methodKey, interfaceKey, CodeEdgeKind.Implements);
                    addEdge(interfaceKey, methodKey, CodeEdgeKind.DispatchesTo);
                }
            }

            foreach (var iface in type.AllInterfaces)
            {
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation ||
                        !implementation.Locations.Any(location => location.IsInSource)) continue;
                    var interfaceKey = SymbolKey(member.OriginalDefinition);
                    var implementationKey = SymbolKey(implementation.OriginalDefinition);
                    addEdge(implementationKey, interfaceKey, CodeEdgeKind.Implements);
                    addEdge(interfaceKey, implementationKey, CodeEdgeKind.DispatchesTo);
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamespaceOrTypeSymbol owner)
    {
        foreach (var member in owner.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                foreach (var nested in EnumerateNamedTypes(ns)) yield return nested;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in EnumerateNamedTypes(type)) yield return nested;
            }
        }
    }

    private static IEnumerable<MemberDeclarationSyntax> EnumerateTypeDeclarations(SyntaxNode root) =>
        root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(static declaration => declaration is
            BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

    private static IEnumerable<MetadataReference> GetPlatformReferences()
    {
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            return trustedPlatformAssemblies
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();
        }

        return [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
    }

    /// <summary>Symbol to a stable, overload-safe graph key.</summary>
    internal static string SymbolKey(ISymbol symbol) =>
        symbol.ToDisplayString(GraphSymbolFormat);

    private static string? ExtractDocSummary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        var start = xml.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        if (start < 0 || end < 0)
            return null;

        var summary = xml[(start + "<summary>".Length)..end].Trim();
        return summary.Length > 500 ? summary[..500] : summary;
    }

}
