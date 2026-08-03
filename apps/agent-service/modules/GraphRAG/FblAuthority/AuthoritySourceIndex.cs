using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Collections.ObjectModel;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>集中定義原始碼掃描的納入副檔名與排除目錄。</summary>
public static class RepositoryPathPolicy
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "packages",
        "node_modules",
    };

    /// <summary>判斷實體檔案是否位於可掃描的原始碼範圍。</summary>
    public static bool IsIncludedSourceFile(string sourceRoot, string fullPath)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, fullPath);
        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        // 任一層屬於建置輸出、套件或版本控制資料時，整條路徑都排除。
        if (segments.Any(segment => ExcludedDirectoryNames.Contains(segment)))
        {
            return false;
        }

        // 單元測試專案不是投資交易系統執行期路徑，也可能包含刻意無法編譯的測試片段。
        if (segments.Any(segment => segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // SPEC 明確排除 Scripts/dist；以完整相對路徑判斷，保留其他 Scripts 原始碼。
        var normalizedRelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        if (normalizedRelativePath.Contains("/Scripts/dist/", StringComparison.OrdinalIgnoreCase)
            || normalizedRelativePath.StartsWith("Scripts/dist/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 壓縮與 bundle 檔缺乏可靠來源位置，不參與 JavaScript 關係抽取。
        var fileName = Path.GetFileName(fullPath);
        return !fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>列舉指定副檔名的來源檔並以相對路徑穩定排序。</summary>
    public static IReadOnlyList<string> EnumerateFiles(string sourceRoot, params string[] extensions)
    {
        var extensionSet = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // EnumerateFiles 採單次串流列舉，再套用共同邊界與 deterministic 排序。
        return Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => extensionSet.Contains(Path.GetExtension(path)))
            .Where(path => IsIncludedSourceFile(sourceRoot, path))
            .OrderBy(path => Path.GetRelativePath(sourceRoot, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>轉成統一使用正斜線的 Repository-relative path。</summary>
    public static string ToRepositoryRelativePath(string sourceRoot, string fullPath)
    {
        // 圖譜與 Artifact 不應受到 Windows 分隔符差異影響。
        return Path.GetRelativePath(sourceRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }
}

/// <summary>表示 C# 型別在單一檔案中的一個 declaration part。</summary>
public sealed record CSharpTypePart(
    string RelativePath,
    int SourceLine,
    ClassDeclarationSyntax Syntax);

/// <summary>表示 C# 方法及後續 Resolver 所需的語法資訊。</summary>
public sealed record IndexedCSharpMethod(
    string Name,
    string RouteName,
    bool IsPublic,
    bool IsNonAction,
    bool IsHttpPost,
    string RelativePath,
    int SourceLine,
    MethodDeclarationSyntax Syntax);

/// <summary>把相同 fully-qualified partial class 的所有檔案合併為一個索引實體。</summary>
public sealed record IndexedCSharpType(
    string Name,
    string FullName,
    IReadOnlyList<string> BaseTypeNames,
    IReadOnlyList<CSharpTypePart> Parts,
    IReadOnlyList<IndexedCSharpMethod> Methods);

/// <summary>
/// 使用 Roslyn 建立 comment-aware 的 C# 型別與方法索引。
/// 語法樹不會把註解中的 URL、class 或 method 當成可執行程式。
/// </summary>
public sealed class CSharpSourceIndex
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IndexedCSharpType>> _typesBySimpleName;
    private readonly IReadOnlyDictionary<string, IndexedCSharpType> _typesByFullName;

    /// <summary>建立完成合併與名稱索引的 C# Source Index。</summary>
    private CSharpSourceIndex(
        IReadOnlyList<IndexedCSharpType> types,
        int fileCount,
        IReadOnlyList<string> parseDiagnosticFiles)
    {
        Types = types;
        FileCount = fileCount;
        ParseDiagnosticFiles = parseDiagnosticFiles;

        // simple name 可能跨 namespace 重複，因此保留候選陣列供 Resolver 判斷歧義。
        _typesBySimpleName = new ReadOnlyDictionary<string, IReadOnlyList<IndexedCSharpType>>(
            types
                .GroupBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<IndexedCSharpType>)group
                        .OrderBy(type => type.FullName, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase));

        // fully-qualified name 在合法 C# 專案中必須唯一；partial 已在建構前合併。
        _typesByFullName = new ReadOnlyDictionary<string, IndexedCSharpType>(
            types.ToDictionary(type => type.FullName, StringComparer.Ordinal));
    }

    /// <summary>取得 Repository 中所有合併後的 class。</summary>
    public IReadOnlyList<IndexedCSharpType> Types { get; }

    /// <summary>取得實際解析的 C# 檔案數量。</summary>
    public int FileCount { get; }

    /// <summary>取得包含 Roslyn error diagnostic 的檔案，供人工檢查語法版本差異。</summary>
    public IReadOnlyList<string> ParseDiagnosticFiles { get; }

    /// <summary>從實體 Repository 建立完整索引。</summary>
    public static async Task<CSharpSourceIndex> CreateAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var files = RepositoryPathPolicy.EnumerateFiles(sourceRoot, ".cs");
        var documents = new List<(string RelativePath, string Text)>(files.Count);

        // 檔案讀取保留相對路徑，任何外部路徑都不會寫入節點 identity。
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            documents.Add((RepositoryPathPolicy.ToRepositoryRelativePath(sourceRoot, file), text));
        }

        return Create(documents);
    }

    /// <summary>從記憶體文件建立索引，供單元測試固定解析規則。</summary>
    public static CSharpSourceIndex Create(
        IReadOnlyList<(string RelativePath, string Text)> documents)
    {
        var parts = new List<UnmergedTypePart>();
        var diagnosticFiles = new List<string>();
        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithDocumentationMode(DocumentationMode.Parse);

        foreach (var document in documents)
        {
            // Roslyn trivia 會保留註解，但 DescendantNodes 只回傳真正語法節點。
            var tree = CSharpSyntaxTree.ParseText(document.Text, parseOptions, document.RelativePath);
            var root = tree.GetCompilationUnitRoot();
            if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                diagnosticFiles.Add(document.RelativePath);
            }

            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                parts.Add(CreateUnmergedPart(document.RelativePath, declaration));
            }
        }

        // 相同 FullName 的 Base／Custom partial class 在此合併為一個 CodeClass 候選。
        var mergedTypes = parts
            .GroupBy(part => part.FullName, StringComparer.Ordinal)
            .Select(MergeParts)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        return new CSharpSourceIndex(mergedTypes, documents.Count, diagnosticFiles);
    }

    /// <summary>以不區分大小寫的 simple name 查詢全部候選型別。</summary>
    public IReadOnlyList<IndexedCSharpType> FindTypes(string simpleName)
    {
        return _typesBySimpleName.TryGetValue(simpleName, out var types)
            ? types
            : Array.Empty<IndexedCSharpType>();
    }

    /// <summary>以大小寫敏感的 fully-qualified name 查詢型別。</summary>
    public IndexedCSharpType? FindTypeByFullName(string fullName)
    {
        return _typesByFullName.GetValueOrDefault(fullName);
    }

    /// <summary>建立尚未合併的單一 class declaration。</summary>
    private static UnmergedTypePart CreateUnmergedPart(
        string relativePath,
        ClassDeclarationSyntax declaration)
    {
        var namespaceNames = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(namespaceDeclaration => namespaceDeclaration.Name.ToString())
            .Reverse();
        var containingTypes = declaration.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .Select(type => type.Identifier.ValueText)
            .Reverse();
        var fullNameSegments = namespaceNames
            .Concat(containingTypes)
            .Append(declaration.Identifier.ValueText);
        var fullName = string.Join('.', fullNameSegments);
        var sourceLine = declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Base type 保存語法文字與最末 simple name，後續可解析繼承鏈而不需編譯舊專案。
        var baseTypeNames = declaration.BaseList?.Types
            .Select(baseType => baseType.Type.ToString())
            .ToArray() ?? Array.Empty<string>();
        var methods = declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .Select(method => CreateMethod(relativePath, method))
            .ToArray();

        return new UnmergedTypePart(
            declaration.Identifier.ValueText,
            fullName,
            baseTypeNames,
            new CSharpTypePart(relativePath, sourceLine, declaration),
            methods);
    }

    /// <summary>解析 ActionName、NonAction 與 HTTP verb attribute。</summary>
    private static IndexedCSharpMethod CreateMethod(
        string relativePath,
        MethodDeclarationSyntax method)
    {
        var attributes = method.AttributeLists.SelectMany(list => list.Attributes).ToArray();
        var actionNameAttribute = attributes.FirstOrDefault(attribute =>
            IsAttributeNamed(attribute, "ActionName"));
        var routeName = actionNameAttribute?.ArgumentList?.Arguments.FirstOrDefault()?.Expression
            is LiteralExpressionSyntax literal
                ? literal.Token.ValueText
                : method.Identifier.ValueText;
        var sourceLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        return new IndexedCSharpMethod(
            method.Identifier.ValueText,
            routeName,
            method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)),
            attributes.Any(attribute => IsAttributeNamed(attribute, "NonAction")),
            attributes.Any(attribute => IsAttributeNamed(attribute, "HttpPost")),
            relativePath,
            sourceLine,
            method);
    }

    /// <summary>比對可含 Attribute 後綴或 namespace 的 attribute 名稱。</summary>
    private static bool IsAttributeNamed(AttributeSyntax attribute, string expectedName)
    {
        var actualName = attribute.Name.ToString().Split('.').Last();
        return string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(actualName, $"{expectedName}Attribute", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>合併相同 fully-qualified partial class 的來源檔、Base Type 與方法。</summary>
    private static IndexedCSharpType MergeParts(IGrouping<string, UnmergedTypePart> group)
    {
        var orderedParts = group
            .OrderBy(part => part.Part.RelativePath, StringComparer.Ordinal)
            .ThenBy(part => part.Part.SourceLine)
            .ToArray();
        var first = orderedParts[0];

        return new IndexedCSharpType(
            first.Name,
            first.FullName,
            orderedParts
                .SelectMany(part => part.BaseTypeNames)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            orderedParts.Select(part => part.Part).ToArray(),
            orderedParts
                .SelectMany(part => part.Methods)
                .OrderBy(method => method.RelativePath, StringComparer.Ordinal)
                .ThenBy(method => method.SourceLine)
                .ToArray());
    }

    /// <summary>暫存合併前的單一 class declaration 資訊。</summary>
    private sealed record UnmergedTypePart(
        string Name,
        string FullName,
        IReadOnlyList<string> BaseTypeNames,
        CSharpTypePart Part,
        IReadOnlyList<IndexedCSharpMethod> Methods);
}

/// <summary>表示可由 MVC Controller 回傳的單一 View 原始檔。</summary>
public sealed record IndexedViewFile(
    string RelativePath,
    string Text);

/// <summary>索引 ASPX、ASCX 與 Razor View，並依 MVC 約定解析實體檔案。</summary>
public sealed class ViewSourceIndex
{
    private static readonly string[] SupportedExtensions = { ".aspx", ".ascx", ".cshtml" };
    private readonly IReadOnlyDictionary<string, IndexedViewFile> _filesByPath;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IndexedViewFile>> _filesByName;

    /// <summary>建立已正規化路徑的 View 索引。</summary>
    private ViewSourceIndex(IReadOnlyList<IndexedViewFile> files)
    {
        Files = files;
        _filesByPath = files.ToDictionary(
            file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        _filesByName = files
            .GroupBy(file => Path.GetFileNameWithoutExtension(file.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IndexedViewFile>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>取得全部納入的 View 檔。</summary>
    public IReadOnlyList<IndexedViewFile> Files { get; }

    /// <summary>從 Repository 建立 View 索引並保留檔案內容供 Script Resolver 使用。</summary>
    public static async Task<ViewSourceIndex> CreateAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var paths = RepositoryPathPolicy.EnumerateFiles(sourceRoot, SupportedExtensions);
        var files = new List<IndexedViewFile>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            files.Add(new IndexedViewFile(
                RepositoryPathPolicy.ToRepositoryRelativePath(sourceRoot, path),
                text));
        }

        return new ViewSourceIndex(files);
    }

    /// <summary>從記憶體 View 建立測試索引。</summary>
    public static ViewSourceIndex Create(IReadOnlyList<IndexedViewFile> files)
    {
        return new ViewSourceIndex(files);
    }

    /// <summary>依明確路徑或 MVC Controller/View 約定尋找候選檔案。</summary>
    public IReadOnlyList<IndexedViewFile> FindViews(string controllerName, string viewName)
    {
        var normalizedViewName = viewName
            .Replace(Path.DirectorySeparatorChar, '/')
            .Trim();

        // 明確 ~/Views/... 或 /Views/... 路徑優先，不再套用 Controller 約定。
        var explicitPath = normalizedViewName
            .TrimStart('~')
            .TrimStart('/');
        if (explicitPath.StartsWith("Views/", StringComparison.OrdinalIgnoreCase))
        {
            return FindByExplicitViewPath($"RiskMaster_Web/{explicitPath}");
        }

        // 一般 View("name") 依 RiskMaster_Web/Views/{Controller}/{name} 解析。
        foreach (var extension in SupportedExtensions)
        {
            var expectedPath = $"RiskMaster_Web/Views/{controllerName}/{normalizedViewName}";
            if (!expectedPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                expectedPath += extension;
            }

            if (_filesByPath.TryGetValue(expectedPath, out var exactView))
            {
                return new[] { exactView };
            }
        }

        // Shared View 是 MVC 的第二順位；只有唯一命中才採用。
        foreach (var extension in SupportedExtensions)
        {
            var sharedPath = $"RiskMaster_Web/Views/Shared/{normalizedViewName}";
            if (!sharedPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                sharedPath += extension;
            }

            if (_filesByPath.TryGetValue(sharedPath, out var sharedView))
            {
                return new[] { sharedView };
            }
        }

        // 最後只容許 Repository 中同名 View 唯一時回傳，避免跨 Controller 猜測。
        var fileName = Path.GetFileNameWithoutExtension(normalizedViewName);
        return _filesByName.TryGetValue(fileName, out var candidates)
            && candidates.Count == 1
                ? candidates
                : Array.Empty<IndexedViewFile>();
    }

    /// <summary>解析可能省略副檔名的明確 View 路徑。</summary>
    private IReadOnlyList<IndexedViewFile> FindByExplicitViewPath(string explicitPath)
    {
        if (_filesByPath.TryGetValue(explicitPath, out var exactView))
        {
            return new[] { exactView };
        }

        foreach (var extension in SupportedExtensions)
        {
            if (_filesByPath.TryGetValue($"{explicitPath}{extension}", out var extensionView))
            {
                return new[] { extensionView };
            }
        }

        return Array.Empty<IndexedViewFile>();
    }
}

/// <summary>表示可追溯的 JavaScript 或 TypeScript 原始檔。</summary>
public sealed record IndexedClientScriptFile(
    string RelativePath,
    string Text);

/// <summary>索引排除編譯輸出後的 JavaScript／TypeScript 原始檔。</summary>
public sealed class ClientScriptSourceIndex
{
    private static readonly string[] SupportedExtensions = { ".js", ".ts", ".tsx" };
    private readonly IReadOnlyDictionary<string, IndexedClientScriptFile> _filesByPath;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IndexedClientScriptFile>> _filesByName;

    /// <summary>建立已正規化路徑與檔名索引。</summary>
    private ClientScriptSourceIndex(IReadOnlyList<IndexedClientScriptFile> files)
    {
        Files = files;
        _filesByPath = files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        _filesByName = files
            .GroupBy(file => Path.GetFileName(file.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IndexedClientScriptFile>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>取得全部可掃描 Client Script。</summary>
    public IReadOnlyList<IndexedClientScriptFile> Files { get; }

    /// <summary>從 Repository 建立 Client Script 索引。</summary>
    public static async Task<ClientScriptSourceIndex> CreateAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var paths = RepositoryPathPolicy.EnumerateFiles(sourceRoot, SupportedExtensions);
        var files = new List<IndexedClientScriptFile>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            files.Add(new IndexedClientScriptFile(
                RepositoryPathPolicy.ToRepositoryRelativePath(sourceRoot, path),
                text));
        }

        return new ClientScriptSourceIndex(files);
    }

    /// <summary>從記憶體檔案建立測試索引。</summary>
    public static ClientScriptSourceIndex Create(IReadOnlyList<IndexedClientScriptFile> files)
    {
        return new ClientScriptSourceIndex(files);
    }

    /// <summary>將 JSPath 或 script src 的邏輯路徑解析成唯一 Repository 檔案。</summary>
    public IReadOnlyList<IndexedClientScriptFile> FindScripts(string logicalPath)
    {
        var normalizedPath = logicalPath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Trim()
            .TrimStart('~')
            .TrimStart('/');
        if (normalizedPath.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath["Scripts/".Length..];
        }

        // RiskMaster Web 的 JSPath 以 Scripts 目錄為根，優先使用完整相對路徑。
        var expectedPath = $"RiskMaster_Web/Scripts/{normalizedPath}";
        if (_filesByPath.TryGetValue(expectedPath, out var exactScript))
        {
            return new[] { exactScript };
        }

        // 只有檔名在 Repository 唯一時才可作為無路徑 fallback。
        var fileName = Path.GetFileName(normalizedPath);
        return _filesByName.TryGetValue(fileName, out var candidates)
            && candidates.Count == 1
                ? candidates
                : Array.Empty<IndexedClientScriptFile>();
    }
}

/// <summary>以 Roslyn 解析 enumConfirmSourceType 的名稱、數值與來源位置。</summary>
public sealed class ConfirmSourceTypeIndex
{
    private readonly IReadOnlyDictionary<string, ConfirmSourceTypeMember> _byName;
    private readonly IReadOnlyDictionary<int, ConfirmSourceTypeMember> _byValue;

    /// <summary>建立名稱與數值雙向索引。</summary>
    private ConfirmSourceTypeIndex(IReadOnlyList<ConfirmSourceTypeMember> members)
    {
        Members = members;
        _byName = members.ToDictionary(member => member.Name, StringComparer.Ordinal);
        _byValue = members
            .GroupBy(member => member.Value)
            .ToDictionary(group => group.Key, group => group.First());
    }

    /// <summary>取得 enum 中所有可確定數值的成員。</summary>
    public IReadOnlyList<ConfirmSourceTypeMember> Members { get; }

    /// <summary>由 Repository 建立 enum 索引。</summary>
    public static async Task<ConfirmSourceTypeIndex> CreateAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var documents = new List<(string RelativePath, string Text)>();
        foreach (var file in RepositoryPathPolicy.EnumerateFiles(sourceRoot, ".cs"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            if (text.Contains("enumConfirmSourceType", StringComparison.Ordinal))
            {
                documents.Add((RepositoryPathPolicy.ToRepositoryRelativePath(sourceRoot, file), text));
            }
        }

        return Create(documents);
    }

    /// <summary>從記憶體來源建立索引，供測試固定 enum 規則。</summary>
    public static ConfirmSourceTypeIndex Create(
        IReadOnlyList<(string RelativePath, string Text)> documents)
    {
        var members = new List<ConfirmSourceTypeMember>();
        foreach (var document in documents)
        {
            var root = CSharpSyntaxTree.ParseText(document.Text).GetCompilationUnitRoot();
            foreach (var declaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>()
                         .Where(item => item.Identifier.ValueText == "enumConfirmSourceType"))
            {
                var nextValue = 0;
                foreach (var member in declaration.Members)
                {
                    var value = member.EqualsValue is null
                        ? nextValue
                        : EvaluateInteger(member.EqualsValue.Value);
                    if (value is null)
                    {
                        continue;
                    }

                    members.Add(new ConfirmSourceTypeMember(
                        member.Identifier.ValueText,
                        value.Value,
                        document.RelativePath,
                        member.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                    nextValue = value.Value + 1;
                }
            }
        }

        return new ConfirmSourceTypeIndex(
            members
                .GroupBy(member => member.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(member => member.Value)
                .ThenBy(member => member.Name, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>以 enum 成員名稱查詢來源。</summary>
    public ConfirmSourceTypeMember? FindByName(string name) => _byName.GetValueOrDefault(name);

    /// <summary>以 DB mapping 數值查詢 enum 名稱；mapping 單獨存在時可回傳 null。</summary>
    public ConfirmSourceTypeMember? FindByValue(int value) => _byValue.GetValueOrDefault(value);

    /// <summary>計算 enum 常見的十進位、十六進位與負整數常數。</summary>
    private static int? EvaluateInteger(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal && literal.Token.Value is not null)
        {
            return Convert.ToInt32(literal.Token.Value);
        }

        if (expression is PrefixUnaryExpressionSyntax unary
            && unary.IsKind(SyntaxKind.UnaryMinusExpression)
            && EvaluateInteger(unary.Operand) is { } operand)
        {
            return -operand;
        }

        return null;
    }
}

/// <summary>保存 ConfirmSourceType enum 成員的直接原始碼位置。</summary>
public sealed record ConfirmSourceTypeMember(
    string Name,
    int Value,
    string RelativePath,
    int SourceLine);

