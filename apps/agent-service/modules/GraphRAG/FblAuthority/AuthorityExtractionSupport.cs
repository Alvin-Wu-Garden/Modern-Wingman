using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>表示中心 SQL 從 tblMenuMap 取得的一筆原始功能資料。</summary>
public sealed record MenuCatalogItem(
    long Id,
    string Name,
    string LinkAddress,
    string? Description);

/// <summary>表示 tblAsyncConfirmSourceTypeMapping 的一筆直接放行映射。</summary>
public sealed record ConfirmMappingItem(
    int ConfirmSourceType,
    long ConfirmMenuId,
    long MaintainMenuId,
    string? WaitForConfirmName);

/// <summary>保存 CustomReport 所需的 RT、DS 與 PD 實際資料列。</summary>
public sealed record CustomReportCatalog(
    IReadOnlyList<CustomReportTemplateItem> Templates,
    IReadOnlyList<CustomReportDataSourceItem> DataSources,
    IReadOnlyList<CustomParameterDataSourceItem> ParameterDataSources);

/// <summary>表示 tblCustomDesignRiskReportTemplate 的 RT。</summary>
public sealed record CustomReportTemplateItem(Guid TemplateId, string? Name, string TemplateXml);

/// <summary>表示 tblCustomDesignReportDataSource 的 DS。</summary>
public sealed record CustomReportDataSourceItem(Guid SerialId, string? Description, string XmlDefinition);

/// <summary>表示 tblCustomDesignReportCustomParameterDataSource 的 PD。</summary>
public sealed record CustomParameterDataSourceItem(Guid SerialId, string? Description);

/// <summary>表示外部資料庫系統目錄中已存在的一個資料庫物件。</summary>
public sealed record DatabaseObjectCatalogItem(
    string SchemaName,
    string ObjectName,
    DatabaseObjectKind Kind,
    string Provider = "SqlServer",
    string DatabaseName = "")
{
    /// <summary>
    /// 取得包含 Provider、資料庫與 Schema 的穩定節點 Key，避免不同資料來源的同名物件碰撞。
    /// 舊測試建立的三參數物件仍會產生可用 Key，正式抽取器會填入實際識別。
    /// </summary>
    public string CreateNodeKey()
    {
        var provider = string.IsNullOrWhiteSpace(Provider) ? "SqlServer" : Provider.Trim();
        var database = string.IsNullOrWhiteSpace(DatabaseName) ? "unknown" : DatabaseName.Trim();
        return $"db:{provider.ToLowerInvariant()}:{database}:{SchemaName}:{ObjectName}";
    }
}

/// <summary>保存一個 Resolver 階段產生的圖與所有待人工審閱問題。</summary>
public sealed record ExtractionResult(
    GraphDocument Document,
    IReadOnlyList<PreflightIssue> Issues);

/// <summary>依 SPEC 正規化 Menu LinkAddress 並選擇解析器策略。</summary>
public static class LinkAddressParser
{
    private const string PluginReportPrefix = "/PluginReport/MenuIndex/";
    private const string CustomReportPrefix = "/CustomReport/MenuIndex/";

    /// <summary>只修正開頭重複斜線與解析用途的尾端斜線，保留其他原始語意。</summary>
    public static string Normalize(string linkAddress)
    {
        // 原始值仍保存在 Menu 屬性，此處只產生供路由比對的 canonical 形式。
        var normalized = linkAddress.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = $"/{normalized.TrimStart('/')}";
        }

