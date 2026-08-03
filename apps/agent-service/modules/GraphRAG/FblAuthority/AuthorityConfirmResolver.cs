using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>整合 mapping table 與 enum 兩個權威來源，建立 Menu、CodeClass 與 ConfirmSourceType 關係。</summary>
public sealed class ConfirmResolver
{
    private readonly CSharpSourceIndex _sourceIndex;
    private readonly ConfirmSourceTypeIndex _confirmSourceIndex;
    private readonly IReadOnlyList<ConfirmMappingItem> _mappings;

    /// <summary>建立 Confirm Resolver；mapping 或 enum 任一方存在皆可建立來源型別節點。</summary>
    public ConfirmResolver(
        CSharpSourceIndex sourceIndex,
        ConfirmSourceTypeIndex confirmSourceIndex,
        IReadOnlyList<ConfirmMappingItem> mappings)
    {
        _sourceIndex = sourceIndex;
        _confirmSourceIndex = confirmSourceIndex;
        _mappings = mappings;
    }

    /// <summary>擴充 DB-backed Menu 配對與程式碼中的 enum 使用關係。</summary>
    public ExtractionResult Resolve(ExtractionResult input)
    {
        var builder = GraphDocumentBuilder.FromDocument(input.Document, GraphBuildStage.StandardWebExtraction);
        var issues = input.Issues.ToList();
        var menuKeys = input.Document.Nodes
            .Where(node => node.Kind == GraphNodeKind.Menu)
            .Select(node => node.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var mapping in _mappings)
        {
            ResolveMapping(builder, mapping, menuKeys, issues);
        }

        var inScopeConfirmSources = _mappings
            .Where(mapping => menuKeys.Contains($"menu:{mapping.ConfirmMenuId}")
                || (mapping.MaintainMenuId != 0 && menuKeys.Contains($"menu:{mapping.MaintainMenuId}")))
            .Select(mapping => mapping.ConfirmSourceType)
            .ToHashSet();

        foreach (var codeNode in input.Document.Nodes.Where(node => node.Kind == GraphNodeKind.CodeClass))
        {
            ResolveCodeReferences(builder, codeNode);
        }

        // Upload 類別不一定已由 Web Action 直接到達，但其 Category 與 ConfirmSource 是明確來源事實。
        foreach (var uploadType in _sourceIndex.Types.Where(type => type.Parts.Any(part =>
                     part.RelativePath.StartsWith("BZConfirmUtility/Upload/", StringComparison.OrdinalIgnoreCase))))
        {
            ResolveUploadType(builder, uploadType, inScopeConfirmSources);
        }

        return new ExtractionResult(builder.Build(), issues);
    }

