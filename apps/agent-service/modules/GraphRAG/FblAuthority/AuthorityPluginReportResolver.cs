using System.Text;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>解析 PluginReport Base64 入口並驗證 PlugIn_Report_FBL 的實際 ReportKernel。</summary>
public sealed class PluginReportResolver
{
    private const string Prefix = "/PluginReport/MenuIndex/";
    private readonly CSharpSourceIndex _sourceIndex;
    private readonly string _sourceRoot;

    /// <summary>建立使用 Source Index 與 csproj 編譯清單的 Resolver。</summary>
    public PluginReportResolver(CSharpSourceIndex sourceIndex, string sourceRoot)
    {
        _sourceIndex = sourceIndex;
        _sourceRoot = sourceRoot;
    }

    /// <summary>解析全部 PluginReport Menu 並建立 Menu／Endpoint→ReportKernel。</summary>
    public async Task<ExtractionResult> ResolveAsync(
        ExtractionResult input,
        CancellationToken cancellationToken)
    {
        var builder = GraphDocumentBuilder.FromDocument(input.Document, GraphBuildStage.StandardWebExtraction);
        var issues = input.Issues.ToList();
        var projectFile = Path.Combine(_sourceRoot, "PlugIn_Report_FBL", "PlugIn_Report_FBL.csproj");
        var projectText = File.Exists(projectFile)
            ? await File.ReadAllTextAsync(projectFile, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        foreach (var menu in input.Document.Nodes.Where(IsPluginMenu))
        {
            ResolveMenu(builder, menu, projectText, issues);
        }

        return new ExtractionResult(builder.Build(), issues);
    }

    /// <summary>解碼單一入口並驗證 assembly、class、IPluginReport 與 csproj。</summary>
    private void ResolveMenu(
        GraphDocumentBuilder builder,
        GraphNode menu,
        string projectText,
        ICollection<PreflightIssue> issues)
    {
        var link = menu.Properties["normalized_link_address"]?.ToString() ?? string.Empty;
        if (!TryDecode(link, out var assemblyName, out var fullTypeName))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.PluginBase64Invalid,
                "PluginReport LinkAddress 無法解碼成 Assembly/FullyQualifiedType。",
                MenuId: menu.Properties["menu_id"]?.ToString(),
                FromKey: menu.Key,
                TargetText: link));
            return;
        }

        if (!string.Equals(assemblyName, "PlugIn_Report_FBL.dll", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.PluginTypeNotFound,
                $"PluginReport assembly '{assemblyName}' 不在允許的 PlugIn_Report_FBL 專案。",
                MenuId: menu.Properties["menu_id"]?.ToString(),
                FromKey: menu.Key,
                TargetText: fullTypeName));
            return;
        }

        var type = _sourceIndex.FindTypeByFullName(fullTypeName);
        if (type is null || !type.Parts.Any(part =>
                part.RelativePath.StartsWith("PlugIn_Report_FBL/", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.PluginTypeNotFound,
                $"PlugIn_Report_FBL 找不到 ReportKernel '{fullTypeName}'。",
                MenuId: menu.Properties["menu_id"]?.ToString(),
                FromKey: menu.Key,
                TargetText: fullTypeName));
            return;
        }

        var uncompiledParts = type.Parts.Where(part => !IsCompiled(part.RelativePath, projectText)).ToArray();
        if (uncompiledParts.Length > 0)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.PluginTypeNotCompiled,
                $"ReportKernel '{fullTypeName}' 的來源檔未納入 csproj Compile。",
                MenuId: menu.Properties["menu_id"]?.ToString(),
                FromKey: menu.Key,
                TargetText: fullTypeName,
                Candidates: uncompiledParts.Select(part => part.RelativePath).ToArray()));
            return;
        }

        // IPluginReport 可由 partial 任一宣告部位直接列在 base list；public 與 non-public 都允許。
        if (!type.BaseTypeNames.Any(baseType =>
                baseType.Split('.').Last() is "IPluginReport"))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.PluginTypeNotFound,
                $"ReportKernel '{fullTypeName}' 未直接實作 IPluginReport。",
                MenuId: menu.Properties["menu_id"]?.ToString(),
                FromKey: menu.Key,
                TargetText: fullTypeName));
            return;
        }

        CodeClassNodeFactory.Add(builder, type, CodeClassRole.ReportKernel);
        var evidence = new GraphEvidence
        {
            SourceKind = GraphSourceKind.Route,
            SourceFile = type.Parts[0].RelativePath,
            SourceLine = type.Parts[0].SourceLine,
            RawValue = link,
            SourceText = $"{assemblyName}/{fullTypeName}",
        };
        builder.AddRelationship(
            GraphRelationshipKind.LoadsPluginReport,
            menu.Key,
            $"code:{type.FullName}",
            evidence);
        builder.AddRelationship(
            GraphRelationshipKind.LoadsPluginReport,
            LinkAddressParser.CreateEndpointKey(link),
            $"code:{type.FullName}",
            evidence);
    }

    /// <summary>判斷節點是否由 PluginReport Resolver 處理。</summary>
    private static bool IsPluginMenu(GraphNode node) =>
        node.Kind == GraphNodeKind.Menu
        && node.Properties.GetValueOrDefault("resolver_kind")?.ToString()
            == MenuResolverKind.PluginReport.ToString();

    /// <summary>將 Base64 payload 解碼為 assembly/type；格式不完整時回傳 false。</summary>
    private static bool TryDecode(
        string normalizedLink,
        out string assemblyName,
        out string fullTypeName)
    {
        assemblyName = string.Empty;
        fullTypeName = string.Empty;
        if (!normalizedLink.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalizedLink[Prefix.Length..]));
            var separator = decoded.IndexOf('/');
            if (separator <= 0 || separator == decoded.Length - 1)
            {
                return false;
            }

            assemblyName = decoded[..separator];
            fullTypeName = decoded[(separator + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>驗證舊式 csproj 的 Compile Include 是否包含來源檔。</summary>
    private static bool IsCompiled(string repositoryRelativePath, string projectText)
    {
        var projectRelativePath = repositoryRelativePath["PlugIn_Report_FBL/".Length..]
            .Replace('/', '\\');
        return projectText.Contains($"Include=\"{projectRelativePath}\"", StringComparison.OrdinalIgnoreCase);
    }
}