        // 根路徑不可被正規化成空字串，只有長度大於一才移除尾端斜線。
        return normalized.Length > 1
            ? normalized.TrimEnd('/')
            : normalized;
    }

    /// <summary>依明確入口格式選擇 Standard、PluginReport 或 CustomReport Resolver。</summary>
    public static MenuResolverKind Classify(string normalizedLinkAddress)
    {
        // ResolverKind 只控制程式分派，不會建立 BusinessFeature 或 family 節點。
        if (normalizedLinkAddress.StartsWith(PluginReportPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return MenuResolverKind.PluginReport;
        }

        if (normalizedLinkAddress.StartsWith(CustomReportPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return MenuResolverKind.CustomReport;
        }

        return MenuResolverKind.StandardWeb;
    }

    /// <summary>建立大小寫不敏感且可重建的 Endpoint 穩定 Key。</summary>
    public static string CreateEndpointKey(string normalizedLinkAddress)
    {
        // MVC 路由比對不區分大小寫，因此 Key 使用 invariant lower-case 去重。
        return $"endpoint:{normalizedLinkAddress.ToLowerInvariant()}";
    }
}

/// <summary>表示從 Standard Web LinkAddress 解析出的 MVC 路由。</summary>
public sealed record MvcRoute(
    string ControllerName,
    string ActionName,
    IReadOnlyList<string> RouteValues)
{
    /// <summary>從已正規化 LinkAddress 解析 Controller、Action 與其餘 route values。</summary>
    public static bool TryParse(string normalizedLinkAddress, out MvcRoute? route)
    {
        // 查詢字串不參與 MVC path segment 比對，但原始 LinkAddress 仍保留在 Evidence。
        var path = normalizedLinkAddress.Split('?', '#')[0];
        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            route = null;
            return false;
        }

        // MVC 約定允許省略 Action，此時使用 Index；目前資料多數仍有明確第二段。
        var actionName = segments.Length >= 2 ? segments[1] : "Index";
        route = new MvcRoute(
            segments[0],
            actionName,
            segments.Skip(2).ToArray());
        return true;
    }

    /// <summary>建立包含 route values 的 WebAction 穩定 Key。</summary>
    public string CreateWebActionKey()
    {
        // MVC 名稱比對不區分大小寫；route value 經 URI escape 後可安全組成 identity。
        var routeValueIdentity = RouteValues.Count == 0
            ? "none"
            : string.Join(',', RouteValues.Select(Uri.EscapeDataString));
        return $"web-action:{ControllerName.ToLowerInvariant()}.{ActionName.ToLowerInvariant()}.{routeValueIdentity}";
    }
}

/// <summary>定義經系統維護者逐筆確認的歷史前端路由例外。</summary>
public enum KnownScriptRouteExclusion
{
    /// <summary>frmInvestmentDecisionCore.js 中已不再使用的 QueryReportDatas Store。</summary>
    InvestmentDecisionQueryReportDatas,
}

/// <summary>集中保存人工確認的歷史路由排除條件，避免散落字串判斷。</summary>
public static class KnownScriptRouteExclusionPolicy
{
    /// <summary>判斷指定 Script URL 是否為人工確認的歷史 Store。</summary>
    public static KnownScriptRouteExclusion? Match(
        string sourceFile,
        string normalizedRoute)
    {
        // 只排除已確認的來源檔與完整 URL 組合；其他同名 URL 仍須正常解析。
        if (string.Equals(
                sourceFile,
                "RiskMaster_Web/Scripts/View/frmInvestmentDecisionCore.js",
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                normalizedRoute,
                "/InvestmentDecision/QueryReportDatas",
                StringComparison.OrdinalIgnoreCase))
        {
            return KnownScriptRouteExclusion.InvestmentDecisionQueryReportDatas;
        }

        return null;
    }
}

