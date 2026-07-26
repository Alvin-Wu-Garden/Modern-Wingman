using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 將各 extractor 的觀察結果合併為 deterministic GraphRAG V3 snapshot。
/// 本類別是發布前的唯一品質閘門：任何 kind 衝突、dangling edge、缺少 evidence 或敏感資料都會直接拒絕，
/// 避免錯誤圖譜被 Neo4j 與 LLM 當成事實。
/// </summary>
public static partial class GraphAssembler
{
    /// <summary>GraphRAG V3 固定 schema version；不可由 profile 或專案設定切換。</summary>
    public const string SchemaVersion = "3.0";

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly Regex PasswordPattern = PasswordRegex();
    private static readonly Regex EmailPattern = EmailRegex();

    /// <summary>
    /// 合併、排序、驗證片段並計算 canonical digest。
    /// CreatedAt 與 ManifestVersion 不參與 digest，使同一份原始內容在不同執行時間仍可做 zero-diff 比較。
    /// </summary>
    /// <param name="projectId">Modern Wingman 專案 ID。</param>
    /// <param name="manifestVersion">本次 staging／active manifest。</param>
    /// <param name="createdAt">snapshot 建立時間。</param>
    /// <param name="indexer">不可改變 schema 語意的索引器版本描述。</param>
    /// <param name="workingTreeFingerprint">完整 artifact manifest 指紋。</param>
    /// <param name="mode">full、no-op 或 body-delta。</param>
    /// <param name="artifacts">完整 artifact manifest。</param>
    /// <param name="fragments">所有靜態、DB 與業務設定 extractor 片段。</param>
    /// <returns>已通過嚴格驗證的 canonical snapshot。</returns>
    public static GraphSnapshot Assemble(
        string projectId,
        string manifestVersion,
        DateTimeOffset createdAt,
        GraphIndexerDescriptor indexer,
        string workingTreeFingerprint,
        string mode,
        IReadOnlyList<GraphArtifact> artifacts,
        IEnumerable<GraphFragment> fragments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestVersion);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingTreeFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(fragments);

