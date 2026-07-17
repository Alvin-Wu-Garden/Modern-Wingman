namespace AgentService.Domain.Models;

/// <summary>程式碼圖譜節點種類。</summary>
public enum CodeNodeKind
{
    Project,
    Solution,
    Assembly,
    Package,
    Module,
    Namespace,
    Type,       // class / interface / struct / enum / record
    Annotation,
    Dependency,
    Method,
    Property,
    Field,
    File,
    Route,
    Endpoint,
    RequestContract,
    ResponseContract,
    BackgroundJob,
    EventConsumer,
    ConfigurationKey,
    Test,
    DataStore,
    Schema,
    Table,
    Collection,
    Column,
    DataField,
    PrimaryKey,
    ForeignKey,
    Index,
    Constraint,
    View,
    Procedure,
    Query,
    Migration,
    DomainTerm,
}

/// <summary>程式碼圖譜關係種類。</summary>
public enum CodeEdgeKind
{
    Contains,    // Namespace CONTAINS Type, Type CONTAINS Method...
    Calls,       // Method CALLS Method
    Implements,  // Type IMPLEMENTS Interface
    Inherits,    // Type INHERITS Type
    References,  // Type REFERENCES Type（欄位/參數/回傳型別）
    DeclaredIn,  // Type DECLARED_IN File
    ProjectReferences,
    DependsOnPackage,
    DispatchesTo,
    Overrides,
    Tests,
    Covers,
    Handles,
    Consumes,
    Produces,
    BindsConfiguration,
    MapsTo,
    Reads,
    Writes,
    ForeignKeyTo,
    Migrates,
    SerializesTo,
    Publishes,
    Aliases,
    SupportedBy,
}

/// <summary>圖譜證據的產生來源。Unknown 讓舊資料保持可反序列化。</summary>
public enum GraphSourceKind
{
    Unknown,
    Compiler,
    Ast,
    FrameworkAdapter,
    Migration,
    Sql,
    Heuristic,
    LlmProposal,
    ItConfirmed,
}

/// <summary>圖譜事實的可信度；推論不得冒充已解析的程式事實。</summary>
public enum GraphConfidence
{
    Unknown,
    Exact,
    Resolved,
    Heuristic,
    Inferred,
    Confirmed,
}

/// <summary>
/// 靜態分析輸出的程式碼節點（語言無關的統一模型；
/// Roslyn / Java 分析器各自映射到此模型再寫入 Neo4j）。
/// </summary>
public sealed class CodeNode
{
    /// <summary>穩定唯一鍵，例如 "MyApp.Services.OrderService.CalculateTotal(int)"。</summary>
    public required string Key { get; init; }

    public required CodeNodeKind Kind { get; init; }

    /// <summary>短名稱（"CalculateTotal"）。</summary>
    public required string Name { get; init; }

    /// <summary>完整簽章或完整名稱。</summary>
    public string? Signature { get; init; }

    /// <summary>相對於專案根目錄的檔案路徑。</summary>
    public string? FilePath { get; init; }

    public int? StartLine { get; init; }
    public int? EndLine { get; init; }

    /// <summary>"csharp" | "java"</summary>
    public required string Language { get; init; }

    /// <summary>框架或建置技術，例如 aspnetcore、spring、maven。</summary>
    public string? Technology { get; init; }

    public GraphSourceKind SourceKind { get; init; } = GraphSourceKind.Unknown;
    public GraphConfidence Confidence { get; init; } = GraphConfidence.Unknown;
    public string? ExtractorId { get; init; }
    public string? ExtractorVersion { get; init; }
    public DateTimeOffset? IndexedAt { get; init; }
    public string? ContentHash { get; init; }
    public string? Reason { get; init; }

    /// <summary>
    /// Graph schema V2 canonical arrays. Stored as deterministic JSON because Neo4j
    /// properties cannot contain nested maps. Legacy/primary provenance fields above
    /// remain populated for existing consumers.
    /// </summary>
    public string? LocationsJson { get; init; }
    public string? EvidenceJson { get; init; }

    /// <summary>XML doc / Javadoc 摘要（若有）。</summary>
    public string? DocComment { get; init; }
}

/// <summary>靜態分析輸出的關係邊。</summary>
public sealed class CodeEdge
{
    public required string SourceKey { get; init; }
    public required string TargetKey { get; init; }
    public required CodeEdgeKind Kind { get; init; }
    public GraphSourceKind SourceKind { get; set; } = GraphSourceKind.Unknown;
    public GraphConfidence Confidence { get; set; } = GraphConfidence.Unknown;
    public string? ExtractorId { get; init; }
    public string? ExtractorVersion { get; init; }
    public string? Reason { get; set; }
    /// <summary>Artifact that produced this edge; used for exact incremental ownership.</summary>
    public string? ArtifactPath { get; set; }
    public string? EvidenceJson { get; init; }
    public DateTimeOffset? IndexedAt { get; set; }
    public string? ContentHash { get; set; }
}

/// <summary>單一檔案/專案的分析結果。</summary>
public sealed class CodeAnalysisResult
{
    public List<CodeNode> Nodes { get; } = [];
    public List<CodeEdge> Edges { get; } = [];
}
