using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>解析 DataBuilder switch 中 CategoryType 到 Transform 實作的直接派送關係。</summary>
public sealed class FileTransformResolver
{
    private readonly CSharpSourceIndex _sourceIndex;

    /// <summary>建立以 Roslyn C# 索引為唯一來源的 FileTransform Resolver。</summary>
    public FileTransformResolver(CSharpSourceIndex sourceIndex)
    {
        _sourceIndex = sourceIndex;
    }

    /// <summary>掃描 DataBuilder 與 BatchDataBuilder partial 實作並擴充圖。</summary>
    public ExtractionResult Resolve(ExtractionResult input)
    {
        var builder = GraphDocumentBuilder.FromDocument(input.Document, GraphBuildStage.StandardWebExtraction);
        var issues = input.Issues.ToList();
        var reachableCategories = input.Document.Nodes
            .Where(node => node.Kind == GraphNodeKind.CategoryType)
            .Select(node => node.Properties.GetValueOrDefault("name")?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
        var dataBuilders = _sourceIndex.Types.Where(type =>
                type.Name == "DataBuilder"
                && type.Parts.Any(part => part.RelativePath.StartsWith("FileTransform/", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (var dataBuilder in dataBuilders)
        {
            ResolveDataBuilder(builder, dataBuilder, reachableCategories, issues);
        }

        return new ExtractionResult(builder.Build(), issues);
    }

    /// <summary>解析同一 switch section 中的 Category case 與 Transform object creation。</summary>
    private void ResolveDataBuilder(
        GraphDocumentBuilder builder,
        IndexedCSharpType dataBuilder,
        IReadOnlySet<string> reachableCategories,
        ICollection<PreflightIssue> issues)
    {
        CodeClassNodeFactory.Add(builder, dataBuilder, CodeClassRole.Builder);
        foreach (var part in dataBuilder.Parts)
        {
            foreach (var section in part.Syntax.DescendantNodes().OfType<SwitchSectionSyntax>())
            {
                var categories = section.Labels
                    .OfType<CaseSwitchLabelSyntax>()
                    .Select(label => TryReadCategoryName(label.Value))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Where(reachableCategories.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (categories.Length == 0)
                {
                    continue;
                }

                foreach (var creation in section.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                             .Where(item => item.Type.ToString().Split('.').Last()
                                 .StartsWith("Transform", StringComparison.Ordinal)))
                {
                    var candidates = ResolveTypeCandidates(creation.Type.ToString(), part.Syntax);
                    if (candidates.Count != 1)
                    {
                        issues.Add(new PreflightIssue(
                            PreflightSeverity.Error,
                            PreflightReasonCode.TransformDispatchUnresolved,
                            $"DataBuilder 無法唯一解析 Transform '{creation.Type}'。",
                            FromKey: $"code:{dataBuilder.FullName}",
                            TargetText: creation.Type.ToString(),
                            SourceFile: part.RelativePath,
                            SourceLine: GetSourceLine(creation),
                            Candidates: candidates.Select(candidate => candidate.FullName).ToArray()));
                        continue;
                    }

                    var transform = candidates[0];
                    CodeClassNodeFactory.Add(builder, transform, CodeClassRole.Transform);
                    builder.AddRelationship(
                        GraphRelationshipKind.CreatesTransform,
                        $"code:{dataBuilder.FullName}",
                        $"code:{transform.FullName}",
                        new GraphEvidence
                        {
                            SourceKind = GraphSourceKind.SourceCode,
                            SourceFile = part.RelativePath,
                            SourceLine = GetSourceLine(creation),
                            SourceText = $"new {creation.Type}(...)以上述 switch section 為準",
                        });

                    foreach (var category in categories)
                    {
                        var categoryKey = $"category:{category}";
                        builder.AddNode(
                            GraphNodeKind.CategoryType,
                            categoryKey,
                            new Dictionary<string, object?> { ["name"] = category });
                        builder.AddRelationship(
                            GraphRelationshipKind.ResolvesTo,
                            categoryKey,
                            $"code:{transform.FullName}",
                            new GraphEvidence
                            {
                                SourceKind = GraphSourceKind.SourceCode,
                                SourceFile = part.RelativePath,
                                SourceLine = GetSourceLine(section.Labels[0]),
                                SourceText = $"case CategoryType.{category}: new {creation.Type}(...) ",
                            });
                    }
                }
            }
        }
    }

    /// <summary>只接受 CategoryType.Member 形式的 case label。</summary>
    private static string? TryReadCategoryName(ExpressionSyntax expression)
    {
        return expression is MemberAccessExpressionSyntax access
            && access.Expression.ToString().EndsWith("CategoryType", StringComparison.Ordinal)
                ? access.Name.Identifier.ValueText
                : null;
    }

    /// <summary>以 fully-qualified name 或該檔 using namespace 唯一解析 Transform 類別。</summary>
    private IReadOnlyList<IndexedCSharpType> ResolveTypeCandidates(
        string typeText,
        ClassDeclarationSyntax declaration)
    {
        var normalized = typeText.Replace("global::", string.Empty, StringComparison.Ordinal);
        if (normalized.Contains('.'))
        {
            var exact = _sourceIndex.FindTypeByFullName(normalized);
            return exact is null ? Array.Empty<IndexedCSharpType>() : new[] { exact };
        }

        var candidates = _sourceIndex.FindTypes(normalized);
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var usingNamespaces = declaration.SyntaxTree.GetCompilationUnitRoot().Usings
            .Where(usingDirective => usingDirective.Alias is null
                && !usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            .Select(usingDirective => usingDirective.Name?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        var imported = candidates.Where(candidate =>
            usingNamespaces.Contains(GetNamespace(candidate.FullName))).ToArray();
        return imported.Length > 0 ? imported : candidates;
    }

    /// <summary>從 fully-qualified name 取得 namespace。</summary>
    private static string GetNamespace(string fullName)
    {
        var index = fullName.LastIndexOf('.');
        return index < 0 ? string.Empty : fullName[..index];
    }

    /// <summary>取得 Roslyn 節點的 1-based 行號。</summary>
    private static int GetSourceLine(Microsoft.CodeAnalysis.SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}

