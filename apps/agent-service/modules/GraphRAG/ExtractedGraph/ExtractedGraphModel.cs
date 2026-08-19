using System.Security.Cryptography;
using System.Text;

namespace AgentService.Modules.GraphRAG.ExtractedGraph;

/// <summary>
/// ParallelExtractor 會產生的原始節點標籤。
/// 這份列舉不包含 Modern Wingman 舊抽取器的相容型別；列舉名稱就是 Neo4j Label。
/// </summary>
public enum GraphNodeKind
{
    Solution,
    Project,
    File,
    Namespace,
    Type,
    Method,
    CodeChunk,
    ExternalSymbol,
    WebFormPage,
    ScriptAsset,
    FrontendBundle,
    FrontendComponent,
    FrontendFunction,
    UIElement,
    ApiEndpoint,
    UnresolvedView,
    View,
    Database,
    DatabaseObject,
    DatabaseColumn,
    StoredProcedureParameter,
    ConnectionProfile,
    Schedule,
    ScheduleTaskInstance,
    ScheduleTaskDefinition,
    ReportTemplate,
    ReportDocument,
    ReportDataSource,
    CustomParameterDataSource,
    ReportParameter,
    ResultColumn,
    CsvFormat,
    CategoryType,
    MenuItem,
    PluginReportTarget,
    EfModel,
    EfEntity,
    DynamicStoredProcedureCall,
}

/// <summary>
/// ParallelExtractor 會產生的原始關係型別。
/// 這份列舉只負責阻止任意 Cypher token；儲存名稱必須由 <see cref="GraphSchema"/> 原樣映射。
/// </summary>
public enum GraphRelationshipKind
{
    ContainsProject,
    ReferencesProject,
    ContainsFile,
    DeclaresNamespace,
    ImportsNamespace,
    DeclaresType,
    ContainsType,
    DeclaresMethod,
    Calls,
    Instantiates,
    DerivesFrom,
    Implements,
    Overrides,
    ImplementsMethod,
    UnresolvedBase,
    HasChunk,
    ContainsFrontendAsset,
    RepresentsSourceFile,
    IncludesScript,
    DeclaresComponent,
    DeclaresFunction,
    ComponentHasFunction,
    UsesUiElement,
    RendersUiElement,
    CallsApi,
    ImportsScript,
    ReturnsView,
    CompiledTo,
    ContainsObject,
    HasColumn,
    HasParameter,
    ForeignKeyTo,
    Reads,
    Writes,
    CallsUdf,
    TargetsDatabase,
    UsesConnection,
    DefinedInProject,
    HasScheduleTask,
    ResolvesToTaskDefinition,
    ExecutesStoredProcedure,
    ContainsReportDocument,
    UsesDataSource,
    UsesCustomParameterSource,
    ReturnsColumn,
    MapsToCategoryType,
    ChildOf,
    NavigatesToController,
    NavigatesToAction,
    LinksToCustomReport,
    LinksToPluginReport,
    ImplementedByType,
    ContainsEfModel,
    DescribesEntity,
    EfMapsTo,
    GeneratesEntityType,
    CustomMapsTo,
    ReferencesDatabaseObject,
    ReferencesStoredProcedure,
    ReferencesUdf,
    CallsStoredProcedure,
    FileCallsStoredProcedure,
    BindsParameter,
    DynamicallyCallsStoredProcedure,
    RendersView,
}

/// <summary>集中保存新圖格式的版本號。</summary>
public static class ProjectGraphVersions
{
    public const string Indexer = "parallel-extractor-v2";
    public const string CanonicalSchema = "parallel-extractor-raw-v2";
    public const string StorageSchema = "wingman-versioned-graph-v2";
    public const string Community = "weighted-leiden-v1";
}

