using System.Text.RegularExpressions;
namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 驗證 FBL 696 個中心菜單是否保留參考專案已確認的強型別核心鏈。
/// 驗證器只讀取單次 <see cref="GraphDocument"/>，不存取 Neo4j、SQL Server 或檔案系統。
/// </summary>
public sealed partial class FblMenuChainValidator
{
    private const int MaximumDatabaseTraversalDepth = 8;

    private static readonly IReadOnlySet<GraphRelationshipKind> DatabaseTraversalRelations =
        new HashSet<GraphRelationshipKind>
        {
            GraphRelationshipKind.Uses,
            GraphRelationshipKind.Extends,
            GraphRelationshipKind.DispatchesWith,
            GraphRelationshipKind.ResolvesTo,
            GraphRelationshipKind.CreatesTransform,
            GraphRelationshipKind.UsesUploadHandler,
            GraphRelationshipKind.UsesBatchProcessor,
            GraphRelationshipKind.ReadsVia,
            GraphRelationshipKind.WritesVia,
            GraphRelationshipKind.UsesDefinition,
            GraphRelationshipKind.MapsTo,
            GraphRelationshipKind.DependsOn,
            GraphRelationshipKind.ReadsData,
            GraphRelationshipKind.Executes,
            GraphRelationshipKind.Queries,
            GraphRelationshipKind.UsesBackendControl,
            GraphRelationshipKind.UsesParameterSource,
        };

    /// <summary>
    /// 以 O(V+E) 建立節點與鄰接索引，再逐一驗證 oracle 中的 696 個菜單。
    /// 不允許用所有 Menu 都具備的 DEFINED_IN 關係冒充功能鏈。
    /// </summary>
    public FblMenuChainValidationResult Validate(
        GraphDocument document,
        FblMenuChainOracle oracle)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(oracle);

        var failures = new List<FblMenuChainFailure>();
        var nodes = BuildNodeIndex(document.Nodes, failures);
        var adjacency = BuildAdjacency(document.Relationships);

        ValidateOracleShape(oracle, failures);
        ValidateDanglingRelationships(document.Relationships, nodes, failures);
        ValidateEvidenceAndSensitiveValues(document, failures);

        var actualMenus = document.Nodes
            .Where(node => node.Kind == GraphNodeKind.Menu)
            .ToArray();
        ValidateMenuInventory(actualMenus, oracle, failures);

        var standardPassed = 0;
        var pluginPassed = 0;
        var customPassed = 0;
        var pluginDatabaseReachable = 0;
        var customWithDataSource = 0;
        var customWithDatabase = 0;

        foreach (var expected in oracle.Menus.OrderBy(menu => menu.MenuId))
        {
            if (!nodes.TryGetValue(expected.MenuKey, out var menuNode))
            {
                failures.Add(CreateFailure(expected, "Menu", null, expected.MenuKey, "找不到中心 Menu 節點。"));
                continue;
            }

            ValidateMenuProperties(menuNode, expected, failures);
            if (!RequireEdge(
                    expected,
                    nodes,
                    adjacency,
                    expected.MenuKey,
                    GraphRelationshipKind.Opens,
                    expected.EndpointKey,
                    GraphNodeKind.Endpoint,
                    "Menu->Endpoint",
                    failures))
            {
                continue;
            }

            switch (expected.ResolverKind)
            {
                case nameof(MenuResolverKind.StandardWeb):
                    if (ValidateStandardWebCore(expected, nodes, adjacency, failures))
                    {
                        standardPassed++;
                    }
                    break;

                case nameof(MenuResolverKind.PluginReport):
                    if (ValidatePluginReportCore(expected, nodes, adjacency, failures))
                    {
                        pluginPassed++;
                    }
                    if (CanReachDatabase(expected.PrimaryTargetKey, nodes, adjacency))
                    {
                        pluginDatabaseReachable++;
                    }
                    else if (expected.RequiresDatabaseReachability)
                    {
                        failures.Add(CreateFailure(
                            expected,
                            "PluginReport->DatabaseObject",
                            expected.PrimaryTargetKey,
                            null,
                            $"參考專案中此 ReportKernel 可在 {MaximumDatabaseTraversalDepth} hops 內到達資料庫物件，但目前鏈路已退化。"));
                    }
                    break;

                case nameof(MenuResolverKind.CustomReport):
                    if (ValidateCustomReportCore(expected, nodes, adjacency, failures))
                    {
                        customPassed++;
                    }

                    var dataSources = GetTargets(
                        adjacency,
                        expected.PrimaryTargetKey,
                        GraphRelationshipKind.ContainsDataSource);
                    if (dataSources.Count > 0)
                    {
                        customWithDataSource++;
                    }
                    else if (expected.RequiresDataSource)
                    {
                        failures.Add(CreateFailure(
                            expected,
                            "Template->DataSource",
                            expected.PrimaryTargetKey,
                            null,
                            "參考專案中此 Template 宣告 DataSourceSerialID，但目前沒有 CONTAINS_DATA_SOURCE。"));
                    }

                    var hasCustomDatabase = dataSources.Any(dataSource =>
                        GetTargets(adjacency, dataSource, GraphRelationshipKind.Queries)
                            .Any(target => nodes.TryGetValue(target, out var node) &&
                                           node.Kind == GraphNodeKind.DatabaseObject));
                    if (hasCustomDatabase)
                    {
                        customWithDatabase++;
                    }
                    else if (expected.RequiresDatabaseReachability)
                    {
                        failures.Add(CreateFailure(
                            expected,
                            "CustomReport->DatabaseObject",
                            dataSources.FirstOrDefault() ?? expected.PrimaryTargetKey,
                            null,
                            "參考專案中此 CustomReport 可到達資料庫物件，但目前 QUERIES 鏈路已退化。"));
                    }
                    break;

                default:
                    failures.Add(CreateFailure(
                        expected,
                        "ResolverKind",
                        expected.MenuKey,
                        expected.ResolverKind,
                        "oracle 包含未支援的 resolver_kind。"));
                    break;
            }
        }

