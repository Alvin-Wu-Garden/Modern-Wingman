using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Modules.GraphRAG.FblAuthority;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// FBL GraphRAG 索引的技術上限。
/// 此設定不允許切換語言或 Graph schema；Modern Wingman 的專案解析固定只支援 FBL 投資系統。
/// </summary>
public sealed class GraphIndexingOptions
{
    /// <summary>設定 section 名稱。</summary>
    public const string SectionName = "GraphRAG";

    /// <summary>來源、資料庫設定與索引版本相同時允許略過重建。</summary>
    public bool EnableNoOpFastPath { get; set; } = true;

    /// <summary>除錯時強制重新抽取全部 FBL 圖。</summary>
    public bool ForceFullIndex { get; set; }

    /// <summary>檔案異動合併等待時間。</summary>
    public int WatcherDebounceMilliseconds { get; set; } = 800;

    /// <summary>單一 FBL working copy 最多掃描的相關檔案數。</summary>
    public int MaximumFiles { get; set; } = 250_000;
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

/// <summary>
/// FBL GraphRAG 唯一索引協調器。
/// 流程固定為資料來源前置閘門、原始碼／資料庫抽取、Preflight、Neo4j 原子發布與背景 Community 摘要；
/// 不再執行舊的通用語言 Extractor、SQLite Evidence 雙寫或 Reconciliation 輪詢。
/// </summary>
public sealed class GraphIndexingService
{
    private const string IndexerVersion = ProjectGraphVersions.Indexer;
    private const long TransientIndexMemoryCollectionThresholdBytes = 1L * 1024 * 1024 * 1024;
    private static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(
        [
            ".cs", ".csproj", ".sln",
            ".js", ".jsx", ".ts", ".tsx",
            ".aspx", ".ascx", ".master",
            ".sql", ".xml", ".config",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> ExcludedDirectories = new HashSet<string>(
        [".git", ".svn", ".vs", "bin", "obj", "node_modules", "packages", "dist", "build", "out", "vendor"],
        StringComparer.OrdinalIgnoreCase);

    private readonly FblAuthorityGraphBuilder _builder;
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
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GraphIndexRun>> _runsByMode =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _pendingFiles =
        new(StringComparer.Ordinal);

    /// <summary>建立只依賴 FBL 權威抽取器與單一 Neo4j store 的索引服務。</summary>
    public GraphIndexingService(
        FblAuthorityGraphBuilder builder,
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
        _builder = builder;
        _graphStore = graphStore;
        _communityAi = communityAi;
        _neo4jRuntime = neo4jRuntime;
        _projects = projects;
        _manifests = manifests;
        _databaseSources = databaseSources;
        _databaseExtractor = databaseExtractor;
        _options = options.Value;
        _logger = logger;
        ValidateOptions(_options);
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

    /// <summary>清除 process 內的 watcher、進度與背景摘要狀態；不刪除持久化 Graph。</summary>
    public void ForgetProjectState(string projectId)
    {
        _pendingFiles.TryRemove(projectId, out _);
        _progress.TryRemove(projectId, out _);
        _runs.TryRemove(projectId, out _);
        _runsByMode.TryRemove(projectId, out _);
        _communityAi.ForgetProject(projectId);
    }

    /// <summary>由 watcher 或資料庫設定端點標記專案需要重新索引。</summary>
    public async Task MarkPendingChangesAsync(
        string projectId,
        string? fullPath = null,
        CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetAsync(projectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(fullPath) && IsInsideRoot(project.RootPath, fullPath))
        {
            var relativePath = NormalizePath(Path.GetRelativePath(project.RootPath, fullPath));
            _pendingFiles.GetOrAdd(
                projectId,
                _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))[relativePath] = 0;
        }

        if (project.IndexStatus != ProjectIndexStatus.Indexing)
        {
            project.IndexStatus = ProjectIndexStatus.PendingChanges;
        }
        project.PendingFileCount = PendingFiles(projectId).Count;
        project.IndexError = null;
        await _projects.SaveAsync(project, cancellationToken);
    }

    /// <summary>取得 manifest 與尚未處理的檔案清單。</summary>
    public async Task<ProjectIndexDiagnostics> GetDiagnosticsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetAsync(projectId, cancellationToken);
        var current = await _manifests.GetCurrentAsync(projectId, cancellationToken);
        var latest = await _manifests.GetLatestAttemptAsync(projectId, cancellationToken);
        var pending = PendingFiles(projectId);
        return new ProjectIndexDiagnostics(
            current,
            latest,
            pending,
            pending.Count > 0 ||
            project?.IndexStatus is ProjectIndexStatus.PendingChanges or ProjectIndexStatus.Stale or ProjectIndexStatus.Failed ||
            latest is { Status: IndexManifestStatus.Failed or IndexManifestStatus.Stale });
    }

    /// <summary>
    /// 以內容雜湊補捉 watcher 遺漏的變更；發現差異只標記 pending，不在此方法重建 Graph。
    /// </summary>
    public async Task<bool> CatchUpAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await RequiredProjectAsync(projectId, cancellationToken);
        var current = await _manifests.GetCurrentAsync(projectId, cancellationToken);
        if (current is null)
        {
            return false;
        }

        var files = EnumerateFiles(project.RootPath);
        var live = await HashFilesAsync(project.RootPath, files, cancellationToken);
        var currentHashes = current.Files.ToDictionary(
            file => file.RelativePath,
            file => file.ContentHash,
            StringComparer.OrdinalIgnoreCase);
        var changed = live.Count != currentHashes.Count || live.Any(file =>
            !currentHashes.TryGetValue(file.RelativePath, out var hash) ||
            !string.Equals(hash, file.ContentHash, StringComparison.Ordinal));
        if (changed)
        {
            await MarkPendingChangesAsync(projectId, cancellationToken: cancellationToken);
        }
        return changed;
    }