/// <summary>將受控列舉映射成 ParallelExtractor 的原始 Neo4j token。</summary>
public static class GraphSchema
{
    private static readonly IReadOnlyDictionary<GraphRelationshipKind, string> RelationshipTypes =
        new Dictionary<GraphRelationshipKind, string>
        {
            [GraphRelationshipKind.ContainsProject] = "CONTAINS_PROJECT",
            [GraphRelationshipKind.ReferencesProject] = "REFERENCES_PROJECT",
            [GraphRelationshipKind.ContainsFile] = "CONTAINS_FILE",
            [GraphRelationshipKind.DeclaresNamespace] = "DECLARES_NAMESPACE",
            [GraphRelationshipKind.ImportsNamespace] = "IMPORTS_NAMESPACE",
            [GraphRelationshipKind.DeclaresType] = "DECLARES_TYPE",
            [GraphRelationshipKind.ContainsType] = "CONTAINS_TYPE",
            [GraphRelationshipKind.DeclaresMethod] = "DECLARES_METHOD",
            [GraphRelationshipKind.Calls] = "CALLS",
            [GraphRelationshipKind.Instantiates] = "INSTANTIATES",
            [GraphRelationshipKind.DerivesFrom] = "DERIVES_FROM",
            [GraphRelationshipKind.Implements] = "IMPLEMENTS",
            [GraphRelationshipKind.Overrides] = "OVERRIDES",
            [GraphRelationshipKind.ImplementsMethod] = "IMPLEMENTS_METHOD",
            [GraphRelationshipKind.UnresolvedBase] = "UNRESOLVED_BASE",
            [GraphRelationshipKind.HasChunk] = "HAS_CHUNK",
            [GraphRelationshipKind.ContainsFrontendAsset] = "CONTAINS_FRONTEND_ASSET",
            [GraphRelationshipKind.RepresentsSourceFile] = "REPRESENTS_SOURCE_FILE",
            [GraphRelationshipKind.IncludesScript] = "INCLUDES_SCRIPT",
            [GraphRelationshipKind.DeclaresComponent] = "DECLARES_COMPONENT",
            [GraphRelationshipKind.DeclaresFunction] = "DECLARES_FUNCTION",
            [GraphRelationshipKind.ComponentHasFunction] = "COMPONENT_HAS_FUNCTION",
            [GraphRelationshipKind.UsesUiElement] = "USES_UI_ELEMENT",
            [GraphRelationshipKind.RendersUiElement] = "RENDERS_UI_ELEMENT",
            [GraphRelationshipKind.CallsApi] = "CALLS_API",
            [GraphRelationshipKind.ImportsScript] = "IMPORTS_SCRIPT",
            [GraphRelationshipKind.ReturnsView] = "RETURNS_VIEW",
            [GraphRelationshipKind.CompiledTo] = "COMPILED_TO",
            [GraphRelationshipKind.ContainsObject] = "CONTAINS_OBJECT",
            [GraphRelationshipKind.HasColumn] = "HAS_COLUMN",
            [GraphRelationshipKind.HasParameter] = "HAS_PARAMETER",
            [GraphRelationshipKind.ForeignKeyTo] = "FOREIGN_KEY_TO",
            [GraphRelationshipKind.Reads] = "READS",
            [GraphRelationshipKind.Writes] = "WRITES",
            [GraphRelationshipKind.CallsUdf] = "CALLS_UDF",
            [GraphRelationshipKind.TargetsDatabase] = "TARGETS_DATABASE",
            [GraphRelationshipKind.UsesConnection] = "USES_CONNECTION",
            [GraphRelationshipKind.DefinedInProject] = "DEFINED_IN_PROJECT",
            [GraphRelationshipKind.HasScheduleTask] = "HAS_SCHEDULE_TASK",
            [GraphRelationshipKind.ResolvesToTaskDefinition] = "RESOLVES_TO_TASK_DEFINITION",
            [GraphRelationshipKind.ExecutesStoredProcedure] = "EXECUTES_STORED_PROCEDURE",
            [GraphRelationshipKind.ContainsReportDocument] = "CONTAINS_REPORT_DOCUMENT",
            [GraphRelationshipKind.UsesDataSource] = "USES_DATA_SOURCE",
            [GraphRelationshipKind.UsesCustomParameterSource] = "USES_CUSTOM_PARAMETER_SOURCE",
            [GraphRelationshipKind.ReturnsColumn] = "RETURNS_COLUMN",
            [GraphRelationshipKind.MapsToCategoryType] = "MAPS_TO_CATEGORY_TYPE",
            [GraphRelationshipKind.ChildOf] = "CHILD_OF",
            [GraphRelationshipKind.NavigatesToController] = "NAVIGATES_TO_CONTROLLER",
            [GraphRelationshipKind.NavigatesToAction] = "NAVIGATES_TO_ACTION",
            [GraphRelationshipKind.LinksToCustomReport] = "LINKS_TO_CUSTOM_REPORT",
            [GraphRelationshipKind.LinksToPluginReport] = "LINKS_TO_PLUGIN_REPORT",
            [GraphRelationshipKind.ImplementedByType] = "IMPLEMENTED_BY_TYPE",
            [GraphRelationshipKind.ContainsEfModel] = "CONTAINS_EF_MODEL",
            [GraphRelationshipKind.DescribesEntity] = "DESCRIBES_ENTITY",
            [GraphRelationshipKind.EfMapsTo] = "EF_MAPS_TO",
            [GraphRelationshipKind.GeneratesEntityType] = "GENERATES_ENTITY_TYPE",
            [GraphRelationshipKind.CustomMapsTo] = "CUSTOM_MAPS_TO",
            [GraphRelationshipKind.ReferencesDatabaseObject] = "REFERENCES_DATABASE_OBJECT",
            [GraphRelationshipKind.ReferencesStoredProcedure] = "REFERENCES_STORED_PROCEDURE",
            [GraphRelationshipKind.ReferencesUdf] = "REFERENCES_UDF",
            [GraphRelationshipKind.CallsStoredProcedure] = "CALLS_STORED_PROCEDURE",
            [GraphRelationshipKind.FileCallsStoredProcedure] = "FILE_CALLS_STORED_PROCEDURE",
            [GraphRelationshipKind.BindsParameter] = "BINDS_PARAMETER",
            [GraphRelationshipKind.DynamicallyCallsStoredProcedure] = "DYNAMICALLY_CALLS_STORED_PROCEDURE",
            [GraphRelationshipKind.RendersView] = "RENDERS_VIEW",
        };