    /// <summary>由一筆 mapping 建立 Maintain→Confirm、Menu→ConfirmSource 關係。</summary>
    private void ResolveMapping(
        GraphDocumentBuilder builder,
        ConfirmMappingItem mapping,
        IReadOnlySet<string> menuKeys,
        ICollection<PreflightIssue> issues)
    {
        var confirmSourceKey = AddConfirmSourceNode(builder, mapping.ConfirmSourceType, GraphSourceKind.DatabaseRow);
        var confirmMenuKey = $"menu:{mapping.ConfirmMenuId}";
        var maintainMenuKey = $"menu:{mapping.MaintainMenuId}";
        var evidence = new GraphEvidence
        {
            SourceKind = GraphSourceKind.DatabaseRow,
            DatabaseObject = "dbo.tblAsyncConfirmSourceTypeMapping",
            RowKey = $"{mapping.ConfirmSourceType}:{mapping.ConfirmMenuId}:{mapping.MaintainMenuId}",
            SourceText = mapping.WaitForConfirmName,
        };

        if (menuKeys.Contains(confirmMenuKey))
        {
            builder.AddRelationship(
                GraphRelationshipKind.AcceptsConfirmSource,
                confirmMenuKey,
                confirmSourceKey,
                evidence);
        }

        if (mapping.MaintainMenuId != 0 && menuKeys.Contains(maintainMenuKey))
        {
            builder.AddRelationship(
                GraphRelationshipKind.UsesConfirmSource,
                maintainMenuKey,
                confirmSourceKey,
                evidence);
        }

        if (mapping.MaintainMenuId != 0
            && menuKeys.Contains(maintainMenuKey)
            && menuKeys.Contains(confirmMenuKey))
        {
            builder.AddRelationship(
                GraphRelationshipKind.ConfirmedBy,
                maintainMenuKey,
                confirmMenuKey,
                evidence);
        }

        // mapping 可合法含歷史或範圍外 Menu；保留 Log 但不把它混入696中心圖。
        if (!menuKeys.Contains(confirmMenuKey)
            || (mapping.MaintainMenuId != 0 && !menuKeys.Contains(maintainMenuKey)))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Information,
                PreflightReasonCode.ConfirmMenuPairUnresolved,
                "Confirm mapping 指向696中心範圍外 Menu，已保留來源節點但未建立 Menu 關係。",
                MenuId: mapping.MaintainMenuId == 0 ? null : mapping.MaintainMenuId.ToString(),
                FromKey: confirmMenuKey,
                TargetText: confirmSourceKey));
        }
    }

    /// <summary>掃描已可到達 CodeClass 直接使用的 enumConfirmSourceType 成員。</summary>
    private void ResolveCodeReferences(GraphDocumentBuilder builder, GraphNode codeNode)
    {
        var fullName = codeNode.Properties.GetValueOrDefault("full_name")?.ToString();
        var type = string.IsNullOrWhiteSpace(fullName) ? null : _sourceIndex.FindTypeByFullName(fullName);
        if (type is null)
        {
            return;
        }

        foreach (var reference in FindConfirmReferences(type))
        {
            var sourceKey = AddConfirmSourceNode(builder, reference.Member.Value, GraphSourceKind.SourceCode);
            builder.AddRelationship(
                GraphRelationshipKind.UsesConfirmSource,
                codeNode.Key,
                sourceKey,
                CreateEnumEvidence(reference));
        }
    }

    /// <summary>把 Upload 類別及其中直接出現的 CategoryType 納入，不做跨類別猜測。</summary>
    private void ResolveUploadType(
        GraphDocumentBuilder builder,
        IndexedCSharpType uploadType,
        IReadOnlySet<int> inScopeConfirmSources)
    {
        var confirmReferences = FindConfirmReferences(uploadType)
            .Where(reference => inScopeConfirmSources.Contains(reference.Member.Value))
            .ToArray();
        var categoryReferences = FindCategoryReferences(uploadType);
        if (confirmReferences.Length == 0)
        {
            return;
        }

        CodeClassNodeFactory.Add(builder, uploadType, CodeClassRole.UploadHandler);
        foreach (var reference in confirmReferences)
        {
            var sourceKey = AddConfirmSourceNode(builder, reference.Member.Value, GraphSourceKind.SourceCode);
            builder.AddRelationship(
                GraphRelationshipKind.UsesConfirmSource,
                $"code:{uploadType.FullName}",
                sourceKey,
                CreateEnumEvidence(reference));
        }

        foreach (var reference in categoryReferences)
        {
            var categoryKey = AddCategoryNode(builder, reference.Name);
            builder.AddRelationship(
                GraphRelationshipKind.ResolvesTo,
                categoryKey,
                $"code:{uploadType.FullName}",
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.SourceCode,
                    SourceFile = reference.SourceFile,
                    SourceLine = reference.SourceLine,
                    SourceText = $"CategoryType.{reference.Name}",
                });
        }
    }

    /// <summary>建立 ConfirmSourceType 節點並補上 enum 名稱；mapping 單獨存在時名稱可為空。</summary>
    private string AddConfirmSourceNode(
        GraphDocumentBuilder builder,
        int value,
        GraphSourceKind sourceKind)
    {
        var member = _confirmSourceIndex.FindByValue(value);
        var key = $"confirm-source:{value}";
        builder.AddNode(
            GraphNodeKind.ConfirmSourceType,
            key,
            new Dictionary<string, object?>
            {
                ["value"] = value,
                ["name"] = member?.Name,
                ["source_kind"] = sourceKind.ToString(),
            });
        return key;
    }

    /// <summary>建立 CategoryType 節點。</summary>
    private static string AddCategoryNode(GraphDocumentBuilder builder, string name)
    {
        var key = $"category:{name}";
        builder.AddNode(
            GraphNodeKind.CategoryType,
            key,
            new Dictionary<string, object?> { ["name"] = name });
        return key;
    }

    /// <summary>尋找型別本文中的 enumConfirmSourceType.Member。</summary>
    private IReadOnlyList<ConfirmReference> FindConfirmReferences(IndexedCSharpType type)
    {
        var result = new Dictionary<string, ConfirmReference>(StringComparer.Ordinal);
        foreach (var part in type.Parts)
        {
            foreach (var access in part.Syntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (!access.Expression.ToString().EndsWith("enumConfirmSourceType", StringComparison.Ordinal))
                {
                    continue;
                }

                var member = _confirmSourceIndex.FindByName(access.Name.Identifier.ValueText);
                if (member is not null)
                {
                    result.TryAdd(
                        $"{member.Name}:{part.RelativePath}:{GetSourceLine(access)}",
                        new ConfirmReference(member, part.RelativePath, GetSourceLine(access)));
                }
            }
        }

        return result.Values.ToArray();
    }

    /// <summary>尋找 Upload 本文中的 CategoryType.Member。</summary>
    private static IReadOnlyList<CategoryReference> FindCategoryReferences(IndexedCSharpType type)
    {
        var result = new Dictionary<string, CategoryReference>(StringComparer.Ordinal);
        foreach (var part in type.Parts)
        {
            foreach (var access in part.Syntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (!access.Expression.ToString().EndsWith("CategoryType", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = access.Name.Identifier.ValueText;
                result.TryAdd(name, new CategoryReference(name, part.RelativePath, GetSourceLine(access)));
            }
        }

        return result.Values.ToArray();
    }

    /// <summary>建立 enum 直接引用的證據。</summary>
    private static GraphEvidence CreateEnumEvidence(ConfirmReference reference) => new()
    {
        SourceKind = GraphSourceKind.SourceCode,
        SourceFile = reference.SourceFile,
        SourceLine = reference.SourceLine,
        SourceText = $"enumConfirmSourceType.{reference.Member.Name}",
    };

    /// <summary>取得 Roslyn 節點的 1-based 行號。</summary>
    private static int GetSourceLine(Microsoft.CodeAnalysis.SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>保存 enum 引用及其實際使用位置。</summary>
    private sealed record ConfirmReference(
        ConfirmSourceTypeMember Member,
        string SourceFile,
        int SourceLine);

    /// <summary>保存 CategoryType 引用及其實際使用位置。</summary>
    private sealed record CategoryReference(
        string Name,
        string SourceFile,
        int SourceLine);
}