    /// <summary>執行完整 FBL 權威索引；成功前不會改變既有 active Graph。</summary>
    public async Task<ProjectEntity> IndexProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await IndexCoreAsync(projectId, cancellationToken);
        }
        finally
        {
            gate.Release();
            ReleaseTransientIndexMemory();
        }
    }

    /// <summary>
    /// FBL 冷索引會暫時建立大量 Roslyn 語法樹；索引方法結束後這些物件已不再使用，
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
            "FBL 索引暫存記憶體已回收。BeforeMiB={BeforeMiB}, AfterMiB={AfterMiB}",
            before / (1024 * 1024),
            Environment.WorkingSet / (1024 * 1024));
    }

    /// <summary>有 pending 或內容差異時執行完整 authority rebuild；沒有變更時回傳 null。</summary>
    public async Task<ProjectEntity?> IncrementalIndexAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetAsync(projectId, cancellationToken);
        if (project is null)
        {
            return null;
        }
        var mustIndex = project.IndexStatus is
            ProjectIndexStatus.NotIndexed or
            ProjectIndexStatus.PendingChanges or
            ProjectIndexStatus.Stale or
            ProjectIndexStatus.Failed ||
            await CatchUpAsync(projectId, cancellationToken);
        return mustIndex ? await IndexProjectAsync(projectId, cancellationToken) : null;
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
        var project = await RequiredProjectAsync(projectId, cancellationToken);
        var previousVersion = project.IndexManifestVersion;
        var previousNodeCount = project.NodeCount;
        var previousEdgeCount = project.EdgeCount;
        var publishedVersion = (string?)null;

        try
        {
            SetProgress(projectId, "preflight", "正在檢查資料庫設定與唯讀連線…", 2, runId, "full", stopwatch);
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
                    "full",
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
                        "full",
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
                    "full",
                    stopwatch);
            }

            SetProgress(projectId, "scan", "正在計算原始碼指紋…", 5, runId, "full", stopwatch);
            var scanWatch = Stopwatch.StartNew();
            var files = EnumerateFiles(project.RootPath);
            var fileManifests = await HashFilesAsync(project.RootPath, files, cancellationToken);
            var databaseFingerprint = ComputeDatabaseFingerprint(databaseSources);
            var fingerprint = ComputeInputFingerprint(fileManifests, databaseFingerprint);
            scanWatch.Stop();
            stageDurations["scan"] = scanWatch.ElapsedMilliseconds;

            var current = await _manifests.GetCurrentAsync(projectId, cancellationToken);
            var activeVersion = await SafeActiveVersionAsync(projectId, cancellationToken);
            var noOp = !_options.ForceFullIndex && _options.EnableNoOpFastPath &&
                       project.IndexStatus == ProjectIndexStatus.Indexing &&
                       PendingFiles(projectId).Count == 0 &&
                       current is not null &&
                       current.IndexerVersion == IndexerVersion &&
                       current.WorkingTreeFingerprint == fingerprint &&
                       current.AnalysisSnapshotHash == databaseFingerprint &&
                       current.Version == activeVersion;
            if (noOp)
            {
                project.IndexStatus = ProjectIndexStatus.Indexed;
                project.PendingFileCount = 0;
                project.IndexError = null;
                await _projects.SaveAsync(project, cancellationToken);
                var run = CompleteRun(
                    project, runId, "no-op", startedAt, stopwatch,
                    current!.Version, current.WorkingTreeFingerprint, stageDurations);
                RecordRun(run);
                SetProgress(projectId, "complete", "FBL 索引內容未變更。", 100, runId, "no-op", stopwatch);
                return project;
            }

            var version = Guid.NewGuid().ToString("N");
            var attempt = CreateManifest(
                project,
                version,
                fingerprint,
                databaseFingerprint,
                fileManifests,
                startedAt,
                IndexManifestStatus.Indexing);
            await _manifests.SaveAttemptAsync(attempt, cancellationToken);

            if (!await _neo4jRuntime.EnsureAvailableAsync(null, cancellationToken))
            {
                throw new InvalidOperationException(
                    _neo4jRuntime.LastError ?? "Neo4j 圖譜資料庫目前無法使用。");
            }

            SetProgress(projectId, "extract", "正在解析原始碼與已設定資料庫物件…", 20, runId, "full", stopwatch);
            var extractWatch = Stopwatch.StartNew();
            var resultDocument = await BuildGraphDocumentAsync(
                project.RootPath,
                databaseSources,
                cancellationToken);
            extractWatch.Stop();
            stageDurations["extract"] = extractWatch.ElapsedMilliseconds;

            SetProgress(projectId, "publish", "結構驗證完成，正在原子發布 Neo4j 圖譜…", 80, runId, "full", stopwatch);
            var publishWatch = Stopwatch.StartNew();
            var communities = FblAuthorityCommunityBuilder.Build(resultDocument);
            var digest = ComputeDocumentDigest(resultDocument, fingerprint);
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
                IndexMode = "full",
                RequiresRetry = false,
            };
            await _manifests.PromoteAsync(promoted, cancellationToken);

            project.Languages = "csharp,javascript,typescript,aspx,sql";
            project.IndexStatus = ProjectIndexStatus.Indexed;
            project.IndexedAt = completedAt;
            project.IndexError = null;
            project.NodeCount = resultDocument.Nodes.Count;
            project.EdgeCount = resultDocument.Relationships.Count;
            project.IndexManifestVersion = version;
            project.PendingFileCount = 0;
            _pendingFiles.TryRemove(projectId, out _);
            await _projects.SaveAsync(project, cancellationToken);

            // 只排入三個 C0；本方法不等待模型，失敗也不得回滾已發布結構。
            try
            {
                await _communityAi.PrewarmC0Async(projectId, version, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "FBL Community 背景預熱排程失敗，結構圖仍可使用。Project={ProjectId}, ExceptionType={ExceptionType}",
                    projectId,
                    exception.GetType().Name);
            }

            var completedRun = CompleteRun(
                project, runId, "full", startedAt, stopwatch, version, digest, stageDurations);
            RecordRun(completedRun);
            SetProgress(projectId, "complete", "FBL 權威圖索引完成；Community AI Summary 將在背景補齊。", 100, runId, "full", stopwatch);
            return project;
        }
        catch (Exception exception)
        {
            if (publishedVersion is not null)
            {
                try
                {
                    // Publish 已切換 Neo4j 後，若本機 manifest／Project 寫入失敗，補償回上一版。
                    await _graphStore.RollbackPublishedVersionAsync(
                        projectId,
                        publishedVersion,
                        previousVersion,
                        CancellationToken.None);
                    await _manifests.RestoreCurrentAsync(
                        projectId,
                        previousVersion,
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
            var failedManifest = new ProjectIndexManifest(
                projectId,
                runId,
                project.RootPath,
                null,
                string.Empty,
                [],
                [],
                PendingFiles(projectId),
                IndexerVersion,
                startedAt,
                DateTimeOffset.UtcNow,
                IndexManifestStatus.Failed,
                previousNodeCount,
                previousEdgeCount,
                SafeError(exception),
                ProjectGraphVersions.CanonicalSchema,
                null,
                "full",
                true);
            await _manifests.SaveAttemptAsync(failedManifest, CancellationToken.None);
            stopwatch.Stop();
            var failedRun = new GraphIndexRun(
                projectId,
                runId,
                "full",
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
            SetProgress(projectId, "failed", SafeError(exception), 100, runId, "full", stopwatch);
            throw;
        }
    }

    /// <summary>
    /// 依已通過連線測試的來源建立候選 GraphDocument。
    /// SQL Server 使用 FBL authority pipeline；SQLite 使用 catalog-only pipeline；沒有資料庫時保留 source-only 圖。
    /// </summary>
    private async Task<GraphDocument> BuildGraphDocumentAsync(
        string rootPath,
        IReadOnlyList<GraphDatabaseSource> sources,
        CancellationToken cancellationToken)
    {
        // SQL Server authority builder 會建立一次 C#/View/Script 索引；SQLite 只附加 DB Object，
        // 避免雙 Provider 時重複掃描整個原始碼。
        var sqlSource = sources.FirstOrDefault(source =>
            source.Provider == ProjectDatabaseProvider.SqlServer);
        GraphDocument? document = null;
        if (sqlSource is not null)
        {
            var authority = await _builder.BuildAsync(
                new FblAuthorityBuildRequest(
                    rootPath,
                    new FblSqlServerAuthoritySource(sqlSource.ConnectionString),
                    ExpectedMenuCount: null,
                    SourceCommit: null,
                    DatabaseSnapshotId: sqlSource.ConfigurationFingerprint,
                    Provider: "SqlServer",
                    DatabaseName: sqlSource.DatabaseName),
                cancellationToken);
            if (authority.Diagnostics.HasBlockingErrors)
            {
                throw new InvalidOperationException(BuildPreflightError(authority.Diagnostics));
            }
            document = authority.Document;
        }

        foreach (var source in sources.Where(source =>
                     source.Provider == ProjectDatabaseProvider.Sqlite))
        {
            var objects = await _databaseExtractor.LoadSqliteDatabaseObjectsAsync(
                source,
                cancellationToken);
            if (document is null)
            {
                document = await new SqliteGraphDocumentBuilder().BuildAsync(
                    rootPath,
                    source,
                    objects,
                    cancellationToken);
            }
            else
            {
                document = AddSqliteDatabaseObjects(document, source, objects);
            }
        }

        if (document is not null)
        {
            // SQLite-only 不套用 SQL Server authority 規則，但仍檢查共用 schema、端點與機敏資料界線。
            var diagnostics = new GraphDocumentValidator(new PreflightValidatorOptions
            {
                ExpectedCenterMenuCount = null,
                RequiredDatabaseName = sqlSource is null && sources.Count == 1
                    ? sources[0].DatabaseName
                    : null,
                RequiredProvider = sqlSource is null && sources.Count == 1
                    ? "Sqlite"
                    : null,
                RequireCompleteExtraction = true,
            }).Validate(document);
            if (diagnostics.HasBlockingErrors)
            {
                throw new InvalidOperationException(BuildPreflightError(diagnostics));
            }
            return document;
        }

        // 沒有設定外部資料庫時仍建立可檢索的原始碼類別清單，並明確標示 source-only。
        return await new SqliteGraphDocumentBuilder().BuildAsync(
            rootPath,
            new GraphDatabaseSource(
                ProjectDatabaseProvider.Sqlite,
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = ":memory:",
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.Memory,
                }.ConnectionString,
                "source-only"),
            Array.Empty<DatabaseObjectCatalogItem>(),
            cancellationToken,
            sourceOnly: true);
    }

    /// <summary>將 SQLite catalog 節點附加到既有 authority graph，不重新解析原始碼。</summary>
    private static GraphDocument AddSqliteDatabaseObjects(
        GraphDocument document,
        GraphDatabaseSource source,
        IReadOnlyList<DatabaseObjectCatalogItem> objects)
    {
        var metadata = document.Metadata with
        {
            Provider = "Mixed",
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
                relationship.Evidence,
                relationship.Properties);
        }
        foreach (var databaseObject in objects)
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
        }
        return builder.Build();
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
        string fingerprint,
        string databaseFingerprint,
        IReadOnlyList<IndexedFileManifest> files,
        DateTimeOffset startedAt,
        IndexManifestStatus status) =>
        new(
            project.Id,
            version,
            project.RootPath,
            null,
            fingerprint,
            [],
            files,
            [],
            IndexerVersion,
            startedAt,
            null,
            status,
            GraphSchemaVersion: ProjectGraphVersions.CanonicalSchema,
            AnalysisSnapshotHash: databaseFingerprint,
            IndexMode: "full",
            RequiresRetry: false);

    /// <summary>列舉 FBL 權威解析會讀取的檔案，並排除建置與套件目錄。</summary>
    private IReadOnlyList<string> EnumerateFiles(string rootPath)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(rootPath));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }
                result.Add(file);
                if (result.Count > _options.MaximumFiles)
                {
                    throw new InvalidOperationException($"FBL 可索引檔案超過安全上限 {_options.MaximumFiles}。");
                }
            }
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>一次串流讀檔產生 SHA-256，避免將大型來源全部載入記憶體。</summary>
    private static async Task<IReadOnlyList<IndexedFileManifest>> HashFilesAsync(
        string rootPath,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var result = new List<IndexedFileManifest>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            var info = new FileInfo(file);
            result.Add(new IndexedFileManifest(
                NormalizePath(Path.GetRelativePath(rootPath, file)),
                LanguageForExtension(info.Extension),
                info.Length,
                Convert.ToHexString(hash).ToLowerInvariant(),
                LastWriteAt: info.LastWriteTimeUtc));
        }
        return result;
    }

    /// <summary>以穩定排序的檔案 hash、DB 設定版本與索引器版本產生輸入指紋。</summary>
    private static string ComputeInputFingerprint(
        IReadOnlyList<IndexedFileManifest> files,
        string databaseFingerprint)
    {
        var material = new StringBuilder(IndexerVersion)
            .Append('\n')
            .Append(databaseFingerprint)
            .Append('\n');
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            material.Append(file.RelativePath).Append('|').Append(file.ContentHash).Append('\n');
        }
        return Sha256(material.ToString());
    }

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

    /// <summary>把 Preflight 錯誤壓縮成不含密碼且可人工追查的訊息。</summary>
    private static string BuildPreflightError(PreflightResult diagnostics)
    {
        var samples = diagnostics.Issues
            .Where(issue => issue.Severity == PreflightSeverity.Error)
            .Take(10)
            .Select(issue => $"{issue.ReasonCode}: {issue.Message}");
        return $"FBL Graph Preflight 未通過，共 {diagnostics.ErrorCount} 個錯誤。" +
               string.Join(" | ", samples);
    }

    /// <summary>取得 active 版本；Neo4j 尚未啟動時不阻擋第一次完整建置。</summary>
    private async Task<string?> SafeActiveVersionAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _graphStore.GetActiveManifestAsync(projectId, cancellationToken);
        }
        catch
        {
            return null;
        }
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

    /// <summary>取得穩定排序的 pending 檔案。</summary>
    private IReadOnlyList<string> PendingFiles(string projectId) =>
        _pendingFiles.TryGetValue(projectId, out var files)
            ? files.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    /// <summary>確認 watcher 路徑沒有越過專案根目錄。</summary>
    private static bool IsInsideRoot(string rootPath, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(path);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>依副檔名寫入 FBL 索引支援的顯示用語言。</summary>
    private static string LanguageForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" or ".csproj" or ".sln" => "csharp",
        ".js" or ".jsx" => "javascript",
        ".ts" or ".tsx" => "typescript",
        ".aspx" or ".ascx" or ".master" => "aspx",
        ".sql" => "sql",
        ".xml" or ".config" => "xml",
        _ => "text",
    };

    /// <summary>正規化 manifest 中的相對路徑。</summary>
    private static string NormalizePath(string path) => path.Replace('\\', '/');

    /// <summary>計算小寫十六進位 SHA-256。</summary>
    private static string Sha256(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    /// <summary>限制持久化錯誤長度並避免連線字串意外寫入。</summary>
    private static string SafeError(Exception exception)
    {
        var message = exception.Message;
        if (SensitiveValueDetector.ContainsSensitiveValue(message))
        {
            return $"索引失敗（{exception.GetType().Name}）；詳細訊息可能包含機敏設定，已隱藏。";
        }
        return message.Length <= 2_000 ? message : message[..2_000];
    }

    /// <summary>驗證所有技術上限。</summary>
    private static void ValidateOptions(GraphIndexingOptions options)
    {
        if (options.WatcherDebounceMilliseconds is < 100 or > 60_000 ||
            options.MaximumFiles is < 1_000 or > 1_000_000)
        {
            throw new InvalidOperationException("GraphRAG 索引設定超出安全範圍。");
        }
    }
}

