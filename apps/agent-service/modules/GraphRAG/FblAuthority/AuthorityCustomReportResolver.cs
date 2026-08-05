using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>從 GUID 入口解析 CustomReport 的 RT→DS→Field→PD 與明確 DB Object。</summary>
public sealed partial class CustomReportResolver
{
    private const string Prefix = "/CustomReport/MenuIndex/";
    private readonly CustomReportCatalog _catalog;
    private readonly IReadOnlyList<DatabaseObjectCatalogItem> _databaseObjects;

    /// <summary>建立只依 SQL authority 資料列與 XML 的 Resolver。</summary>
    public CustomReportResolver(
        CustomReportCatalog catalog,
        IReadOnlyList<DatabaseObjectCatalogItem> databaseObjects)
    {
        _catalog = catalog;
        _databaseObjects = databaseObjects;
    }

    /// <summary>解析全部 CustomReport Menu，未知 XML element 不建立實體。</summary>
    public ExtractionResult Resolve(ExtractionResult input)
    {
        var builder = GraphDocumentBuilder.FromDocument(input.Document, GraphBuildStage.StandardWebExtraction);
        var issues = input.Issues.ToList();
        foreach (var menu in input.Document.Nodes.Where(IsCustomReportMenu))
        {
            ResolveMenu(builder, menu, issues);
        }

        return new ExtractionResult(builder.Build(), issues);
    }

    /// <summary>以 LinkAddress GUID 找唯一 RT，再沿 XML 中 DataSourceSerialID 展開。</summary>
    private void ResolveMenu(
        GraphDocumentBuilder builder,
        GraphNode menu,
        ICollection<PreflightIssue> issues)
    {
        var link = menu.Properties["normalized_link_address"]?.ToString() ?? string.Empty;
        if (!TryReadTemplateId(link, out var templateId))
        {
            issues.Add(CreateTemplateIssue(menu, link));
            return;
        }

        var templates = _catalog.Templates.Where(item => item.TemplateId == templateId).ToArray();
        if (templates.Length != 1)
        {
            issues.Add(CreateTemplateIssue(menu, templateId.ToString()));
            return;
        }

        var template = templates[0];
        var templateKey = $"custom-report-template:{template.TemplateId:D}";
        builder.AddNode(
            GraphNodeKind.CustomReportTemplate,
            templateKey,
            new Dictionary<string, object?>
            {
                ["template_id"] = template.TemplateId.ToString("D"),
                ["name"] = template.Name,
            });
        builder.AddRelationship(
            GraphRelationshipKind.OpensCustomReport,
            menu.Key,
            templateKey,
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.DatabaseRow,
                DatabaseObject = "dbo.tblCustomDesignRiskReportTemplate",
                RowKey = template.TemplateId.ToString("D"),
                RawValue = link,
            });

        if (!TryParseXml(template.TemplateXml, out var templateXml))
        {
            issues.Add(CreateTemplateIssue(menu, "TemplateXML 格式錯誤"));
            return;
        }

