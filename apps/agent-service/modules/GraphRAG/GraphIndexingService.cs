using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Modules.GraphRAG.FblAuthority;
using AgentService.Modules.GraphRAG.ParallelExtractor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// ParallelExtractor GraphRAG 索引的技術上限。
/// Graph schema 固定對應移植抽取器；FBL 業務資料只在目標 SQL Server 具備相應表格時啟用。
/// </summary>
public sealed class GraphIndexingOptions
{
    /// <summary>設定 section 名稱。</summary>
    public const string SectionName = "GraphRAG";

    /// <summary>
    /// 是否將 CodeChunk 的完整程式碼保存到 Neo4j。
    /// false 對應 ParallelExtractor 的 --graph-lines；true 對應 --graph/full 模式。
    /// </summary>
    public bool IncludeCodeChunkText { get; set; }

    /// <summary>ParallelExtractor 每個 Project 的最大平行工作數。</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

}

/// <summary>右下角與專案頁顯示的一次索引進度。</summary>
public sealed record GraphIndexProgress(
    string ProjectId,
    string Phase,
    string Message,
    int Percent,
    string RunId,
    string Mode,
    long ElapsedMilliseconds);

/// <summary>保存最近一次索引執行的去敏感統計。</summary>
public sealed record GraphIndexRun(
    string ProjectId,
    string RunId,
    string Mode,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long ElapsedMilliseconds,
    int NodeCount,
    int EdgeCount,
    string? ManifestVersion,
    string? CanonicalDigest,
    IReadOnlyDictionary<string, long> StageDurationsMilliseconds,
    string? Error = null,
    long PeakWorkingSetBytes = 0);

/// <summary>同一專案已有索引工作時回傳給 REST 409 的明確例外。</summary>
public sealed class GraphIndexAlreadyRunningException(string projectId)
    : InvalidOperationException($"專案已有索引正在執行：{projectId}");

/// <summary>
/// ParallelExtractor GraphRAG 唯一索引協調器。
/// 流程固定為資料來源前置閘門、原始碼／資料庫抽取、Preflight、Neo4j 原子發布與背景 Community 摘要；
/// 不再執行舊的通用語言 Extractor、SQLite Evidence 雙寫或 Reconciliation 輪詢。
/// </summary>
public sealed class GraphIndexingService
{
    private const string IndexerVersion = ProjectGraphVersions.Indexer;
    private const long TransientIndexMemoryCollectionThresholdBytes = 1L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> SnapshotExtensions = new(
        [
            ".cs", ".csproj", ".sln",
            ".js", ".jsx", ".ts", ".tsx",
            ".aspx", ".ascx", ".master",
            ".sql", ".json", ".xml", ".config",
            ".md", ".txt", ".yaml", ".yml",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SnapshotExcludedDirectories = new(
        [
            ".git", ".svn", ".vs", "bin", "obj", "node_modules",
            "packages", "dist", "build", "out", "target", "vendor",
        ],
        StringComparer.OrdinalIgnoreCase);
    private readonly ParallelExtractorPipeline _parallelExtractor;
    private readonly IGraphStore _graphStore;
    private readonly GraphCommunityAiService _communityAi;
    private readonly INeo4jRuntime _neo4jRuntime;
    private readonly IProjectRepository _projects;
    private readonly IProjectIndexManifestStore _manifests;
    private readonly IGraphDatabaseSourceProvider _databaseSources;
    private readonly ProjectGraphDatabaseExtractor _databaseExtractor;
    private readonly GraphIndexingOptions _options;
    private readonly ILogger<GraphIndexingService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GraphIndexProgress> _progress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GraphIndexRun> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reservedRuns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GraphIndexRun>> _runsByMode =
        new(StringComparer.Ordinal);

    /// <summary>建立只依賴 ParallelExtractor 與單一 Neo4j store 的索引服務。</summary>
    public GraphIndexingService(
        ParallelExtractorPipeline parallelExtractor,
        IGraphStore graphStore,
        GraphCommunityAiService communityAi,
        INeo4jRuntime neo4jRuntime,
        IProjectRepository projects,
        IProjectIndexManifestStore manifests,
        IGraphDatabaseSourceProvider databaseSources,
        ProjectGraphDatabaseExtractor databaseExtractor,
        IOptions<GraphIndexingOptions> options,
        ILogger<GraphIndexingService> logger)
    {
        _parallelExtractor = parallelExtractor;
        _graphStore = graphStore;
        _communityAi = communityAi;
        _neo4jRuntime = neo4jRuntime;
        _projects = projects;
        _manifests = manifests;
        _databaseSources = databaseSources;
        _databaseExtractor = databaseExtractor;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>取得目前或最後索引進度。</summary>
    public GraphIndexProgress? GetProgress(string projectId) =>
        _progress.TryGetValue(projectId, out var value) ? value : null;

    /// <summary>取得最近一次執行；指定 mode 時讀取該模式最近結果。</summary>
    public GraphIndexRun? GetLastRun(string projectId, string? mode = null)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return _runs.TryGetValue(projectId, out var value) ? value : null;
        }
        return _runsByMode.TryGetValue(projectId, out var modes) &&
               modes.TryGetValue(mode.Trim(), out var result)
            ? result
            : null;
    }

    /// <summary>清除 process 內的進度與背景摘要狀態；不刪除持久化 Graph。</summary>
    public void ForgetProjectState(string projectId)
    {
        _progress.TryRemove(projectId, out _);
        _runs.TryRemove(projectId, out _);
        _runsByMode.TryRemove(projectId, out _);
        _communityAi.ForgetProject(projectId);
    }

    /// <summary>專案資料庫設定變更後標記既有圖需要由使用者重新按下索引。</summary>
    public async Task MarkConfigurationChangedAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetAsync(projectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        if (project.IndexStatus != ProjectIndexStatus.Indexing)
        {
            project.IndexStatus = ProjectIndexStatus.Stale;
        }
        project.IndexError = null;
        await _projects.SaveAsync(project, cancellationToken);
    }

    /// <summary>取得目前與最近一次完整索引 manifest。</summary>
    public async Task<ProjectIndexDiagnostics> GetDiagnosticsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetAsync(projectId, cancellationToken);
        var current = await _manifests.GetCurrentAsync(projectId, cancellationToken);
        var latest = await _manifests.GetLatestAttemptAsync(projectId, cancellationToken);
        return new ProjectIndexDiagnostics(
            current,
            latest,
            project?.IndexStatus is ProjectIndexStatus.Stale or ProjectIndexStatus.Failed);
    }