/// <summary>供專案建立、匯入與刪除端點即時管理檔案監看。</summary>
public interface IGraphIndexWatcherRegistry
{
    /// <summary>註冊或更新一個專案根目錄。</summary>
    bool RegisterProject(ProjectEntity project);

    /// <summary>解除專案監看與尚未執行的 debounce。</summary>
    bool UnregisterProject(string projectId);

    /// <summary>
    /// 判斷目前是否仍有 watcher 監看指定專案。
    /// 問答端點用這個狀態決定是否需要執行一次昂貴的完整檔案指紋 fallback。
    /// </summary>
    bool IsRegistered(string projectId);
}

/// <summary>
/// 只監看 FBL 解析會使用的來源檔案。Host 啟動時讀取一次專案清單，
/// 後續由 REST 端點即時註冊，不以固定週期查詢 SQLite。
/// </summary>
public sealed class GraphIndexWatcherService(
    IProjectRepository projects,
    GraphIndexingService indexing,
    IOptions<GraphIndexingOptions> options,
    ILogger<GraphIndexWatcherService> logger) : BackgroundService, IGraphIndexWatcherRegistry
{
    private static readonly IReadOnlySet<string> WatchedExtensions = new HashSet<string>(
        [".cs", ".js", ".jsx", ".ts", ".tsx", ".aspx", ".ascx", ".master", ".sql", ".xml", ".config"],
        StringComparer.OrdinalIgnoreCase);
    private readonly object _watcherGate = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounces = new(StringComparer.Ordinal);
    private CancellationToken _stoppingToken;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        try
        {
            foreach (var project in await projects.ListAsync(stoppingToken))
            {
                RegisterProject(project);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <inheritdoc />
    public bool RegisterProject(ProjectEntity project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.Id) || !Directory.Exists(project.RootPath))
        {
            return false;
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootPath));
        lock (_watcherGate)
        {
            if (_watchers.TryGetValue(project.Id, out var current))
            {
                if (Path.TrimEndingDirectorySeparator(current.Path)
                    .Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                _watchers.TryRemove(project.Id, out _);
                current.Dispose();
            }
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += (_, args) => Schedule(project.Id, args.FullPath);
            watcher.Created += (_, args) => Schedule(project.Id, args.FullPath);
            watcher.Deleted += (_, args) => Schedule(project.Id, args.FullPath);
            watcher.Renamed += (_, args) => Schedule(project.Id, args.FullPath);
            // FileSystemWatcher 的內部緩衝區溢位或底層 I/O 錯誤可能造成事件遺失。
            // 這時不能假設索引仍然是最新，先標記 PendingChanges，讓下一次索引
            // 或問答的安全流程重新建立權威 Graph。
            watcher.Error += (_, args) => HandleWatcherError(project.Id, args.GetException());
            _watchers[project.Id] = watcher;
            return true;
        }
    }

    /// <inheritdoc />
    public bool UnregisterProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }
        lock (_watcherGate)
        {
            if (!_watchers.TryRemove(projectId, out var watcher))
            {
                return false;
            }
            watcher.Dispose();
            if (_debounces.TryRemove(projectId, out var debounce))
            {
                debounce.Cancel();
                debounce.Dispose();
            }
            return true;
        }
    }

    /// <inheritdoc />
    public bool IsRegistered(string projectId) =>
        !string.IsNullOrWhiteSpace(projectId) && _watchers.ContainsKey(projectId);

    /// <summary>合併短時間異動並觸發一次完整 authority rebuild。</summary>
    private void Schedule(string projectId, string path)
    {
        if (!WatchedExtensions.Contains(Path.GetExtension(path)))
        {
            return;
        }
        // 原始碼工具共用的目錄與內容快取只失效異動檔案，下一次查詢再惰性重建；
        // 這不會觸發同步索引，也不會讓當前問答等待完整 Catch-up。
        ProjectAnalysisTools.InvalidateFileCatalog(path);
        if (_debounces.TryRemove(projectId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        var debounce = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        _debounces[projectId] = debounce;
        _ = Task.Run(async () =>
        {
            try
            {
                await indexing.MarkPendingChangesAsync(projectId, path, debounce.Token);
                await Task.Delay(options.Value.WatcherDebounceMilliseconds, debounce.Token);
                await indexing.IncrementalIndexAsync(projectId, debounce.Token);
            }
            catch (OperationCanceledException) when (debounce.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "FBL Graph watcher 索引失敗。Project={ProjectId}, ExceptionType={ExceptionType}",
                    projectId,
                    exception.GetType().Name);
            }
            finally
            {
                if (_debounces.TryGetValue(projectId, out var current) && ReferenceEquals(current, debounce))
                {
                    _debounces.TryRemove(projectId, out _);
                }
                debounce.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 將 watcher 的底層錯誤轉成持久化的 PendingChanges。
    /// 不在錯誤事件執行緒直接重建 Graph，避免阻塞 FileSystemWatcher 的事件處理。
    /// </summary>
    private void HandleWatcherError(string projectId, Exception exception)
    {
        logger.LogWarning(
            "FBL Graph watcher 發生檔案系統錯誤，將要求下一次索引重新確認。Project={ProjectId}, ExceptionType={ExceptionType}",
            projectId,
            exception.GetType().Name);

        _ = MarkWatcherErrorPendingAsync(projectId);
    }

    /// <summary>非同步保存 watcher 錯誤狀態，避免未觀察的背景例外。</summary>
    private async Task MarkWatcherErrorPendingAsync(string projectId)
    {
        try
        {
            await indexing.MarkPendingChangesAsync(projectId, cancellationToken: _stoppingToken);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "FBL Graph watcher 無法保存 PendingChanges。Project={ProjectId}, ExceptionType={ExceptionType}",
                projectId,
                exception.GetType().Name);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        lock (_watcherGate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
        }
        foreach (var debounce in _debounces.Values)
        {
            debounce.Cancel();
            debounce.Dispose();
        }
        base.Dispose();
    }
}