        var materialized = fragments.ToList();
        var nodes = MergeNodes(materialized.SelectMany(fragment => fragment.Nodes));
        var edges = MergeEdges(materialized.SelectMany(fragment => fragment.Edges));
        nodes = AddRelationshipSearchTerms(nodes, edges);
        var diagnostics = materialized.SelectMany(fragment => fragment.Diagnostics)
            .Distinct()
            .OrderBy(item => item.Severity)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Artifact, StringComparer.Ordinal)
            .ThenBy(item => item.AffectedId, StringComparer.Ordinal)
            .ToList();
        var capabilityGaps = materialized.SelectMany(fragment => fragment.CapabilityGaps)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var canonicalArtifacts = artifacts
            .Select(NormalizeArtifact)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        var canonicalIndexer = indexer with
        {
            Extractors = indexer.Extractors
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
        };

        Validate(canonicalArtifacts, nodes, edges, diagnostics);

        var payload = new CanonicalPayload(
            SchemaVersion,
            projectId,
            canonicalIndexer,
            workingTreeFingerprint,
            canonicalArtifacts,
            nodes,
            edges,
            diagnostics,
            capabilityGaps);
        var digest = GraphIdentity.Sha256(JsonSerializer.Serialize(payload, CanonicalJsonOptions));

        return new GraphSnapshot(
            SchemaVersion,
            projectId,
            manifestVersion,
            createdAt,
            canonicalIndexer,
            workingTreeFingerprint,
            mode,
            canonicalArtifacts,
            nodes,
            edges,
            diagnostics,
            capabilityGaps,
            digest);
    }

    /// <summary>
    /// 重新計算 snapshot digest，用於讀取 SQLite／Neo4j staging 後確認內容未被破壞。
    /// </summary>
    /// <param name="snapshot">待驗證 snapshot。</param>
    /// <returns>排除 runtime 欄位後重新計算的 digest。</returns>
    public static string RecomputeDigest(GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var payload = new CanonicalPayload(
            snapshot.SchemaVersion,
            snapshot.ProjectId,
            snapshot.Indexer,
            snapshot.WorkingTreeFingerprint,
            snapshot.Artifacts,
            snapshot.Nodes,
            snapshot.Edges,
            snapshot.Diagnostics,
            snapshot.CapabilityGaps);
        return GraphIdentity.Sha256(JsonSerializer.Serialize(payload, CanonicalJsonOptions));
    }

    /// <summary>
    /// 驗證 snapshot 的 canonical digest 及所有 domain invariant。
    /// 任何失敗都代表 snapshot 不可發布，而不是可忽略的警告。
    /// </summary>
    /// <param name="snapshot">待驗證 snapshot。</param>
    public static void ValidateSnapshot(GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Graph schema 必須是 {SchemaVersion}，實際為 {snapshot.SchemaVersion}。");

        Validate(snapshot.Artifacts, snapshot.Nodes, snapshot.Edges, snapshot.Diagnostics);
        var recomputed = RecomputeDigest(snapshot);
        if (!string.Equals(recomputed, snapshot.CanonicalDigest, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Canonical digest 不一致；expected={snapshot.CanonicalDigest}, actual={recomputed}。");
    }

    private static IReadOnlyList<GraphNode> MergeNodes(IEnumerable<GraphNode> source)
    {
        var result = new List<GraphNode>();
        foreach (var group in source.GroupBy(node => node.Id, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var observations = group.ToList();
            var first = observations[0];
            if (observations.Any(node => node.Kind != first.Kind))
            {
                var kinds = string.Join(", ", observations.Select(node => node.Kind).Distinct());
                throw new InvalidOperationException(
                    $"同一 node ID 不可對應不同 kind：{group.Key} => {kinds}。");
            }

            var role = SelectRole(observations.Select(node => node.Role));
            var name = SelectText(observations.Select(node => node.Name));
            var searchableText = string.Join(' ', observations
                .Select(node => node.SearchableText.Trim())
                .Append(RoleSearchText(role))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal));
            var aliases = observations.SelectMany(node => node.Aliases)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var attributes = MergeAttributes(group.Key, observations);
            var evidence = MergeEvidence(observations.SelectMany(node => node.Evidence));
            var primary = observations.OrderBy(node => EvidenceRank(node.Evidence))
                .ThenBy(node => node.FilePath, StringComparer.Ordinal)
                .ThenBy(node => node.StartLine)
                .First();

            result.Add(new GraphNode(
                group.Key,
                first.Kind,
                role,
                name,
                searchableText,
                primary.Language,
                primary.Technology,
                primary.State,
                aliases,
                primary.FilePath is null ? null : GraphIdentity.NormalizePath(primary.FilePath),
                primary.StartLine,
                primary.EndLine,
                attributes,
                evidence));
        }

        return result;
    }

    private static IReadOnlyList<GraphEdge> MergeEdges(IEnumerable<GraphEdge> source)
    {
        return source
            .GroupBy(edge => (edge.SourceId, edge.Kind, edge.TargetId))
            .Select(group =>
            {
                var expectedId = GraphIdentity.Edge(
                    group.Key.SourceId, group.Key.Kind, group.Key.TargetId);
                if (group.Any(edge => !string.Equals(edge.Id, expectedId, StringComparison.Ordinal)))
                    throw new InvalidOperationException(
                        $"Edge ID 必須由 source/kind/target 計算：{group.Key.SourceId} " +
                        $"{group.Key.Kind} {group.Key.TargetId}。");
                return new GraphEdge(
                    expectedId,
                    group.Key.SourceId,
                    group.Key.Kind,
                    group.Key.TargetId,
                    MergeEvidence(group.SelectMany(edge => edge.Evidence)));
            })
            .OrderBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 把已存在且有 evidence 的資料存取關係加入 Code node 的 BM25 文字。
    /// 這只建立檢索別名，不新增或推測 edge；可避免「更新／儲存」只命中名稱剛好含 Update
    /// 的 Controller，卻漏掉真正擁有 WRITES 的 DAL owner。
    /// </summary>
    private static IReadOnlyList<GraphNode> AddRelationshipSearchTerms(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges)
    {
        var outgoingKinds = edges
            .GroupBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.Kind).ToHashSet(),
                StringComparer.Ordinal);
        return nodes.Select(node =>
            {
                if (node.Kind != GraphNodeKind.Code ||
                    !outgoingKinds.TryGetValue(node.Id, out var kinds))
                    return node;
                var terms = new List<string>();
                if (kinds.Contains(GraphEdgeKind.Writes))
                    terms.Add("資料寫入 儲存 更新 新增 刪除 Write Save Update Insert Delete");
                if (kinds.Contains(GraphEdgeKind.Reads))
                    terms.Add("資料讀取 查詢 Read Search Query Select");
                if (terms.Count == 0) return node;
                var searchableText = string.Join(
                    ' ',
                    new[] { node.SearchableText }
                        .Concat(terms)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal));
                return node with { SearchableText = searchableText };
            })
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<GraphEvidence> MergeEvidence(IEnumerable<GraphEvidence> source)
    {
        return source
            .Select(NormalizeEvidence)
            .DistinctBy(EvidenceIdentity, StringComparer.Ordinal)
            .OrderBy(evidence => evidence.Confidence)
            .ThenBy(evidence => evidence.Source)
            .ThenBy(evidence => evidence.Artifact, StringComparer.Ordinal)
            .ThenBy(evidence => evidence.StartLine)
            .ThenBy(evidence => evidence.Reason, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> MergeAttributes(
        string nodeId,
        IReadOnlyList<GraphNode> observations)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in observations.SelectMany(node => node.Attributes)
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Value, StringComparer.Ordinal))
        {
            if (result.TryGetValue(pair.Key, out var existing) &&
                !string.Equals(existing, pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Node {nodeId} 的 attribute '{pair.Key}' 發生衝突：'{existing}' / '{pair.Value}'。");
            // Route、SQL object 與 CLR symbol 的穩定 ID 都採大小寫不敏感正規化；
            // 同一事實只差來源 casing 時保留 ordinal 排序後的第一個表示，確保 canonical digest
            // 不受 extractor 執行順序影響。非 case-only 差異仍立即失敗，避免掩蓋真正衝突。
            if (!result.ContainsKey(pair.Key))
                result[pair.Key] = pair.Value;
        }
        return result;
    }

    private static GraphArtifact NormalizeArtifact(GraphArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var path = artifact.Path.StartsWith("db:", StringComparison.Ordinal)
            ? artifact.Path.Trim()
            : GraphIdentity.NormalizePath(artifact.Path);
        return artifact with { Path = path };
    }

    private static GraphEvidence NormalizeEvidence(GraphEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var artifact = evidence.Artifact.StartsWith("db:", StringComparison.Ordinal)
            ? evidence.Artifact.Trim()
            : GraphIdentity.NormalizePath(evidence.Artifact);
        var details = evidence.Details?
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return evidence with
        {
            Artifact = artifact,
            Reason = evidence.Reason.Trim(),
            Details = details,
        };
    }

    private static void Validate(
        IReadOnlyList<GraphArtifact> artifacts,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        IReadOnlyList<GraphDiagnostic> diagnostics)
    {
        EnsureUnique(artifacts.Select(item => item.Id), "artifact");
        EnsureUnique(nodes.Select(item => item.Id), "node");
        EnsureUnique(edges.Select(item => item.Id), "edge");

        var nodeIds = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!GraphRoles.IsKnown(node.Role))
                throw new InvalidOperationException($"Node {node.Id} 使用未定義 role：{node.Role}。");
            if (node.Evidence.Count == 0)
                throw new InvalidOperationException($"Node {node.Id} 缺少 evidence。");
            ValidateSensitiveData(node.Id, node.Name, node.SearchableText);
            foreach (var pair in node.Attributes)
                ValidateSensitiveData($"{node.Id}.attributes.{pair.Key}", pair.Key, pair.Value);
            foreach (var evidence in node.Evidence)
                ValidateEvidence(node.Id, evidence);
        }

        foreach (var edge in edges)
        {
            if (!nodeIds.Contains(edge.SourceId) || !nodeIds.Contains(edge.TargetId))
                throw new InvalidOperationException(
                    $"Edge {edge.Id} 存在 dangling endpoint：{edge.SourceId} -> {edge.TargetId}。");
            if (edge.Evidence.Count == 0)
                throw new InvalidOperationException($"Edge {edge.Id} 缺少 evidence。");
            foreach (var evidence in edge.Evidence)
                ValidateEvidence(edge.Id, evidence);
        }

        foreach (var diagnostic in diagnostics)
        {
            if (string.IsNullOrWhiteSpace(diagnostic.Message) ||
                !diagnostic.Message.Any(character => character is >= '\u3400' and <= '\u9fff'))
                throw new InvalidOperationException(
                    $"Diagnostic {diagnostic.Code} 必須提供繁體中文訊息。");
            ValidateSensitiveData($"diagnostic:{diagnostic.Code}", diagnostic.Artifact, diagnostic.Message);
        }

        if (diagnostics.Any(item => item.Severity == GraphDiagnosticSeverity.Error))
            throw new InvalidOperationException("索引含有 Error diagnostic，禁止發布 canonical graph。");
    }

    private static void ValidateEvidence(string ownerId, GraphEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.Artifact))
            throw new InvalidOperationException($"{ownerId} 的 evidence 缺少 artifact。");
        if (string.IsNullOrWhiteSpace(evidence.Reason) ||
            !evidence.Reason.Any(character => character is >= '\u3400' and <= '\u9fff'))
            throw new InvalidOperationException($"{ownerId} 的 evidence 必須提供繁體中文 reason。");
        ValidateSensitiveData($"{ownerId}.evidence", evidence.Artifact, evidence.Reason);
        if (evidence.Details is null) return;
        foreach (var pair in evidence.Details)
            ValidateSensitiveData($"{ownerId}.evidence.{pair.Key}", pair.Key, pair.Value);
    }

    private static void ValidateSensitiveData(string owner, params string?[] values)
    {
        var text = string.Join('\n', values.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (PasswordPattern.IsMatch(text))
            throw new InvalidOperationException($"{owner} 疑似包含密碼或 connection string，禁止進入圖譜。");
        if (EmailPattern.IsMatch(text))
            throw new InvalidOperationException($"{owner} 疑似包含 Email，禁止進入圖譜。");
    }

    private static string SelectRole(IEnumerable<string> roles)
    {
        var candidates = roles.Distinct(StringComparer.Ordinal).ToList();
        if (candidates.Any(role => !GraphRoles.IsKnown(role)))
            throw new InvalidOperationException(
                $"Node role 必須使用 GraphRoles 常數：{string.Join(", ", candidates)}。");
        if (candidates.Count == 1) return candidates[0];

        // 一般 type/configuration 是 extractor 尚未辨識業務角色時的保底值；
        // 同一 node 後續若由框架或 DB extractor 找到更具體角色，應採用更具體者，
        // 但兩個同樣具體且不同的角色代表模型衝突，不能靜默猜測。
        var generic = new HashSet<string>([GraphRoles.Type, GraphRoles.Configuration], StringComparer.Ordinal);
        var specific = candidates.Where(role => !generic.Contains(role)).ToList();
        if (specific.Count == 1) return specific[0];
        throw new InvalidOperationException($"同一 node 發生 role 衝突：{string.Join(", ", candidates)}。");
    }

    private static string SelectText(IEnumerable<string> values)
    {
        var candidates = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();
        return candidates.Count == 0
            ? throw new InvalidOperationException("Canonical node 的必要文字不可為空。")
            : candidates[0];
    }

    /// <summary>
    /// 動態設定的 name 通常只是 GUID、FormatType 或使用者自訂標題，沒有 role 語意。
    /// 這個固定小字典只提升 BM25 可尋性，不改變 identity、kind、role 或圖關係，
    /// 也不把 method／column／設定 row 重新膨脹成節點。
    /// </summary>
    private static string RoleSearchText(string role) => role switch
    {
        GraphRoles.MenuFeature => "選單 功能 Menu",
        GraphRoles.ApprovalFeature => "覆核 放行 Confirm Approval",
        GraphRoles.CustomReport => "自訂報表 CustomReport",
        GraphRoles.Schedule => "排程 Schedule",
        GraphRoles.BatchReport => "批次報表 BatchReport",
        GraphRoles.FrontendPage => "前端頁面 frontend page",
        GraphRoles.WebRoute or GraphRoles.ControllerAction => "HTTP 入口 Route Action",
        GraphRoles.ScheduledTask => "排程任務 Scheduled Task",
        GraphRoles.MessageConsumer => "訊息消費 Message Consumer",
        GraphRoles.CliCommand => "命令列 CLI",
        GraphRoles.Controller => "Controller 控制器",
        GraphRoles.BusinessService => "BZ 商業邏輯 Service",
        GraphRoles.Repository => "資料存取 Repository",
        GraphRoles.DataModel => "資料模型 ORM Entity",
        GraphRoles.ReportPlugin => "報表外掛 Report Plugin",
        GraphRoles.Type => "程式型別 Code",
        GraphRoles.Module => "前端模組 JavaScript",
        GraphRoles.Migration => "資料庫 Migration",
        GraphRoles.Table => "資料表 SQL Table",
        GraphRoles.View => "資料檢視 SQL View",
        GraphRoles.Procedure => "預存程序 Stored Procedure",
        GraphRoles.ReportTemplate => "報表模板 Report Template",
        GraphRoles.ReportDataSource => "報表資料來源 Report DataSource",
        GraphRoles.ReportDataSourceGroup => "報表資料來源群組 DataSource Group",
        GraphRoles.CustomEnum => "自訂列舉 Custom Enum",
        GraphRoles.ProductType => "商品類型 ProductType",
        GraphRoles.CustomProductType => "客製商品類型 Custom ProductType",
        GraphRoles.CsvFormat => "CSV 格式 CsvFormat",
        GraphRoles.Configuration => "動態設定 Configuration",
        _ => string.Empty,
    };

    private static string? SelectNullableText(IEnumerable<string?> values)
    {
        var candidates = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();
        return candidates.Count == 0 ? null : candidates[0];
    }

    private static int EvidenceRank(IReadOnlyList<GraphEvidence> evidence) =>
        evidence.Count == 0 ? int.MaxValue : evidence.Min(item => (int)item.Confidence);

    private static string EvidenceIdentity(GraphEvidence evidence) =>
        JsonSerializer.Serialize(evidence, CanonicalJsonOptions);

    private static void EnsureUnique(IEnumerable<string> identities, string entityType)
    {
        var duplicate = identities.GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Canonical {entityType} identity 重複：{duplicate.Key}。");
    }

    private sealed record CanonicalPayload(
        string SchemaVersion,
        string ProjectId,
        GraphIndexerDescriptor Indexer,
        string WorkingTreeFingerprint,
        IReadOnlyList<GraphArtifact> Artifacts,
        IReadOnlyList<GraphNode> Nodes,
        IReadOnlyList<GraphEdge> Edges,
        IReadOnlyList<GraphDiagnostic> Diagnostics,
        IReadOnlyList<string> CapabilityGaps);

    [GeneratedRegex(
        @"(?i)(password|pwd)\s*[:=]|(data\s+source|server)\s*=.+;\s*(user\s+id|uid)\s*=",
        RegexOptions.CultureInvariant)]
    private static partial Regex PasswordRegex();

    [GeneratedRegex(
        @"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
