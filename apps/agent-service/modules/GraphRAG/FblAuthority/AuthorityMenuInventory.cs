namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 建立全部 resolver 共用的第一層中心圖。
/// 此 Extractor 只盤點 Menu、tblMenuMap 與 Endpoint，不冒充完整功能抽取。
/// </summary>
public sealed class MenuInventoryExtractor
{
    /// <summary>把中心 SQL 結果轉成 enum 節點與 enum 關係。</summary>
    public GraphDocument Extract(
        IReadOnlyList<MenuCatalogItem> menus,
        string sourceRoot,
        string? sourceCommit = null,
        string? databaseSnapshotIdentity = null)
    {
        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var metadata = new GraphRunMetadata(
            runId,
            DateTimeOffset.UtcNow,
            sourceRoot,
            "FBL_SPV_SIT",
            GraphBuildStage.MenuInventory,
            sourceCommit,
            databaseSnapshotIdentity);
        var builder = new GraphDocumentBuilder(metadata);

        // tblMenuMap 本身是 DEFINED_IN 的目標，因此以 DatabaseObject enum 建立一次。
        const string menuTableKey = "db:FBL_SPV_SIT:dbo:tblMenuMap";
        builder.AddNode(
            GraphNodeKind.DatabaseObject,
            menuTableKey,
            new Dictionary<string, object?>
            {
                ["database"] = "FBL_SPV_SIT",
                ["schema"] = "dbo",
                ["name"] = "tblMenuMap",
                ["object_kind"] = DatabaseObjectKind.Table.ToString(),
            });

        foreach (var menu in menus)
        {
            AddMenuAndEndpoint(builder, menu, menuTableKey);
        }

        return builder.Build();
    }

    /// <summary>加入單一 Menu、中心 row、正規化 Endpoint 與兩條直接關係。</summary>
    private static void AddMenuAndEndpoint(
        GraphDocumentBuilder builder,
        MenuCatalogItem menu,
        string menuTableKey)
    {
        var menuKey = $"menu:{menu.Id}";
        var normalizedLinkAddress = LinkAddressParser.Normalize(menu.LinkAddress);
        var resolverKind = LinkAddressParser.Classify(normalizedLinkAddress);
        var endpointKey = LinkAddressParser.CreateEndpointKey(normalizedLinkAddress);

        // Menu 同時保存 DB 原值與正規化值，後續可稽核任何路由差異。
        builder.AddNode(
            GraphNodeKind.Menu,
            menuKey,
            new Dictionary<string, object?>
            {
                ["menu_id"] = menu.Id,
                ["name"] = menu.Name,
                ["description"] = menu.Description,
                ["link_address"] = menu.LinkAddress,
                ["normalized_link_address"] = normalizedLinkAddress,
                ["resolver_kind"] = resolverKind.ToString(),
            });

        // 多個 Menu 若共用同一 Endpoint，Builder 會依 canonical Key 去重。
        builder.AddNode(
            GraphNodeKind.Endpoint,
            endpointKey,
            new Dictionary<string, object?>
            {
                ["path"] = normalizedLinkAddress,
            });

        // DEFINED_IN 直接來自 tblMenuMap row，不需要任何推測。
        builder.AddRelationship(
            GraphRelationshipKind.DefinedIn,
            menuKey,
            menuTableKey,
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.DatabaseRow,
                DatabaseObject = "dbo.tblMenuMap",
                RowKey = menu.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DatabaseColumn = "ID",
            });

        // OPENS 直接來自該 row 的 LinkAddress，原值放在 raw_value 供人工比對。
        builder.AddRelationship(
            GraphRelationshipKind.Opens,
            menuKey,
            endpointKey,
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.Route,
                DatabaseObject = "dbo.tblMenuMap",
                DatabaseColumn = "LinkAddress",
                RowKey = menu.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RawValue = menu.LinkAddress,
            });
    }
}