    /// <summary>執行完整 ParallelExtractor 索引；成功前不會改變既有 active Graph。</summary>
    public async Task<ProjectEntity> IndexProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (!TryReserveIndex(projectId))
            throw new GraphIndexAlreadyRunningException(projectId);
        return await RunReservedIndexAsync(projectId, cancellationToken);
    }

    /// <summary>在進入背景佇列前原子保留專案，讓重複 HTTP 要求立即得到 409。</summary>
    public bool TryReserveIndex(string projectId) =>
        _reservedRuns.TryAdd(projectId, new CancellationTokenSource());

    /// <summary>背景工作尚未成功排入時釋放預留，避免專案永久回傳 409。</summary>
    public void ReleaseReservation(string projectId)
    {
        if (!_reservedRuns.TryRemove(projectId, out var reservation)) return;
        reservation.Cancel();
        reservation.Dispose();
    }

    /// <summary>執行已由 REST 層保留的索引工作。</summary>
    public async Task<ProjectEntity> RunReservedIndexAsync(
        string projectId,
        CancellationToken hostCancellationToken = default)
    {
        if (!_reservedRuns.TryGetValue(projectId, out var userCancellation))
            throw new InvalidOperationException("索引工作尚未保留。 ");
        var gate = _gates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, hostCancellationToken))
            throw new GraphIndexAlreadyRunningException(projectId);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            hostCancellationToken,
            userCancellation.Token);
        try
        {
            return await IndexCoreAsync(projectId, runCancellation.Token);
        }
        finally
        {
            if (_reservedRuns.TryRemove(projectId, out var reservation))
                reservation.Dispose();
            gate.Release();
            ReleaseTransientIndexMemory();
        }
    }

    /// <summary>要求取消指定專案目前正在執行的完整索引。</summary>
    public bool CancelIndex(string projectId) =>
        _reservedRuns.TryGetValue(projectId, out var cancellation) &&
        TryCancel(cancellation);

    /// <summary>
    /// 取消索引並等待執行中的候選版本完成補償。刪除專案必須先走此方法，
    /// 否則索引工作可能在專案資料刪除後又把候選圖發布回 Neo4j。
    /// </summary>
    public async Task CancelAndWaitAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        CancelIndex(projectId);
        while (_reservedRuns.ContainsKey(projectId))
            await Task.Delay(50, cancellationToken);
    }

    private static bool TryCancel(CancellationTokenSource cancellation)
    {
        if (cancellation.IsCancellationRequested) return false;
        cancellation.Cancel();
        return true;
    }

    /// <summary>
    /// 冷索引會暫時建立大量 Roslyn 語法樹；索引方法結束後這些物件已不再使用，
    /// 但桌面常駐服務可能很久都不觸發完整壓縮。只有工作集超過 1 GiB 時才主動回收，
    /// 避免一般問答與 no-op 索引承受不必要的 full GC。
    /// </summary>
    private void ReleaseTransientIndexMemory()
    {
        var before = Environment.WorkingSet;
        if (before < TransientIndexMemoryCollectionThresholdBytes)
        {
            return;
        }

        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Aggressive,
            blocking: true,
            compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Aggressive,
            blocking: true,
            compacting: true);
        _logger.LogInformation(
            "索引暫存記憶體已回收。BeforeMiB={BeforeMiB}, AfterMiB={AfterMiB}",
            before / (1024 * 1024),
            Environment.WorkingSet / (1024 * 1024));
}
    /// <summary>索引主流程，所有例外都保存去敏感診斷並保留上一版 active graph。</summary>
    private async Task<ProjectEntity> IndexCoreAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        var stageDurations = new Dictionary<string, long>(StringComparer.Ordinal);
        var indexMode = _options.IncludeCodeChunkText ? "full" : "graph-lines";
        // 使用者可能在工作仍排隊時就按下取消。先以不受該工作 token 影響的短查詢
        // 取得專案，才能在下方 catch 將狀態可靠寫成 Canceled；不會因此開始任何抽取。
        var project = await RequiredProjectAsync(projectId, CancellationToken.None);
        var previousVersion = project.IndexManifestVersion;
        var previousNodeCount = project.NodeCount;
        var previousEdgeCount = project.EdgeCount;
        var publishedVersion = (string?)null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetProgress(projectId, "preflight", "正在檢查資料庫設定與唯讀連線…", 2, runId, indexMode, stopwatch);
            project.IndexStatus = ProjectIndexStatus.Indexing;
            project.IndexError = null;
            await _projects.SaveAsync(project, cancellationToken);

            // 連線測試必須先於任何原始碼掃描；先完成所有來源測試，再一次回報失敗清單。
            var databaseSources = await _databaseSources.GetAllAsync(project, cancellationToken);
            var connectionFailures = new List<string>();
            foreach (var source in databaseSources)
            {
                SetProgress(
                    projectId,
                    "preflight",
                    $"正在測試 {source.Provider}／{source.DatabaseName} 唯讀連線…",
                    3,
                    runId,
                    indexMode,
                    stopwatch);
                try
                {
                    await _databaseExtractor.TestConnectionAsync(source, cancellationToken);
                    SetProgress(
                        projectId,
                        "preflight",
                        $"{source.Provider}／{source.DatabaseName} 唯讀連線測試通過。",
                        4,
                        runId,
                        indexMode,
                        stopwatch);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // 連線測試只記錄安全識別與型別；密碼不會進入進度、Log 或例外摘要。
                    connectionFailures.Add(
                        $"{source.Provider} ({source.DatabaseName})：{exception.Message}");
                }
            }

            if (connectionFailures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"資料庫唯讀連線測試未全部通過；索引尚未開始。{string.Join("；", connectionFailures)}");
            }

            if (databaseSources.Count == 0)
            {
                SetProgress(
                    projectId,
                    "preflight",
                    "未設定外部資料庫，將執行僅原始碼索引。",
                    4,
                    runId,
                    indexMode,
                    stopwatch);
            }

            var databaseFingerprint = ComputeDatabaseFingerprint(databaseSources);
            var fingerprint = Sha256(string.Join(
                "|",
                IndexerVersion,
                databaseFingerprint,
                _options.IncludeCodeChunkText ? "full" : "graph-lines"));

            var version = Guid.NewGuid().ToString("N");
            var attempt = CreateManifest(
                project,
                version,
                databaseFingerprint,
                startedAt,
                indexMode);
            if (!await _neo4jRuntime.EnsureAvailableAsync(null, cancellationToken))
            {
                throw new InvalidOperationException(
                    _neo4jRuntime.LastError ?? "Neo4j 圖譜資料庫目前無法使用。");
            }

            if (!project.GraphStorageMigrated)
            {
                SetProgress(projectId, "migration", "正在清除舊版圖譜格式與 manifest…", 8, runId, indexMode, stopwatch);
                await _graphStore.DeleteProjectAsync(projectId, cancellationToken);
                await _manifests.DeleteProjectAsync(projectId, cancellationToken);
                project.GraphStorageMigrated = true;
                project.IndexManifestVersion = null;
                project.NodeCount = 0;
                project.EdgeCount = 0;
                previousVersion = null;
                previousNodeCount = 0;
                previousEdgeCount = 0;
                await _projects.SaveAsync(project, cancellationToken);
            }

            SetProgress(projectId, "extract", "正在解析原始碼與已設定資料庫物件…", 20, runId, indexMode, stopwatch);
            var extractWatch = Stopwatch.StartNew();
            var resultDocument = await BuildGraphDocumentAsync(
                project,
                databaseSources,
                cancellationToken);
            extractWatch.Stop();
            stageDurations["extract"] = extractWatch.ElapsedMilliseconds;

            // Roslyn Workspace、Compilation 與前端 AST 在此時已經無需使用；
            // 發布百萬級 Neo4j 資料前先回收，避免兩個階段的峰值記憶體重疊。
            ReleaseTransientIndexMemory();

            SetProgress(projectId, "community", "正在執行確定性 Community 分群…", 65, runId, indexMode, stopwatch);
            var communityWatch = Stopwatch.StartNew();
            var communities = FblAuthorityCommunityBuilder.Build(resultDocument, cancellationToken);
            communityWatch.Stop();
            stageDurations["community"] = communityWatch.ElapsedMilliseconds;

            SetProgress(projectId, "manifest", "正在建立原始碼檔案版本清單…", 72, runId, indexMode, stopwatch);
            var digest = ComputeDocumentDigest(resultDocument, fingerprint);
            var fileSnapshot = await BuildFileSnapshotAsync(
                project.RootPath,
                cancellationToken);

            SetProgress(projectId, "publish", "結構驗證完成，正在原子發布 Neo4j 圖譜…", 80, runId, indexMode, stopwatch);
            var publishWatch = Stopwatch.StartNew();
            await _graphStore.EnsureSchemaAsync(cancellationToken);
            // 先記錄候選版本；Publish 內部若在 Promote 後發生例外，catch 仍能執行補償。
            publishedVersion = version;
            await _graphStore.PublishAsync(
                new FblGraphSnapshot(projectId, version, digest, resultDocument, communities),
                cancellationToken);
            publishWatch.Stop();
            stageDurations["publish"] = publishWatch.ElapsedMilliseconds;

            var completedAt = DateTimeOffset.UtcNow;
            var promoted = attempt with
            {
                CompletedAt = completedAt,
                Status = IndexManifestStatus.Fresh,
                NodeCount = resultDocument.Nodes.Count,
                EdgeCount = resultDocument.Relationships.Count,
                GraphSchemaVersion = ProjectGraphVersions.CanonicalSchema,
                IndexMode = indexMode,
                RequiresRetry = false,
            };
            await _manifests.PromoteAsync(promoted, cancellationToken);
            await _manifests.SaveFileSnapshotAsync(
                projectId,
                version,
                fileSnapshot,
                cancellationToken);

            project.Languages = "csharp,javascript,typescript,aspx,sql";
            project.IndexStatus = ProjectIndexStatus.Indexed;
            project.IndexedAt = completedAt;
            project.IndexError = null;
            project.NodeCount = resultDocument.Nodes.Count;
            project.EdgeCount = resultDocument.Relationships.Count;
            project.IndexManifestVersion = version;
            await _projects.SaveAsync(project, cancellationToken);

            // 到這裡 Neo4j active、manifest 與 Project 已經一致。舊版清理屬於
            // best-effort maintenance；失敗不得回滾已完成的發布，下次成功發布會再清理。
            SetProgress(projectId, "cleanup", "新版本已啟用，正在清理超過保留上限的舊圖譜…", 95, runId, indexMode, stopwatch);
            try
            {
                await _manifests.PruneSuccessfulAsync(
                    projectId,
                    previousVersion,
                    CancellationToken.None);
                await _graphStore.FinalizePublishedVersionAsync(projectId, CancellationToken.None);
            }

            catch (Exception exception)
            {
                _logger.LogWarning(
                    "GraphRAG 已發布，但舊版清理失敗；不影響 active graph。Project={ProjectId}, ExceptionType={ExceptionType}",
                    projectId,
                    exception.GetType().Name);
            }

            // 只排入三個 C0；本方法不等待模型，失敗也不得回滾已發布結構。
            try
            {
                await _communityAi.PrewarmC0Async(projectId, version, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Community 背景預熱排程失敗，結構圖仍可使用。Project={ProjectId}, ExceptionType={ExceptionType}",
                    projectId,
                    exception.GetType().Name);
            }

            var completedRun = CompleteRun(
                project, runId, indexMode, startedAt, stopwatch, version, digest, stageDurations);
            RecordRun(completedRun);
            SetProgress(projectId, "complete", "ParallelExtractor 圖譜索引完成；Community AI 摘要將在背景補齊。", 100, runId, indexMode, stopwatch);
            return project;
        }
        catch (OperationCanceledException)
        {
            if (publishedVersion is not null)
            {
                await _manifests.RestoreCurrentAsync(
                    projectId,
                    previousVersion,
                    CancellationToken.None);
                await _graphStore.RollbackPublishedVersionAsync(
                    projectId,
                    publishedVersion,
                    previousVersion,
                    CancellationToken.None);
                await _manifests.DeleteVersionAsync(
                    projectId,
                    publishedVersion,
                    CancellationToken.None);
            }
            project.IndexStatus = ProjectIndexStatus.Canceled;
            project.IndexError = "索引已由使用者取消。";
            project.IndexManifestVersion = previousVersion;
            project.NodeCount = previousNodeCount;
            project.EdgeCount = previousEdgeCount;
            await _projects.SaveAsync(project, CancellationToken.None);
            stopwatch.Stop();
            RecordRun(new GraphIndexRun(
                projectId,
                runId,
                indexMode,
                "canceled",
                startedAt,
                DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds,
                previousNodeCount,
                previousEdgeCount,
                previousVersion,
                null,
                stageDurations,
                "索引已取消。",
                Environment.WorkingSet));
            SetProgress(projectId, "canceled", "索引已取消；既有版本未變更。", 100, runId, indexMode, stopwatch);
            throw;
        }
        catch (Exception exception)
        {
            if (publishedVersion is not null)
            {
                try
                {
                    // Publish 已切換 Neo4j 後，若本機 manifest／Project 寫入失敗，補償回上一版。
                    await _manifests.RestoreCurrentAsync(
                        projectId,
                        previousVersion,
                        CancellationToken.None);
                    await _graphStore.RollbackPublishedVersionAsync(
                        projectId,
                        publishedVersion,
                        previousVersion,
                        CancellationToken.None);
                    await _manifests.DeleteVersionAsync(
                        projectId,
                        publishedVersion,
                        CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(
                        rollbackException,
                        "GraphRAG 發布補償失敗；需人工檢查 active graph。Project={ProjectId}",
                        projectId);
                }
            }
            project.IndexStatus = ProjectIndexStatus.Failed;
            project.IndexError = SafeError(exception);
            project.IndexManifestVersion = previousVersion;
            project.NodeCount = previousNodeCount;
            project.EdgeCount = previousEdgeCount;
            await _projects.SaveAsync(project, CancellationToken.None);
            stopwatch.Stop();
            var failedRun = new GraphIndexRun(
                projectId,
                runId,
                indexMode,
                "failed",
                startedAt,
                DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds,
                previousNodeCount,
                previousEdgeCount,
                previousVersion,
                null,
                stageDurations,
                SafeError(exception),
                PeakWorkingSetBytes: Environment.WorkingSet);
            RecordRun(failedRun);
            SetProgress(projectId, "failed", SafeError(exception), 100, runId, indexMode, stopwatch);
            throw;
        }
    }

    /// <summary>
    /// 每次索引都完整執行 ParallelExtractor 的後端、前端與 SQL 階段。
    /// SQL Server 連線來自專案設定；SQLite catalog 仍以 Modern Wingman 既有格式附加保存。
    /// </summary>
    private async Task<GraphDocument> BuildGraphDocumentAsync(
        ProjectEntity project,
        IReadOnlyList<GraphDatabaseSource> sources,
        CancellationToken cancellationToken)
    {
        var sqlSource = sources.FirstOrDefault(source =>
            source.Provider == ProjectDatabaseProvider.SqlServer);
        var extraction = await _parallelExtractor.ExtractAsync(
            project.RootPath,
            project.SelectedSolutionPath,
            sqlSource?.ConnectionString,
            _options.IncludeCodeChunkText,
            Math.Max(1, _options.MaxDegreeOfParallelism),
            cancellationToken);
        var document = extraction.Document;

        foreach (var source in sources.Where(source =>
                     source.Provider == ProjectDatabaseProvider.Sqlite))
        {
            var objects = await _databaseExtractor.LoadSqliteDatabaseObjectsAsync(
                source,
                cancellationToken);
            document = AddSqliteDatabaseObjects(document, source, objects);
        }
        return document;
    }

    /// <summary>將 SQLite catalog 節點附加到既有 ParallelExtractor graph，不重新解析原始碼。</summary>
    private static GraphDocument AddSqliteDatabaseObjects(
        GraphDocument document,
        GraphDatabaseSource source,
        ProjectGraphDatabaseExtractor.SqliteDatabaseCatalog catalog)
    {
        var metadata = document.Metadata with
        {
            Provider = document.Metadata.Provider.Equals("SourceOnly", StringComparison.Ordinal)
                ? "Sqlite"
                : "Mixed",
            DatabaseName = string.Join(
                ";",
                new[] { document.Metadata.DatabaseName, source.DatabaseName }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
            DatabaseSnapshotIdentity = string.Join(
                ";",
                new[] { document.Metadata.DatabaseSnapshotIdentity, source.ConfigurationFingerprint }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)),
        };
        var builder = new GraphDocumentBuilder(metadata);
        foreach (var node in document.Nodes)
        {
            builder.AddNode(node.Kind, node.Key, node.Properties);
        }
        foreach (var relationship in document.Relationships)
        {
            builder.AddRelationship(
                relationship.Kind,
                relationship.SourceKey,
                relationship.TargetKey,
                relationship.Properties);
        }
        var databaseId = StableDatabaseId("database", "Sqlite", source.DatabaseName);
        builder.AddNode(
            GraphNodeKind.Database,
            databaseId,
            new Dictionary<string, object?>
            {
                ["name"] = source.DatabaseName,
                ["rowDataImported"] = false,
                ["metadataOnly"] = true,
            });
        var objectIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var databaseObject in catalog.Objects)
        {
            var objectId = StableDatabaseId(
                "db-object",
                "Sqlite",
                source.DatabaseName,
                "main",
                databaseObject.ObjectType,
                databaseObject.Name);
            objectIds[databaseObject.Name] = objectId;
            builder.AddNode(
                GraphNodeKind.DatabaseObject,
                objectId,
                new Dictionary<string, object?>
                {
                    ["databaseName"] = source.DatabaseName,
                    ["schemaName"] = "main",
                    ["name"] = databaseObject.Name,
                    ["objectType"] = databaseObject.ObjectType,
                    ["hasDefinition"] = !string.IsNullOrWhiteSpace(databaseObject.Definition),
                    ["definitionHash"] = string.IsNullOrWhiteSpace(databaseObject.Definition)
                        ? null
                        : StableDatabaseId("definition-hash", databaseObject.Definition),
                    ["definitionLength"] = databaseObject.Definition?.Length ?? 0,
                    ["metadataOnly"] = true,
                });
            builder.AddRelationship(
                GraphRelationshipKind.ContainsObject,
                databaseId,
                objectId,
                new Dictionary<string, object?> { ["objectType"] = databaseObject.ObjectType });
        }
        foreach (var column in catalog.Columns)
        {
            if (!objectIds.TryGetValue(column.ObjectName, out var objectId)) continue;
            var columnId = StableDatabaseId(
                "db-column",
                "Sqlite",
                source.DatabaseName,
                "main",
                column.ObjectName,
                column.Name);
            builder.AddNode(
                GraphNodeKind.DatabaseColumn,
                columnId,
                new Dictionary<string, object?>
                {
                    ["databaseName"] = source.DatabaseName,
                    ["schemaName"] = "main",
                    ["objectName"] = column.ObjectName,
                    ["name"] = column.Name,
                    ["ordinal"] = column.Ordinal,
                    ["dataType"] = column.DataType,
                    ["isNullable"] = column.IsNullable,
                    ["isPrimaryKey"] = column.IsPrimaryKey,
                });
            builder.AddRelationship(
                GraphRelationshipKind.HasColumn,
                objectId,
                columnId,
                new Dictionary<string, object?> { ["ordinal"] = column.Ordinal });
        }
        foreach (var foreignKey in catalog.ForeignKeys)
        {
            if (!objectIds.TryGetValue(foreignKey.SourceTable, out var sourceId) ||
                !objectIds.TryGetValue(foreignKey.TargetTable, out var targetId)) continue;
            builder.AddRelationship(
                GraphRelationshipKind.ForeignKeyTo,
                sourceId,
                targetId,
                new Dictionary<string, object?>
                {
                    ["sourceColumn"] = foreignKey.SourceColumn,
                    ["targetColumn"] = foreignKey.TargetColumn,
                    ["ordinal"] = foreignKey.Ordinal,
                });
        }
        foreach (var view in catalog.Objects.Where(item =>
                     item.ObjectType == "View" && !string.IsNullOrWhiteSpace(item.Definition)))
        {
            var sourceId = objectIds[view.Name];
            foreach (var tableName in ExtractSqliteViewReferences(view.Definition!))
            {
                if (!objectIds.TryGetValue(tableName, out var targetId) || sourceId == targetId) continue;
                builder.AddRelationship(
                    GraphRelationshipKind.Reads,
                    sourceId,
                    targetId,
                    new Dictionary<string, object?>
                    {
                        ["evidence"] = "sqlite_schema_definition_token_match",
                        ["access"] = "READ",
                        ["confidence"] = "EXACT_CATALOG_MATCH",
                    });
            }
        }
        return builder.Build();
    }

    private static IEnumerable<string> ExtractSqliteViewReferences(string definition) =>
        Regex.Matches(
                definition,
                "\\b(?:FROM|JOIN)\\s+(?:\\[|`|\")?(?<name>[A-Za-z_][A-Za-z0-9_]*)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string StableDatabaseId(string prefix, params object?[] values)
    {
        var canonical = string.Join('\u001f', values.Select(value => value?.ToString() ?? string.Empty));
        return $"{prefix}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32]}";
    }

    /// <summary>以已設定來源的 Provider、DatabaseName 與 fingerprint 產生確定性的彙總指紋。</summary>
    private static string ComputeDatabaseFingerprint(
        IReadOnlyList<GraphDatabaseSource> sources)
    {
        var material = string.Join(
            "|",
            sources
                .OrderBy(source => source.Provider)
                .ThenBy(source => source.DatabaseName, StringComparer.OrdinalIgnoreCase)
                .Select(source => string.Join(
                    ":",
                    source.Provider,
                    source.DatabaseName.Trim().ToUpperInvariant(),
                    source.ConfigurationFingerprint)));
        return Sha256(material);
    }

    /// <summary>建立成功或進入中的 immutable manifest。</summary>
    private static ProjectIndexManifest CreateManifest(
        ProjectEntity project,
        string version,
        string databaseFingerprint,
        DateTimeOffset startedAt,
        string indexMode) =>
        new(
            project.Id,
            version,
            project.RootPath,
            IndexerVersion,
            startedAt,
            null,
            IndexManifestStatus.Fresh,
            GraphSchemaVersion: ProjectGraphVersions.CanonicalSchema,
            AnalysisSnapshotHash: databaseFingerprint,
            IndexMode: indexMode,
            RequiresRetry: false);

    /// <summary>圖內容摘要只使用穩定 key、enum 關係與輸入指紋，不包含時間或密碼。</summary>
    private static string ComputeDocumentDigest(GraphDocument document, string inputFingerprint)
    {
        var material = new StringBuilder(inputFingerprint).Append('\n');
        foreach (var node in document.Nodes.OrderBy(node => node.Key, StringComparer.Ordinal))
        {
            material.Append(node.Kind).Append('|').Append(node.Key).Append('\n');
        }
        foreach (var relationship in document.Relationships.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            material.Append(relationship.SourceKey).Append('|')
                .Append(GraphSchema.GetRelationshipType(relationship.Kind)).Append('|')
                .Append(relationship.TargetKey).Append('\n');
        }
        return Sha256(material.ToString());
    }

    /// <summary>將成功執行轉成固定統計。</summary>
    private static GraphIndexRun CompleteRun(
        ProjectEntity project,
        string runId,
        string mode,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        string version,
        string digest,
        IReadOnlyDictionary<string, long> stageDurations)
    {
        stopwatch.Stop();
        return new GraphIndexRun(
            project.Id,
            runId,
            mode,
            "succeeded",
            startedAt,
            DateTimeOffset.UtcNow,
            stopwatch.ElapsedMilliseconds,
            project.NodeCount,
            project.EdgeCount,
            version,
            digest,
            stageDurations,
            PeakWorkingSetBytes: Environment.WorkingSet);
    }

    /// <summary>保存全域與 per-mode 最近結果。</summary>
    private void RecordRun(GraphIndexRun run)
    {
        _runs[run.ProjectId] = run;
        _runsByMode.GetOrAdd(
            run.ProjectId,
            _ => new ConcurrentDictionary<string, GraphIndexRun>(StringComparer.Ordinal))[run.Mode] = run;
    }

    /// <summary>更新前端可讀的進度物件。</summary>
    private void SetProgress(
        string projectId,
        string phase,
        string message,
        int percent,
        string runId,
        string mode,
        Stopwatch stopwatch) =>
        _progress[projectId] = new GraphIndexProgress(
            projectId,
            phase,
            message,
            Math.Clamp(percent, 0, 100),
            runId,
            mode,
            stopwatch.ElapsedMilliseconds);

    /// <summary>取得必須存在且根目錄有效的專案。</summary>
    private async Task<ProjectEntity> RequiredProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await _projects.GetAsync(projectId, cancellationToken) ??
                      throw new KeyNotFoundException($"找不到專案：{projectId}");
        if (!Directory.Exists(project.RootPath))
        {
            throw new DirectoryNotFoundException($"專案根目錄不存在：{project.RootPath}");
        }
        return project;
    }

    /// <summary>計算小寫十六進位 SHA-256。</summary>
    private static string Sha256(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    /// <summary>限制持久化錯誤長度並避免連線字串意外寫入。</summary>
    private static string SafeError(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Pwd=", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("User ID=", StringComparison.OrdinalIgnoreCase))
        {
            return $"索引失敗（{exception.GetType().Name}）；詳細訊息可能包含機敏設定，已隱藏。";
        }
        return message.Length <= 2_000 ? message : message[..2_000];
    }

    private static async Task<IReadOnlyList<ProjectIndexedFile>> BuildFileSnapshotAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var paths = EnumerateSnapshotFiles(rootPath).ToArray();
        var files = new ConcurrentBag<ProjectIndexedFile>();
        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            async (path, token) =>
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, token));
                files.Add(new ProjectIndexedFile(
                    Path.GetRelativePath(rootPath, path).Replace('\\', '/'),
                    hash));
            });
        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 只列出專案原始碼工具能開啟的檔案；在走訪時就略過建置輸出與套件目錄，
    /// 不把安裝包、二進位檔或資料庫檔誤當成原始碼版本清單的一部分。
    /// </summary>
    private static IEnumerable<string> EnumerateSnapshotFiles(string rootPath)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> childDirectories;
            IEnumerable<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory).ToArray();
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in childDirectories.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(child);
                if (!SnapshotExcludedDirectories.Contains(info.Name) &&
                    !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push(child);
            }
            foreach (var file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (SnapshotExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
            }
        }
    }
}
