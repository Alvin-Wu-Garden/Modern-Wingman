using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentService.UnitTests;

/// <summary>
/// 防止破壞式重構後的 GraphRAG 核心退化成沒有設計理由的巨型實作。
/// 此閘門只檢查公開契約與明確列出的重要 internal helper，不要求每一行都有註解。
/// </summary>
public sealed partial class GraphRAGChineseDocumentationTests
{
    private static readonly IReadOnlySet<string> ImportantInternalTypes =
        new HashSet<string>(["GraphCollections"], StringComparer.Ordinal);

    [Fact]
    public void ProductionModule_PublicContractsHaveChineseXmlDocumentation()
    {
        var module = FindModuleDirectory();
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(module, "*.cs")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("TODO: comment later", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TBD", source, StringComparison.OrdinalIgnoreCase);
            var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
            foreach (var declaration in root.DescendantNodes()
                         .Where(RequiresDocumentation))
            {
                var documentation = string.Concat(
                    declaration.GetLeadingTrivia()
                        .Where(trivia => trivia.IsKind(
                            SyntaxKind.SingleLineDocumentationCommentTrivia))
                        .Select(trivia => trivia.ToFullString()));
                if (documentation.Contains("<inheritdoc", StringComparison.OrdinalIgnoreCase) ||
                    ChineseCharacterRegex().IsMatch(documentation))
                    continue;
                var line = declaration.GetLocation().GetLineSpan()
                    .StartLinePosition.Line + 1;
                failures.Add(
                    $"{Path.GetFileName(file)}:{line} {DeclarationName(declaration)}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "下列 GraphRAG V3 公開／重要 internal 契約缺少繁體中文 XML doc：\n" +
            string.Join('\n', failures));
    }

    private static bool RequiresDocumentation(SyntaxNode node)
    {
        if (node is EnumMemberDeclarationSyntax member)
            return member.Parent is EnumDeclarationSyntax declaration &&
                   IsPublic(declaration) &&
                   IsEffectivelyPublic(declaration);
        if (node is BaseTypeDeclarationSyntax type)
            return IsPublic(type) && IsEffectivelyPublic(type) ||
                   type.Modifiers.Any(SyntaxKind.InternalKeyword) &&
                   ImportantInternalTypes.Contains(type.Identifier.ValueText);
        if (node is DelegateDeclarationSyntax @delegate)
            return IsPublic(@delegate) && IsEffectivelyPublic(@delegate);
        if (node is not MemberDeclarationSyntax declarationNode)
            return false;
        if (!IsEffectivelyPublic(declarationNode))
            return false;
        if (declarationNode.Parent is InterfaceDeclarationSyntax parentInterface &&
            IsPublic(parentInterface))
            return declarationNode is MethodDeclarationSyntax or
                PropertyDeclarationSyntax or EventDeclarationSyntax or
                IndexerDeclarationSyntax;
        return declarationNode.Modifiers.Any(SyntaxKind.PublicKeyword) &&
               declarationNode is MethodDeclarationSyntax or
                   PropertyDeclarationSyntax or
                   ConstructorDeclarationSyntax or
                   EventDeclarationSyntax or
                   EventFieldDeclarationSyntax or
                   FieldDeclarationSyntax or
                   IndexerDeclarationSyntax or
                   OperatorDeclarationSyntax or
                   ConversionOperatorDeclarationSyntax;
    }

    private static bool IsPublic(MemberDeclarationSyntax declaration) =>
        declaration.Modifiers.Any(SyntaxKind.PublicKeyword);

    private static bool IsEffectivelyPublic(SyntaxNode declaration) =>
        declaration.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .All(type => type.Modifiers.Any(SyntaxKind.PublicKeyword));

    private static string DeclarationName(SyntaxNode declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        EnumMemberDeclarationSyntax member => member.Identifier.ValueText,
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
        FieldDeclarationSyntax field => string.Join(
            ",", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
        EventFieldDeclarationSyntax field => string.Join(
            ",", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
        _ => declaration.Kind().ToString(),
    };

    private static string FindModuleDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName, "apps", "agent-service", "modules", "GraphRAG");
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "找不到 apps/agent-service/modules/GraphRAG 測試目錄。");
    }

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex ChineseCharacterRegex();
}