        ValidateGoldenPaths(document, nodes, adjacency, failures);
        ValidateAggregateThresholds(
            oracle,
            pluginDatabaseReachable,
            customWithDataSource,
            customWithDatabase,
            failures);

        return new FblMenuChainValidationResult(
            oracle.SourceRunId,
            document.Metadata.RunId,
            actualMenus.Length,
            standardPassed,
            pluginPassed,
            customPassed,
            pluginDatabaseReachable,
            customWithDataSource,
            customWithDatabase,
            failures.OrderBy(failure => failure.MenuId).ThenBy(failure => failure.FailedSegment).ToArray());
    }

    /// <summary>建立 stable key 索引；重複 Key 必須明確失敗，不得由後一筆覆蓋。</summary>
    private static IReadOnlyDictionary<string, GraphNode> BuildNodeIndex(
        IReadOnlyList<GraphNode> source,
        ICollection<FblMenuChainFailure> failures)
    {
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in source)
        {
            if (!nodes.TryAdd(node.Key, node))
            {
                failures.Add(new FblMenuChainFailure(
                    null,
                    null,
                    null,
                    "DuplicateNode",
                    node.Key,
                    node.Key,
                    Array.Empty<string>(),
                    null,
                    null,
                    "GraphDocument 出現重複 stable key。"));
            }
        }
        return nodes;
    }

    /// <summary>將關係一次分組為 (source, kind) 鄰接表，避免 696 次重掃全部 edge。</summary>
    private static IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> BuildAdjacency(
        IReadOnlyList<GraphRelationship> relationships)
    {
        return relationships
            .GroupBy(edge => new AdjacencyKey(edge.SourceKey, edge.Kind))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GraphRelationship>)group.ToArray());
    }

    /// <summary>檢查 oracle 本身沒有重複或分類數量錯誤，防止測試基準被意外改壞。</summary>
    private static void ValidateOracleShape(
        FblMenuChainOracle oracle,
        ICollection<FblMenuChainFailure> failures)
    {
        AddOracleCountFailure(oracle, failures, "Total", oracle.Menus.Count, oracle.ExpectedMenuCount);
        AddOracleCountFailure(
            oracle,
            failures,
            nameof(MenuResolverKind.StandardWeb),
            oracle.Menus.Count(menu => menu.ResolverKind == nameof(MenuResolverKind.StandardWeb)),
            oracle.ExpectedStandardWebCount);
        AddOracleCountFailure(
            oracle,
            failures,
            nameof(MenuResolverKind.PluginReport),
            oracle.Menus.Count(menu => menu.ResolverKind == nameof(MenuResolverKind.PluginReport)),
            oracle.ExpectedPluginReportCount);
        AddOracleCountFailure(
            oracle,
            failures,
            nameof(MenuResolverKind.CustomReport),
            oracle.Menus.Count(menu => menu.ResolverKind == nameof(MenuResolverKind.CustomReport)),
            oracle.ExpectedCustomReportCount);

        foreach (var duplicate in oracle.Menus.GroupBy(menu => menu.MenuId).Where(group => group.Count() > 1))
        {
            failures.Add(new FblMenuChainFailure(
                duplicate.Key,
                null,
                null,
                "OracleDuplicateMenuId",
                null,
                duplicate.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Array.Empty<string>(),
                null,
                null,
                "oracle 中 Menu ID 重複。"));
        }

        foreach (var duplicate in oracle.Menus
                     .GroupBy(menu => menu.LinkAddress, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            failures.Add(new FblMenuChainFailure(
                null,
                null,
                duplicate.Key,
                "OracleDuplicateLinkAddress",
                null,
                duplicate.Key,
                duplicate.Select(menu => menu.MenuKey).ToArray(),
                null,
                null,
                "oracle 中 LinkAddress 重複。"));
        }
    }

    private static void AddOracleCountFailure(
        FblMenuChainOracle oracle,
        ICollection<FblMenuChainFailure> failures,
        string category,
        int actual,
        int expected)
    {
        if (actual == expected)
        {
            return;
        }

        failures.Add(new FblMenuChainFailure(
            null,
            null,
            null,
            $"OracleCount:{category}",
            null,
            expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [actual.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            null,
            null,
            $"oracle 數量不符；來源 run={oracle.SourceRunId}。"));
    }

    /// <summary>檢查實際 Menu 數量、ID 與 LinkAddress 唯一性。</summary>
    private static void ValidateMenuInventory(
        IReadOnlyList<GraphNode> actualMenus,
        FblMenuChainOracle oracle,
        ICollection<FblMenuChainFailure> failures)
    {
        if (actualMenus.Count != oracle.ExpectedMenuCount)
        {
            failures.Add(new FblMenuChainFailure(
                null,
                null,
                null,
                "MenuCount",
                null,
                oracle.ExpectedMenuCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [actualMenus.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                null,
                null,
                "中心 Menu 數量不是凍結 oracle 的 696。"));
        }

        ValidateUniqueProperty(actualMenus, "menu_id", "DuplicateMenuId", failures);
        ValidateUniqueProperty(actualMenus, "link_address", "DuplicateLinkAddress", failures);
    }

    private static void ValidateUniqueProperty(
        IEnumerable<GraphNode> menus,
        string propertyName,
        string failedSegment,
        ICollection<FblMenuChainFailure> failures)
    {
        foreach (var duplicate in menus
                     .Select(menu => new
                     {
                         Menu = menu,
                         Value = menu.Properties.GetValueOrDefault(propertyName)?.ToString(),
                     })
                     .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                     .GroupBy(item => item.Value!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            failures.Add(new FblMenuChainFailure(
                null,
                null,
                propertyName == "link_address" ? duplicate.Key : null,
                failedSegment,
                null,
                duplicate.Key,
                duplicate.Select(item => item.Menu.Key).ToArray(),
                null,
                null,
                $"中心 Menu 的 {propertyName} 不唯一。"));
        }
    }

    /// <summary>核對來源欄位，避免相同 ID 被錯接成另一個功能。</summary>
    private static void ValidateMenuProperties(
        GraphNode actual,
        FblMenuChainExpectation expected,
        ICollection<FblMenuChainFailure> failures)
    {
        RequireProperty(actual, expected, "menu_id", expected.MenuId.ToString(System.Globalization.CultureInfo.InvariantCulture), failures);
        RequireProperty(actual, expected, "name", expected.Name, failures);
        RequireProperty(actual, expected, "link_address", expected.LinkAddress, failures);
        RequireProperty(actual, expected, "resolver_kind", expected.ResolverKind, failures);
    }

    private static void RequireProperty(
        GraphNode actual,
        FblMenuChainExpectation expected,
        string propertyName,
        string expectedValue,
        ICollection<FblMenuChainFailure> failures)
    {
        var actualValue = actual.Properties.GetValueOrDefault(propertyName)?.ToString();
        if (!string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
        {
            failures.Add(CreateFailure(
                expected,
                $"MenuProperty:{propertyName}",
                actual.Key,
                expectedValue,
                $"實際值為 '{actualValue ?? "<null>"}'。"));
        }
    }

    private static bool ValidateStandardWebCore(
        FblMenuChainExpectation expected,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency,
        ICollection<FblMenuChainFailure> failures)
    {
        var actionValid = RequireEdge(
            expected,
            nodes,
            adjacency,
            expected.EndpointKey,
            GraphRelationshipKind.RoutesTo,
            expected.PrimaryTargetKey,
            GraphNodeKind.WebAction,
            "Endpoint->WebAction",
            failures);
        var controllerValid = !string.IsNullOrWhiteSpace(expected.ImplementationKey) && RequireEdge(
            expected,
            nodes,
            adjacency,
            expected.PrimaryTargetKey,
            GraphRelationshipKind.ImplementedBy,
            expected.ImplementationKey!,
            GraphNodeKind.CodeClass,
            "WebAction->Controller",
            failures);
        var downstreamValid = !string.IsNullOrWhiteSpace(expected.DownstreamKey) && RequireEdge(
            expected,
            nodes,
            adjacency,
            expected.PrimaryTargetKey,
            GraphRelationshipKind.Renders,
            expected.DownstreamKey!,
            GraphNodeKind.ViewPage,
            "WebAction->View",
            failures);
        return actionValid && controllerValid && downstreamValid;
    }

    private static bool ValidatePluginReportCore(
        FblMenuChainExpectation expected,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency,
        ICollection<FblMenuChainFailure> failures)
    {
        var menuValid = RequireEdge(
            expected,
            nodes,
            adjacency,
            expected.MenuKey,
            GraphRelationshipKind.LoadsPluginReport,
            expected.PrimaryTargetKey,
            GraphNodeKind.CodeClass,
            "Menu->ReportKernel",
            failures);
        var endpointValid = RequireEdge(
            expected,
            nodes,
            adjacency,
            expected.EndpointKey,
            GraphRelationshipKind.LoadsPluginReport,
            expected.PrimaryTargetKey,
            GraphNodeKind.CodeClass,
            "Endpoint->ReportKernel",
            failures);
        return menuValid && endpointValid;
    }

    private static bool ValidateCustomReportCore(
        FblMenuChainExpectation expected,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency,
        ICollection<FblMenuChainFailure> failures)
    {
        return RequireEdge(
            expected,
            nodes,
            adjacency,
            expected.MenuKey,
            GraphRelationshipKind.OpensCustomReport,
            expected.PrimaryTargetKey,
            GraphNodeKind.CustomReportTemplate,
            "Menu->CustomReportTemplate",
            failures);
    }

    /// <summary>驗證一段關係的 exact target、target kind 與直接證據。</summary>
    private static bool RequireEdge(
        FblMenuChainExpectation expected,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency,
        string sourceKey,
        GraphRelationshipKind relationshipKind,
        string targetKey,
        GraphNodeKind targetKind,
        string segment,
        ICollection<FblMenuChainFailure> failures)
    {
        var candidates = GetEdges(adjacency, sourceKey, relationshipKind);
        var edge = candidates.FirstOrDefault(candidate => candidate.TargetKey == targetKey);
        if (edge is null)
        {
            failures.Add(new FblMenuChainFailure(
                expected.MenuId,
                expected.Name,
                expected.LinkAddress,
                segment,
                sourceKey,
                targetKey,
                candidates.Select(candidate => candidate.TargetKey).ToArray(),
                candidates.FirstOrDefault()?.Evidence.SourceFile,
                candidates.FirstOrDefault()?.Evidence.SourceLine,
                $"缺少 {relationshipKind} exact edge。"));
            return false;
        }

        if (!nodes.TryGetValue(targetKey, out var target) || target.Kind != targetKind)
        {
            failures.Add(CreateFailure(
                expected,
                $"{segment}:TargetKind",
                sourceKey,
                $"{targetKey} ({targetKind})",
                $"實際 target kind 為 '{target?.Kind.ToString() ?? "<missing>"}'。"));
            return false;
        }

        if (!HasLocator(edge.Evidence))
        {
            failures.Add(new FblMenuChainFailure(
                expected.MenuId,
                expected.Name,
                expected.LinkAddress,
                $"{segment}:Evidence",
                sourceKey,
                targetKey,
                Array.Empty<string>(),
                edge.Evidence.SourceFile,
                edge.Evidence.SourceLine,
                "核心鏈 edge 沒有可稽核的來源定位。"));
            return false;
        }

        return true;
    }

    private static IReadOnlyList<GraphRelationship> GetEdges(
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency,
        string source,
        GraphRelationshipKind kind) =>
        adjacency.GetValueOrDefault(new AdjacencyKey(source, kind)) ?? Array.Empty<GraphRelationship>();

    private static IReadOnlyList<string> GetTargets(
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency,
        string source,
        GraphRelationshipKind kind) =>
        GetEdges(adjacency, source, kind).Select(edge => edge.TargetKey).ToArray();

    /// <summary>使用 bounded typed BFS 計算 Plugin 的資料庫可達性，避免圖爆炸。</summary>
    private static bool CanReachDatabase(
        string start,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency)
    {
        var queue = new Queue<(string Key, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        queue.Enqueue((start, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= MaximumDatabaseTraversalDepth)
            {
                continue;
            }

            foreach (var relationshipKind in DatabaseTraversalRelations)
            {
                foreach (var edge in GetEdges(adjacency, current, relationshipKind))
                {
                    if (!visited.Add(edge.TargetKey))
                    {
                        continue;
                    }

                    if (nodes.TryGetValue(edge.TargetKey, out var target) &&
                        target.Kind == GraphNodeKind.DatabaseObject)
                    {
                        return true;
                    }

                    queue.Enqueue((edge.TargetKey, depth + 1));
                }
            }
        }

        return false;
    }

    private static void ValidateDanglingRelationships(
        IReadOnlyList<GraphRelationship> relationships,
        IReadOnlyDictionary<string, GraphNode> nodes,
        ICollection<FblMenuChainFailure> failures)
    {
        foreach (var edge in relationships)
        {
            if (!nodes.ContainsKey(edge.SourceKey) || !nodes.ContainsKey(edge.TargetKey))
            {
                failures.Add(new FblMenuChainFailure(
                    null,
                    null,
                    null,
                    "DanglingEdge",
                    edge.SourceKey,
                    edge.TargetKey,
                    Array.Empty<string>(),
                    edge.Evidence.SourceFile,
                    edge.Evidence.SourceLine,
                    "關係端點不存在。"));
            }
        }
    }

    private static void ValidateEvidenceAndSensitiveValues(
        GraphDocument document,
        ICollection<FblMenuChainFailure> failures)
    {
        foreach (var edge in document.Relationships)
        {
            if (!HasLocator(edge.Evidence))
            {
                failures.Add(new FblMenuChainFailure(
                    null,
                    null,
                    null,
                    "EvidenceMissing",
                    edge.SourceKey,
                    edge.TargetKey,
                    Array.Empty<string>(),
                    null,
                    null,
                    "關係沒有檔案、資料庫列、XML 路徑或原始值可供稽核。"));
            }

            foreach (var value in EvidenceStrings(edge.Evidence))
            {
                if (ContainsSensitiveValue(value))
                {
                    failures.Add(new FblMenuChainFailure(
                        null,
                        null,
                        null,
                        "SensitiveEvidence",
                        edge.SourceKey,
                        edge.TargetKey,
                        Array.Empty<string>(),
                        edge.Evidence.SourceFile,
                        edge.Evidence.SourceLine,
                        "關係證據疑似包含密碼、Token 或完整連線字串；報告不輸出原值。"));
                    break;
                }
            }
        }

        foreach (var node in document.Nodes)
        {
            if (node.Properties.Values
                .OfType<string>()
                .Any(ContainsSensitiveValue))
            {
                failures.Add(new FblMenuChainFailure(
                    null,
                    null,
                    null,
                    "SensitiveNodeProperty",
                    node.Key,
                    null,
                    Array.Empty<string>(),
                    null,
                    null,
                    "節點屬性疑似包含秘密；報告不輸出原值。"));
            }
        }
    }

    private static bool HasLocator(GraphEvidence evidence) =>
        !string.IsNullOrWhiteSpace(evidence.SourceFile) ||
        !string.IsNullOrWhiteSpace(evidence.DatabaseObject) ||
        !string.IsNullOrWhiteSpace(evidence.RowKey) ||
        !string.IsNullOrWhiteSpace(evidence.XmlPath) ||
        !string.IsNullOrWhiteSpace(evidence.SourceText) ||
        !string.IsNullOrWhiteSpace(evidence.RawValue);

    private static IEnumerable<string> EvidenceStrings(GraphEvidence evidence)
    {
        if (evidence.SourceFile is not null) yield return evidence.SourceFile;
        if (evidence.SourceText is not null) yield return evidence.SourceText;
        if (evidence.DatabaseObject is not null) yield return evidence.DatabaseObject;
        if (evidence.DatabaseColumn is not null) yield return evidence.DatabaseColumn;
        if (evidence.RowKey is not null) yield return evidence.RowKey;
        if (evidence.XmlPath is not null) yield return evidence.XmlPath;
        if (evidence.RawValue is not null) yield return evidence.RawValue;
        if (evidence.Predicate is not null) yield return evidence.Predicate;
        foreach (var value in evidence.RequiredValues) yield return value;
    }

    private static bool ContainsSensitiveValue(string value) =>
        CredentialAssignmentRegex().IsMatch(value) ||
        ConnectionStringRegex().IsMatch(value) ||
        BearerTokenRegex().IsMatch(value);

    [GeneratedRegex(@"(?i)\b(password|pwd|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[:=]\s*[^\s;]+")]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(@"(?i)\b(server|data source)\s*=.+;\s*(user id|uid|password|pwd)\s*=")]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"(?i)\bbearer\s+[a-z0-9._-]{16,}")]
    private static partial Regex BearerTokenRegex();

    private static void ValidateGoldenPaths(
        GraphDocument document,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency,
        ICollection<FblMenuChainFailure> failures)
    {
        RequireGoldenNode(nodes, "menu:20059", GraphNodeKind.Menu, "20059 Menu", failures);
        RequireGoldenNode(nodes, "web-action:fxratemaintain.index.Query", GraphNodeKind.WebAction, "20059 Query Action", failures);
        RequireGoldenEdge(
            adjacency,
            "category:ManagementFee",
            GraphRelationshipKind.ResolvesTo,
            "code:APEX.FileTransform.TransformV2.TransformV2_P13_FxRate",
            "20059 Category->Transform",
            failures);
        RequireGoldenEdge(
            adjacency,
            "code:APEX.RiskMaster.DBDefinition.DDFxRate",
            GraphRelationshipKind.MapsTo,
            "db:FBL_SPV_SIT:dbo:tblFxRate",
            "20059 DD->tblFxRate",
            failures);
        RequireGoldenOutgoing(adjacency, "menu:20059", GraphRelationshipKind.ConfirmedBy, "20059 ConfirmedBy", failures);

        RequireGoldenNode(nodes, "menu:140078", GraphNodeKind.Menu, "140078 Menu", failures);
        RequireGoldenNode(
            nodes,
            "web-action:equitytransactionactualdeal.portfoliocombobox.none",
            GraphNodeKind.WebAction,
            "140078 PortfolioCombobox Action",
            failures);
        RequireGoldenNode(
            nodes,
            "web-action:equitytransactiontempcashincrease.gettempcashincreasetransformtoprepaymentdata.none",
            GraphNodeKind.WebAction,
            "140078 TempCashIncrease Action",
            failures);
        RequireGoldenOutgoing(adjacency, "menu:140078", GraphRelationshipKind.ConfirmedBy, "140078 ConfirmedBy", failures);

        _ = document;
    }

    private static void RequireGoldenNode(
        IReadOnlyDictionary<string, GraphNode> nodes,
        string key,
        GraphNodeKind kind,
        string description,
        ICollection<FblMenuChainFailure> failures)
    {
        if (!nodes.TryGetValue(key, out var node) || node.Kind != kind)
        {
            failures.Add(new FblMenuChainFailure(
                null, description, null, "GoldenPath", null, key, Array.Empty<string>(), null, null,
                "Golden node 缺失或種類錯誤。"));
        }
    }

    private static void RequireGoldenEdge(
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency,
        string source,
        GraphRelationshipKind kind,
        string target,
        string description,
        ICollection<FblMenuChainFailure> failures)
    {
        if (!GetEdges(adjacency, source, kind).Any(edge => edge.TargetKey == target))
        {
            failures.Add(new FblMenuChainFailure(
                null, description, null, "GoldenPath", source, target,
                GetEdges(adjacency, source, kind).Select(edge => edge.TargetKey).ToArray(), null, null,
                "Golden edge 缺失。"));
        }
    }

    private static void RequireGoldenOutgoing(
        IReadOnlyDictionary<AdjacencyKey, IReadOnlyList<GraphRelationship>> adjacency,
        string source,
        GraphRelationshipKind kind,
        string description,
        ICollection<FblMenuChainFailure> failures)
    {
        if (GetEdges(adjacency, source, kind).Count == 0)
        {
            failures.Add(new FblMenuChainFailure(
                null, description, null, "GoldenPath", source, kind.ToString(),
                Array.Empty<string>(), null, null, "Golden outgoing edge 缺失。"));
        }
    }

    private static void ValidateAggregateThresholds(
        FblMenuChainOracle oracle,
        int pluginDatabaseReachable,
        int customWithDataSource,
        int customWithDatabase,
        ICollection<FblMenuChainFailure> failures)
    {
        AddThresholdFailure("PluginDatabaseReachable", pluginDatabaseReachable, oracle.MinimumPluginDatabaseReachable, failures);
        AddThresholdFailure("CustomWithDataSource", customWithDataSource, oracle.MinimumCustomWithDataSource, failures);
        AddThresholdFailure("CustomWithDatabase", customWithDatabase, oracle.MinimumCustomWithDatabase, failures);
    }

    private static void AddThresholdFailure(
        string segment,
        int actual,
        int minimum,
        ICollection<FblMenuChainFailure> failures)
    {
        if (actual >= minimum)
        {
            return;
        }

        failures.Add(new FblMenuChainFailure(
            null,
            null,
            null,
            $"Threshold:{segment}",
            null,
            minimum.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [actual.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            null,
            null,
            "深層鏈路數量低於已驗證參考專案，判定為退化。"));
    }

    private static FblMenuChainFailure CreateFailure(
        FblMenuChainExpectation expected,
        string segment,
        string? lastReached,
        string? expectedTarget,
        string message) => new(
            expected.MenuId,
            expected.Name,
            expected.LinkAddress,
            segment,
            lastReached,
            expectedTarget,
            Array.Empty<string>(),
            null,
            null,
            message);

    private readonly record struct AdjacencyKey(string SourceKey, GraphRelationshipKind Kind);
}

/// <summary>凍結自 D:\GraphRAG 正式 run 的 696 菜單驗收基準。</summary>
public sealed record FblMenuChainOracle(
    string SourceRunId,
    int ExpectedMenuCount,
    int ExpectedStandardWebCount,
    int ExpectedPluginReportCount,
    int ExpectedCustomReportCount,
    int MinimumPluginDatabaseReachable,
    int MinimumCustomWithDataSource,
    int MinimumCustomWithDatabase,
    IReadOnlyList<FblMenuChainExpectation> Menus);

/// <summary>單一菜單的來源欄位與參考專案核心鏈 exact target。</summary>
public sealed record FblMenuChainExpectation(
    long MenuId,
    string Name,
    string LinkAddress,
    string ResolverKind,
    string MenuKey,
    string EndpointKey,
    string PrimaryTargetKey,
    string? ImplementationKey,
    string? DownstreamKey,
    bool RequiresDataSource,
    bool RequiresDatabaseReachability);

/// <summary>單一斷鏈或安全問題；可直接序列化為 JSONL。</summary>
public sealed record FblMenuChainFailure(
    long? MenuId,
    string? Name,
    string? LinkAddress,
    string FailedSegment,
    string? LastReachedKey,
    string? ExpectedTarget,
    IReadOnlyList<string> CandidateTargets,
    string? SourceFile,
    int? SourceLine,
    string Message);

/// <summary>696 驗收結果及深層鏈非退化統計。</summary>
public sealed record FblMenuChainValidationResult(
    string OracleRunId,
    string ActualRunId,
    int ActualMenuCount,
    int StandardCorePassed,
    int PluginCorePassed,
    int CustomCorePassed,
    int PluginDatabaseReachable,
    int CustomWithDataSource,
    int CustomWithDatabase,
    IReadOnlyList<FblMenuChainFailure> Failures)
{
    /// <summary>只有完全沒有失敗項目才可通過發布閘門。</summary>
    public bool IsSuccess => Failures.Count == 0;
}