        var dataSourceIds = templateXml!
            .Descendants()
            .Attributes("DataSourceSerialID")
            .Select(attribute => Guid.TryParse(attribute.Value, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        foreach (var dataSourceId in dataSourceIds)
        {
            ResolveDataSource(builder, template, templateKey, dataSourceId, issues);
        }
    }

    /// <summary>建立 RT→DS，並抽取影響 PD 或 DB 查詢的必要欄位。</summary>
    private void ResolveDataSource(
        GraphDocumentBuilder builder,
        CustomReportTemplateItem template,
        string templateKey,
        Guid dataSourceId,
        ICollection<PreflightIssue> issues)
    {
        var matches = _catalog.DataSources.Where(item => item.SerialId == dataSourceId).ToArray();
        if (matches.Length != 1)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.CustomReportDataSourceNotFound,
                $"RT '{template.TemplateId}' 找不到唯一 DS '{dataSourceId}'。",
                FromKey: templateKey,
                TargetText: dataSourceId.ToString("D")));
            return;
        }

        var dataSource = matches[0];
        var dataSourceKey = $"custom-report-ds:{dataSource.SerialId:D}";
        builder.AddNode(
            GraphNodeKind.CustomReportDataSource,
            dataSourceKey,
            new Dictionary<string, object?>
            {
                ["serial_id"] = dataSource.SerialId.ToString("D"),
                ["description"] = dataSource.Description,
            });
        builder.AddRelationship(
            GraphRelationshipKind.ContainsDataSource,
            templateKey,
            dataSourceKey,
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.Xml,
                DatabaseObject = "dbo.tblCustomDesignRiskReportTemplate",
                RowKey = template.TemplateId.ToString("D"),
                XmlPath = "//@DataSourceSerialID",
                RawValue = dataSource.SerialId.ToString("D"),
            });

        if (!TryParseXml(dataSource.XmlDefinition, out var definition))
        {
            return;
        }

        ResolveParameterFields(builder, dataSource, dataSourceKey, definition!, issues);
        ResolveDatabaseReferences(builder, dataSource, dataSourceKey, definition!);
    }

    /// <summary>只為 CustomParameterDataSource_* 欄位建節點，並依 Description exact match PD。</summary>
    private void ResolveParameterFields(
        GraphDocumentBuilder builder,
        CustomReportDataSourceItem dataSource,
        string dataSourceKey,
        XDocument definition,
        ICollection<PreflightIssue> issues)
    {
        foreach (var parameter in definition.Descendants().Where(element => element.Name.LocalName == "Parameter"))
        {
            var fieldType = parameter.Attribute("FieldType")?.Value;
            if (fieldType is null
                || !fieldType.StartsWith("CustomParameterDataSource_", StringComparison.Ordinal))
            {
                continue;
            }

            var name = parameter.Attribute("Name")?.Value ?? string.Empty;
            var description = parameter.Attribute("Description")?.Value;
            var fieldKey = $"report-field:{dataSource.SerialId:D}:{name}";
            builder.AddNode(
                GraphNodeKind.ReportField,
                fieldKey,
                new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["field_type"] = fieldType,
                    ["description"] = description,
                });
            builder.AddRelationship(
                GraphRelationshipKind.HasField,
                dataSourceKey,
                fieldKey,
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.Xml,
                    DatabaseObject = "dbo.tblCustomDesignReportDataSource",
                    RowKey = dataSource.SerialId.ToString("D"),
                    XmlPath = $"//Parameter[@Name='{name}']",
                });

            var parameterSources = _catalog.ParameterDataSources
                .Where(item => string.Equals(item.Description, description, StringComparison.Ordinal))
                .ToArray();
            if (parameterSources.Length > 1)
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.CustomParameterDataSourceAmbiguous,
                    $"DS 欄位 '{name}' 的 Description 對應多筆 PD。",
                    FromKey: fieldKey,
                    TargetText: description,
                    Candidates: parameterSources.Select(item => item.SerialId.ToString("D")).ToArray()));
                continue;
            }

            if (parameterSources.Length == 1)
            {
                var source = parameterSources[0];
                var sourceKey = $"custom-parameter-ds:{source.SerialId:D}";
                builder.AddNode(
                    GraphNodeKind.CustomParameterDataSource,
                    sourceKey,
                    new Dictionary<string, object?>
                    {
                        ["serial_id"] = source.SerialId.ToString("D"),
                        ["description"] = source.Description,
                    });
                builder.AddRelationship(
                    GraphRelationshipKind.UsesParameterSource,
                    fieldKey,
                    sourceKey,
                    new GraphEvidence
                    {
                        SourceKind = GraphSourceKind.DatabaseRow,
                        DatabaseObject = "dbo.tblCustomDesignReportCustomParameterDataSource",
                        DatabaseColumn = "Description",
                        RowKey = source.SerialId.ToString("D"),
                        RawValue = description,
                    });
            }
        }
    }

    /// <summary>只在 SQL／Query／Command／Script XML 節點中比對實際 sys.objects 名稱。</summary>
    private void ResolveDatabaseReferences(
        GraphDocumentBuilder builder,
        CustomReportDataSourceItem dataSource,
        string dataSourceKey,
        XDocument definition)
    {
        var queryTexts = definition.Descendants()
            .Where(element => IsQueryElement(element.Name.LocalName))
            .Select(element => element.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        foreach (var queryText in queryTexts)
        {
            var tokens = SqlIdentifierRegex().Matches(queryText)
                .Select(match => match.Value.Split('.').Last().Trim('[', ']'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var databaseObject in _databaseObjects.Where(item => tokens.Contains(item.ObjectName)))
            {
                builder.AddNode(
                    GraphNodeKind.DatabaseObject,
                    databaseObject.CreateNodeKey(),
                    new Dictionary<string, object?>
                    {
                        ["provider"] = databaseObject.Provider,
                        ["database"] = databaseObject.DatabaseName,
                        ["schema"] = databaseObject.SchemaName,
                        ["name"] = databaseObject.ObjectName,
                        ["object_kind"] = databaseObject.Kind.ToString(),
                    });
                builder.AddRelationship(
                    GraphRelationshipKind.Queries,
                    dataSourceKey,
                    databaseObject.CreateNodeKey(),
                    new GraphEvidence
                    {
                        SourceKind = GraphSourceKind.Xml,
                        DatabaseObject = "dbo.tblCustomDesignReportDataSource",
                        RowKey = dataSource.SerialId.ToString("D"),
                        XmlPath = "//*[contains(local-name(),'Query|SQL|Command|Script')]",
                        SourceText = databaseObject.ObjectName,
                    });
            }
        }
    }

    /// <summary>判斷 XML element 是否承載可執行查詢文字。</summary>
    private static bool IsQueryElement(string localName) =>
        localName.Contains("Sql", StringComparison.OrdinalIgnoreCase)
        || localName.Contains("Query", StringComparison.OrdinalIgnoreCase)
        || localName.Contains("Command", StringComparison.OrdinalIgnoreCase)
        || localName.Contains("Script", StringComparison.OrdinalIgnoreCase);

    /// <summary>判斷是否為 CustomReport 中心 Menu。</summary>
    private static bool IsCustomReportMenu(GraphNode node) =>
        node.Kind == GraphNodeKind.Menu
        && node.Properties.GetValueOrDefault("resolver_kind")?.ToString()
            == MenuResolverKind.CustomReport.ToString();

    /// <summary>解析固定路由最後一段 GUID。</summary>
    private static bool TryReadTemplateId(string link, out Guid templateId)
    {
        templateId = Guid.Empty;
        return link.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(link[Prefix.Length..].Trim('/'), out templateId);
    }

    /// <summary>安全解析資料庫 XML；空值或格式錯誤回傳 false。</summary>
    private static bool TryParseXml(string xml, out XDocument? document)
    {
        try
        {
            document = string.IsNullOrWhiteSpace(xml) ? null : XDocument.Parse(xml);
            return document is not null;
        }
        catch (System.Xml.XmlException)
        {
            document = null;
            return false;
        }
    }

    /// <summary>建立一致的 RT 找不到錯誤。</summary>
    private static PreflightIssue CreateTemplateIssue(GraphNode menu, string target) => new(
        PreflightSeverity.Error,
        PreflightReasonCode.CustomReportTemplateNotFound,
        "CustomReport LinkAddress 找不到唯一 RT。",
        MenuId: menu.Properties["menu_id"]?.ToString(),
        FromKey: menu.Key,
        TargetText: target);

    /// <summary>擷取 SQL 中可與 sys.objects 精確比對的 identifier。</summary>
    [GeneratedRegex(@"(?:\b[a-zA-Z_][a-zA-Z0-9_]*\b|\[[^\]]+\])(?:\.(?:\b[a-zA-Z_][a-zA-Z0-9_]*\b|\[[^\]]+\]))?")]
    private static partial Regex SqlIdentifierRegex();
}