/// <summary>
/// 以輕量 lexical scanner 解析 JavaScript／TypeScript 的請求路徑字串。
/// Scanner 會辨識字串與註解邊界，避免把註解中的歷史 URL 建成關係。
/// </summary>
public static class BrowserScriptRouteReferenceParser
{
    /// <summary>解析具有 request context 的 `/Controller/Action/...` 字串。</summary>
    public static IReadOnlyList<JavaScriptRouteReference> Parse(string scriptText)
    {
        var references = new List<JavaScriptRouteReference>();
        var index = 0;
        var line = 1;

        while (index < scriptText.Length)
        {
            var current = scriptText[index];

            // 單行註解直接跳到換行，註解中的 URL 不進入字串解析。
            if (current == '/' && Peek(scriptText, index + 1) == '/')
            {
                SkipLineComment(scriptText, ref index, ref line);
                continue;
            }

            // 區塊註解保留行號計數，但完全忽略其中內容。
            if (current == '/' && Peek(scriptText, index + 1) == '*')
            {
                SkipBlockComment(scriptText, ref index, ref line);
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                var stringStart = index;
                var sourceLine = line;
                var value = ReadStringLiteral(scriptText, ref index, ref line, current);
                if (value is not null
                    && LooksLikeRequestContext(scriptText, stringStart)
                    && TryNormalizeRoute(value, out var normalizedRoute))
                {
                    references.Add(new JavaScriptRouteReference(normalizedRoute!, sourceLine, value));
                }

                continue;
            }

            if (current == '\n')
            {
                line++;
            }

            index++;
        }

        // 同一 Script 重複使用相同 URL 時只建立一條 CALLS，保留第一個來源位置。
        return references
            .GroupBy(reference => reference.NormalizedRoute, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(reference => reference.SourceLine).First())
            .OrderBy(reference => reference.NormalizedRoute, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>略過 JavaScript 單行註解並維持正確行號。</summary>
    private static void SkipLineComment(string text, ref int index, ref int line)
    {
        index += 2;
        while (index < text.Length && text[index] != '\n')
        {
            index++;
        }

        if (index < text.Length)
        {
            line++;
            index++;
        }
    }

    /// <summary>略過 JavaScript 區塊註解並維持正確行號。</summary>
    private static void SkipBlockComment(string text, ref int index, ref int line)
    {
        index += 2;
        while (index < text.Length)
        {
            if (text[index] == '\n')
            {
                line++;
            }

            if (text[index] == '*' && Peek(text, index + 1) == '/')
            {
                index += 2;
                return;
            }

            index++;
        }
    }

    /// <summary>讀取單引號、雙引號或 template literal，處理跳脫字元。</summary>
    private static string? ReadStringLiteral(
        string text,
        ref int index,
        ref int line,
        char quote)
    {
        var builder = new System.Text.StringBuilder();
        index++;

        while (index < text.Length)
        {
            var current = text[index];
            if (current == '\\' && index + 1 < text.Length)
            {
                // 路由只需要保留被跳脫的實際字元，不執行完整 JavaScript escape evaluation。
                builder.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (current == quote)
            {
                index++;
                return builder.ToString();
            }

            if (current == '\n')
            {
                line++;
                if (quote != '`')
                {
                    return null;
                }
            }

            builder.Append(current);
            index++;
        }

        return null;
    }

    /// <summary>以字串前方有限範圍判斷是否位於 URL、Ajax 或 request 語境。</summary>
    private static bool LooksLikeRequestContext(string text, int stringStart)
    {
        var contextStart = Math.Max(0, stringStart - 160);
        var context = text.AsSpan(contextStart, stringStart - contextStart);
        return ContainsOrdinalIgnoreCase(context, "url")
            || ContainsOrdinalIgnoreCase(context, "ajax")
            || ContainsOrdinalIgnoreCase(context, "request")
            || ContainsOrdinalIgnoreCase(context, "datasource")
            || ContainsOrdinalIgnoreCase(context, "read:")
            || ContainsOrdinalIgnoreCase(context, "create:")
            || ContainsOrdinalIgnoreCase(context, "update:")
            || ContainsOrdinalIgnoreCase(context, "destroy:");
    }

    /// <summary>正規化路由並排除檔案、dist、Content 與非 MVC 路徑。</summary>
    private static bool TryNormalizeRoute(string value, out string? normalizedRoute)
    {
        normalizedRoute = null;
        if (!value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var path = value.Split('?', '#')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2
            || segments[0].Equals("Scripts", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("Content", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("dist", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lastSegment = segments[^1];
        if (Path.HasExtension(lastSegment))
        {
            return false;
        }

        normalizedRoute = $"/{string.Join('/', segments)}";
        return true;
    }

    /// <summary>安全讀取下一個字元，超出範圍時回傳 null character。</summary>
    private static char Peek(string text, int index)
    {
        return index < text.Length ? text[index] : '\0';
    }

    /// <summary>在 Span 上執行不分大小寫的 ordinal 搜尋。</summary>
    private static bool ContainsOrdinalIgnoreCase(ReadOnlySpan<char> source, string value)
    {
        return source.IndexOf(value.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

/// <summary>保存 Client Script 中一筆可執行 MVC request route。</summary>
public sealed record JavaScriptRouteReference(
    string NormalizedRoute,
    int SourceLine,
    string RawValue);

/// <summary>從有效 View markup 擷取 JSPath 與直接 script src，不解析註解內容。</summary>
public static partial class ViewScriptReferenceParser
{
    /// <summary>解析 View 直接載入的 Client Script 邏輯路徑與行號。</summary>
    public static IReadOnlyList<ViewScriptReference> Parse(string viewText)
    {
        // 註解以等長空白取代並保留換行，讓後續 Match.Index 仍可換算原始行號。
        var executableText = ViewCommentPattern().Replace(
            viewText,
            match => PreserveLineBreaks(match.Value));
        var references = new List<ViewScriptReference>();

        foreach (Match match in JsPathPattern().Matches(executableText))
        {
            var logicalPath = match.Groups["path"].Value;
            if (!HasSupportedScriptExtension(logicalPath) || IsExcludedScriptBoundary(logicalPath))
            {
                continue;
            }

            references.Add(new ViewScriptReference(
                logicalPath,
                GetLineNumber(executableText, match.Index)));
        }

        foreach (Match match in DirectScriptSourcePattern().Matches(executableText))
        {
            var source = match.Groups["src"].Value;

            // Server expression 會由 JSPath 規則解析；AbsolutePath endpoint 不是實體 ClientScript。
            if (source.Contains("<%", StringComparison.Ordinal)
                || !HasSupportedScriptExtension(source)
                || IsExcludedScriptBoundary(source))
            {
                continue;
            }

            references.Add(new ViewScriptReference(
                source.Split('?', '#')[0],
                GetLineNumber(executableText, match.Index)));
        }

        // 同一 View 重複載入相同路徑時只建立一條 LOADS，保留第一個來源位置。
        return references
            .GroupBy(reference => reference.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(reference => reference.SourceLine).First())
            .OrderBy(reference => reference.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>判斷是否為 SPEC 排除的 dist bundle 或 Scripts 根目錄外第三方資源。</summary>
    private static bool IsExcludedScriptBoundary(string path)
    {
        var normalizedPath = path.Replace(Path.DirectorySeparatorChar, '/').Trim();
        return normalizedPath.StartsWith("../", StringComparison.Ordinal)
            || normalizedPath.Contains("/dist/", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith("dist/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判斷 URL 是否明確指向 JavaScript 或 TypeScript 檔案。</summary>
    private static bool HasSupportedScriptExtension(string path)
    {
        var pathWithoutQuery = path.Split('?', '#')[0];
        return pathWithoutQuery.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || pathWithoutQuery.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || pathWithoutQuery.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>以空白取代註解字元，但完整保留 CR／LF。</summary>
    private static string PreserveLineBreaks(string text)
    {
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = source[index] is '\r' or '\n' ? source[index] : ' ';
            }
        });
    }

    /// <summary>將 Match 字元位置換算為 1-based 行號。</summary>
    private static int GetLineNumber(string text, int characterIndex)
    {
        var line = 1;
        for (var index = 0; index < characterIndex; index++)
        {
            if (text[index] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>辨識 ASP.NET server comment 與 HTML comment。</summary>
    [GeneratedRegex(@"<%--.*?--%>|<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ViewCommentPattern();

    /// <summary>擷取 UrlUtility.JSPath 的字串 literal。</summary>
    [GeneratedRegex(@"\bJSPath\s*\(\s*[""'](?<path>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsPathPattern();

    /// <summary>擷取不含 server expression 的直接 script src。</summary>
    [GeneratedRegex(@"<script\b[^>]*\bsrc\s*=\s*[""'](?<src>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DirectScriptSourcePattern();
}

/// <summary>保存 View 中一筆直接 Client Script 引用。</summary>
public sealed record ViewScriptReference(
    string LogicalPath,
    int SourceLine);

/// <summary>從已解析 Action 的 Roslyn 語法樹找出目前 route value 實際回傳的 View。</summary>
public static class MvcViewCallResolver
{
    /// <summary>解析 Action overload 中可到達的 View 呼叫與來源位置。</summary>
    public static IReadOnlyList<ResolvedViewCall> Resolve(
        IReadOnlyList<IndexedCSharpMethod> methods,
        MvcRoute route)
    {
        var calls = new List<ResolvedViewCall>();

        foreach (var method in methods)
        {
            // RouteValues 依方法參數順序綁定；未提供的參數視為 null。
            var parameterValues = method.Syntax.ParameterList.Parameters
                .Select((parameter, index) => new
                {
                    Name = parameter.Identifier.ValueText,
                    Value = index < route.RouteValues.Count ? route.RouteValues[index] : null,
                })
                .ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var invocation in method.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!IsViewInvocation(invocation) || !IsReachable(invocation, method.Syntax, parameterValues))
                {
                    continue;
                }

                var viewName = ResolveViewName(invocation, route.ActionName);
                if (viewName is null)
                {
                    continue;
                }

                calls.Add(new ResolvedViewCall(
                    viewName,
                    method.RelativePath,
                    invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
        }

        // 同一 View 在多個等價 overload 出現時只保留第一個穩定來源位置。
        return calls
            .GroupBy(call => call.ViewName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(call => call.SourceFile, StringComparer.Ordinal).ThenBy(call => call.SourceLine).First())
            .OrderBy(call => call.ViewName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>判斷 invocation 是否為 View(...) 或 this.View(...)。</summary>
    private static bool IsViewInvocation(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax identifier => string.Equals(
                identifier.Identifier.ValueText,
                "View",
                StringComparison.Ordinal),
            MemberAccessExpressionSyntax memberAccess => string.Equals(
                memberAccess.Name.Identifier.ValueText,
                "View",
                StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>取得明確 View 名稱；沒有字串第一參數時採 MVC Action 約定。</summary>
    private static string? ResolveViewName(
        InvocationExpressionSyntax invocation,
        string defaultActionName)
    {
        var firstArgument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (firstArgument is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        // View() 或 View(model) 都使用目前 Action 名稱；變數型 View name 則留待人工審閱。
        return firstArgument is null || !LooksLikeViewNameExpression(firstArgument)
            ? defaultActionName
            : null;
    }

    /// <summary>判斷第一參數是否可能是動態 View name，而非 model。</summary>
    private static bool LooksLikeViewNameExpression(ExpressionSyntax expression)
    {
        // 只有名稱包含 View 的 identifier 才保守視為動態 View name，避免把一般 model 誤判成路徑。
        return expression is IdentifierNameSyntax identifier
            && identifier.Identifier.ValueText.Contains("view", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>依目前 route parameter 評估 invocation 所在的 if／else 分支是否可到達。</summary>
    private static bool IsReachable(
        InvocationExpressionSyntax invocation,
        MethodDeclarationSyntax method,
        IReadOnlyDictionary<string, string?> parameterValues)
    {
        foreach (var ifStatement in invocation.Ancestors().OfType<IfStatementSyntax>())
        {
            if (!method.Span.Contains(ifStatement.Span))
            {
                continue;
            }

            var condition = EvaluateBoolean(ifStatement.Condition, parameterValues);
            if (condition is null)
            {
                continue;
            }

            var isInIfBranch = ifStatement.Statement.Span.Contains(invocation.Span);
            var isInElseBranch = ifStatement.Else?.Statement.Span.Contains(invocation.Span) == true;
            if ((isInIfBranch && !condition.Value) || (isInElseBranch && condition.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>評估 route value 常見的相等、不等、AND 與 OR 條件。</summary>
    private static bool? EvaluateBoolean(
        ExpressionSyntax expression,
        IReadOnlyDictionary<string, string?> parameterValues)
    {
        if (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            return EvaluateBoolean(parenthesized.Expression, parameterValues);
        }

        if (expression is PrefixUnaryExpressionSyntax unary
            && unary.IsKind(SyntaxKind.LogicalNotExpression))
        {
            var operand = EvaluateBoolean(unary.Operand, parameterValues);
            return operand is null ? null : !operand.Value;
        }

        if (expression is not BinaryExpressionSyntax binary)
        {
            return null;
        }

        if (binary.IsKind(SyntaxKind.LogicalAndExpression)
            || binary.IsKind(SyntaxKind.LogicalOrExpression))
        {
            var leftBoolean = EvaluateBoolean(binary.Left, parameterValues);
            var rightBoolean = EvaluateBoolean(binary.Right, parameterValues);
            if (leftBoolean is null || rightBoolean is null)
            {
                return null;
            }

            return binary.IsKind(SyntaxKind.LogicalAndExpression)
                ? leftBoolean.Value && rightBoolean.Value
                : leftBoolean.Value || rightBoolean.Value;
        }

        if (!binary.IsKind(SyntaxKind.EqualsExpression)
            && !binary.IsKind(SyntaxKind.NotEqualsExpression))
        {
            return null;
        }

        var leftValue = EvaluateString(binary.Left, parameterValues);
        var rightValue = EvaluateString(binary.Right, parameterValues);
        if (!leftValue.IsKnown || !rightValue.IsKnown)
        {
            return null;
        }

        var equals = string.Equals(leftValue.Value, rightValue.Value, StringComparison.Ordinal);
        return binary.IsKind(SyntaxKind.EqualsExpression) ? equals : !equals;
    }

    /// <summary>解析字串 literal、null 或已綁定的 route parameter。</summary>
    private static EvaluatedString EvaluateString(
        ExpressionSyntax expression,
        IReadOnlyDictionary<string, string?> parameterValues)
    {
        if (expression is LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return new EvaluatedString(true, literal.Token.ValueText);
            }

            if (literal.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return new EvaluatedString(true, null);
            }
        }

        if (expression is IdentifierNameSyntax identifier
            && parameterValues.TryGetValue(identifier.Identifier.ValueText, out var value))
        {
            return new EvaluatedString(true, value);
        }

        return new EvaluatedString(false, null);
    }

    /// <summary>保存字串運算是否可由目前支援規則確定。</summary>
    private readonly record struct EvaluatedString(bool IsKnown, string? Value);
}

/// <summary>保存已解析 View 名稱與對應 Controller 原始碼位置。</summary>
public sealed record ResolvedViewCall(
    string ViewName,
    string SourceFile,
    int SourceLine);

/// <summary>集中建立 CodeClass 節點，確保所有 Resolver 使用相同屬性格式。</summary>
public static class CodeClassNodeFactory
{
    /// <summary>以 fully-qualified type 合併 partial class 並建立 CodeClass 節點。</summary>
    public static void Add(
        GraphDocumentBuilder builder,
        IndexedCSharpType type,
        CodeClassRole role)
    {
        builder.AddNode(
            GraphNodeKind.CodeClass,
            $"code:{type.FullName}",
            new Dictionary<string, object?>
            {
                ["name"] = type.Name,
                ["full_name"] = type.FullName,
                ["role"] = role.ToString(),
                ["source_files"] = type.Parts.Select(part => part.RelativePath).Distinct().ToArray(),
            });
    }
}