    public static string GetNodeLabel(GraphNodeKind kind) => kind.ToString();

    public static string GetRelationshipType(GraphRelationshipKind kind) =>
        RelationshipTypes.TryGetValue(kind, out var value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "關係型別尚未建立原始名稱映射。");

    public static bool TryParseNodeLabel(string label, out GraphNodeKind kind) =>
        Enum.TryParse(label, ignoreCase: false, out kind) && GetNodeLabel(kind) == label;

    public static bool TryParseRelationshipType(string type, out GraphRelationshipKind kind)
    {
        foreach (var pair in RelationshipTypes)
        {
            if (pair.Value.Equals(type, StringComparison.Ordinal))
            {
                kind = pair.Key;
                return true;
            }
        }
        kind = default;
        return false;
    }

    public static void EnsureCompleteMappings()
    {
        if (RelationshipTypes.Count != Enum.GetValues<GraphRelationshipKind>().Length)
            throw new InvalidOperationException("ParallelExtractor 關係型別映射不完整。");
    }
}

/// <summary>一個 ParallelExtractor 原始節點；Properties 不會加入搜尋或相容輔助欄位。</summary>
public sealed record GraphNode(
    string Key,
    GraphNodeKind Kind,
    IReadOnlyDictionary<string, object?> Properties)
{
    public static GraphNode Create(
        GraphNodeKind kind,
        string key,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        new(
            key,
            kind,
            properties is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(properties, StringComparer.Ordinal));
}

/// <summary>一條 ParallelExtractor 原始關係；Properties 直接保存原始屬性。</summary>
public sealed record GraphRelationship(
    string Id,
    string SourceKey,
    string TargetKey,
    GraphRelationshipKind Kind,
    IReadOnlyDictionary<string, object?> Properties)
{
    public static GraphRelationship Create(
        GraphRelationshipKind kind,
        string sourceKey,
        string targetKey,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var rawType = GraphSchema.GetRelationshipType(kind);
        var identity = $"{rawType}|{sourceKey}|{targetKey}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return new GraphRelationship(
            id,
            sourceKey,
            targetKey,
            kind,
            properties is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(properties, StringComparer.Ordinal));
    }
}

/// <summary>單次完整抽取的來源資訊；此資料只保存於 SQLite manifest，不寫入 GraphEntity。</summary>
public sealed record GraphRunMetadata(
    string RunId,
    DateTimeOffset GeneratedAt,
    string SourceRoot,
    string DatabaseName,
    string? SourceCommit,
    string? DatabaseSnapshotIdentity,
    string Provider = "SourceOnly");

/// <summary>Neo4j 發布與 1:1 驗收共用的原始圖文件。</summary>
public sealed record GraphDocument(
    GraphRunMetadata Metadata,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphRelationship> Relationships);

/// <summary>
/// 組合多個資料來源的原始圖。節點以 label+id、關係以 type+source+target 去重，
/// 重複屬性採最後一個非 null 值，對齊原始 Neo4j writer 的 SET += 語意。
/// </summary>
public sealed class GraphDocumentBuilder
{
    private readonly GraphRunMetadata _metadata;
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphRelationship> _relationships = new(StringComparer.Ordinal);

    public GraphDocumentBuilder(GraphRunMetadata metadata) => _metadata = metadata;

    public GraphNode AddNode(
        GraphNodeKind kind,
        string key,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var identity = $"{GraphSchema.GetNodeLabel(kind)}|{key}";
        if (!_nodes.TryGetValue(identity, out var node))
            node = GraphNode.Create(kind, key);
        var merged = new Dictionary<string, object?>(node.Properties, StringComparer.Ordinal);
        if (properties is not null)
        {
            foreach (var pair in properties)
                if (pair.Value is not null)
                    merged[pair.Key] = pair.Value;
        }
        node = node with { Properties = merged };
        _nodes[identity] = node;
        return node;
    }

    public GraphRelationship AddRelationship(
        GraphRelationshipKind kind,
        string sourceKey,
        string targetKey,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var relationship = GraphRelationship.Create(kind, sourceKey, targetKey, properties);
        _relationships[relationship.Id] = relationship;
        return relationship;
    }

    public GraphDocument Build() => new(
        _metadata,
        _nodes.Values.OrderBy(node => node.Kind).ThenBy(node => node.Key, StringComparer.Ordinal).ToArray(),
        _relationships.Values.OrderBy(edge => edge.Kind).ThenBy(edge => edge.SourceKey, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetKey, StringComparer.Ordinal).ToArray());
}
