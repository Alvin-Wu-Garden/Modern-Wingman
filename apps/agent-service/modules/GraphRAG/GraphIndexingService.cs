using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// GraphRAG V3 索引技術設定。
/// 這些開關不允許選擇 NodeKind／EdgeKind 或改變 schema，因此不是索引 profile。
/// </summary>
public sealed class GraphIndexingOptions
{
    /// <summary>設定 section 名稱。</summary>
    public const string SectionName = "GraphRAG";

    /// <summary>內容、schema、extractor、DB fingerprint 全相同時啟用 no-op。</summary>
    public bool EnableNoOpFastPath { get; set; } = true;

    /// <summary>緊急除錯用 full rebuild kill switch。</summary>
    public bool ForceFullIndex { get; set; }

    /// <summary>檔案 watcher 的 debounce 毫秒數。</summary>
    public int WatcherDebounceMilliseconds { get; set; } = 800;

    /// <summary>單一專案一次最多索引的原始檔案數，避免錯誤 root 耗盡資源。</summary>
    public int MaximumFiles { get; set; } = 250_000;
}

/// <summary>提供 project-specific live DB secret，不讓 indexing service 直接讀 appsettings 明文密碼。</summary>
public interface IGraphDatabaseSourceProvider
{
    /// <summary>沒有設定 live DB 時回傳 null；實作不得將 connection string 寫入 log。</summary>
    /// <param name="project">目前索引專案。</param>
    /// <param name="cancellationToken">取消取得 secret 的 token。</param>
    /// <returns>記憶體中的安全 SQL Server source。</returns>
    Task<SqlServerGraphSource?> GetAsync(
        ProjectEntity project,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 從 process environment 取得 optional SQL Server secret。
/// 支援全域 WINGMAN_GRAPHRAG_SQLSERVER_CONNECTION，也支援尾端加雙底線與 project ID 的專案專用值。
/// </summary>
public sealed class EnvironmentGraphDatabaseSourceProvider : IGraphDatabaseSourceProvider
{
    /// <inheritdoc />
    public Task<SqlServerGraphSource?> GetAsync(
        ProjectEntity project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        var suffix = "__" + EnvironmentKey(project.Id);
        var connectionString =
            Environment.GetEnvironmentVariable(
                "WINGMAN_GRAPHRAG_SQLSERVER_CONNECTION" + suffix) ??
            Environment.GetEnvironmentVariable(
                "WINGMAN_GRAPHRAG_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return Task.FromResult<SqlServerGraphSource?>(null);

        var database =
            Environment.GetEnvironmentVariable(
                "WINGMAN_GRAPHRAG_SQLSERVER_DATABASE" + suffix) ??
            Environment.GetEnvironmentVariable(
                "WINGMAN_GRAPHRAG_SQLSERVER_DATABASE");
        if (string.IsNullOrWhiteSpace(database))
        {
            // SqlConnectionStringBuilder 只在記憶體解析 InitialCatalog；
            // 解析結果只取 logical database name，完整連線字串不進例外或 log。
            database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException(
                "已設定 GraphRAG SQL Server 連線，但缺少 logical database name。");
        return Task.FromResult<SqlServerGraphSource?>(
            new SqlServerGraphSource(connectionString, database));
    }

    private static string EnvironmentKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character)
                ? char.ToUpperInvariant(character)
                : '_');
        return builder.ToString();
    }
}

/// <summary>前端輪詢用的 V3 索引進度。</summary>
/// <param name="ProjectId">專案 ID。</param>
/// <param name="Phase">scan、extract、assemble、publish、complete 或 failed。</param>
/// <param name="Message">繁體中文進度訊息。</param>
/// <param name="Percent">0 到 100。</param>
/// <param name="RunId">本次索引 run ID。</param>
/// <param name="Mode">full 或 no-op。</param>
/// <param name="ElapsedMilliseconds">目前經過毫秒。</param>
public sealed record GraphIndexProgress(
    string ProjectId,
    string Phase,
    string Message,
    int Percent,
    string RunId,
    string Mode,
    long ElapsedMilliseconds);

/// <summary>一次 V3 index run 的可診斷結果。</summary>
/// <param name="ProjectId">專案 ID。</param>
/// <param name="RunId">run ID。</param>
/// <param name="Mode">full 或 no-op。</param>
/// <param name="Status">succeeded、partial、failed。</param>
/// <param name="StartedAt">開始時間。</param>
/// <param name="CompletedAt">完成時間。</param>
/// <param name="ElapsedMilliseconds">總耗時。</param>
/// <param name="NodeCount">active node 數。</param>
/// <param name="EdgeCount">active edge 數。</param>
/// <param name="ManifestVersion">成功發布版本。</param>
/// <param name="CanonicalDigest">V3 canonical digest。</param>
/// <param name="StageDurationsMilliseconds">各階段耗時。</param>
/// <param name="Error">已去敏感的失敗說明。</param>
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
    string? Error = null);

/// <summary>
/// GraphRAG V3 索引 orchestrator。
/// 順序固定為 artifact hash → extractor → canonical validation → Neo4j staging/publish → SQLite promote；
/// 任一步失敗都不會覆寫 ProjectEntity 的上一個成功 manifest 與統計。
/// </summary>
public sealed class GraphIndexingService
{
    private static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(
        [".cs", ".java", ".js", ".jsx", ".ts", ".tsx", ".sql"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> ExcludedDirectories = new HashSet<string>(
        [".git", ".vs", "bin", "obj", "node_modules", "packages", "target", "dist", "build", "out", "vendor"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly string IndexerVersion =
        typeof(GraphIndexingService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(GraphIndexingService).Assembly.GetName().Version?.ToString()
        ?? "development";

    private readonly IReadOnlyList<IGraphExtractor> _extractors;
    private readonly SqlServerGraphExtractor _sqlExtractor;
    private readonly IGraphStore _graphStore;
    private readonly INeo4jRuntime _neo4jRuntime;
    private readonly IProjectRepository _projects;
    private readonly IProjectIndexManifestStore _manifests;
    private readonly IGraphDatabaseSourceProvider _databaseSources;
    private readonly GraphIndexingOptions _options;
    private readonly ILogger<GraphIndexingService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GraphIndexProgress> _progress =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GraphIndexRun> _runs =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GraphSnapshot> _activeSnapshots =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _pendingFiles =
        new(StringComparer.Ordinal);

    /// <summary>建立 V3 index orchestrator。</summary>
    public GraphIndexingService(
        IEnumerable<IGraphExtractor> extractors,
        SqlServerGraphExtractor sqlExtractor,
        IGraphStore graphStore,
        INeo4jRuntime neo4jRuntime,
        IProjectRepository projects,
        IProjectIndexManifestStore manifests,
        IGraphDatabaseSourceProvider databaseSources,
        IOptions<GraphIndexingOptions> options,
        ILogger<GraphIndexingService> logger)
    {
        _extractors = extractors
            .DistinctBy(extractor => extractor.Id, StringComparer.Ordinal)
            .OrderBy(extractor => extractor.Id, StringComparer.Ordinal)
            .ToList();
        _sqlExtractor = sqlExtractor;
        _graphStore = graphStore;
        _neo4jRuntime = neo4jRuntime;
        _projects = projects;
        _manifests = manifests;
        _databaseSources = databaseSources;
        _options = options.Value;
        _logger = logger;
        ValidateOptions(_options);
        if (_extractors.Count == 0)
            throw new InvalidOperationException("GraphRAG V3 至少需要一個 extractor。");
    }

    /// <summary>取得目前或最後一筆索引進度。</summary>
    public GraphIndexProgress? GetProgress(string projectId) =>
        _progress.TryGetValue(projectId, out var value) ? value : null;

    /// <summary>取得最後一次 V3 index run。</summary>
    public GraphIndexRun? GetLastRun(string projectId) =>
        _runs.TryGetValue(projectId, out var value) ? value : null;

    /// <summary>取得本 process 最近成功的 canonical snapshot，供 diagnostics 與測試使用。</summary>
    public GraphSnapshot? GetActiveSnapshotForDiagnostics(string projectId) =>
        _activeSnapshots.TryGetValue(projectId, out var value) ? value : null;

    /// <summary>刪除 watcher／progress／memory snapshot 狀態；不刪持久化 graph。</summary>
    public void ForgetProjectState(string projectId)
    {
        _activeSnapshots.TryRemove(projectId, out _);
        _pendingFiles.TryRemove(projectId, out _);
        _progress.TryRemove(projectId, out _);
        _runs.TryRemove(projectId, out _);
    }

    /// <summary>Watcher 記錄待處理檔案；不會直接修改 active graph。</summary>
    public async Task MarkPendingChangesAsync(
        string projectId,
        string? fullPath = null,
        CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetAsync(projectId, cancellationToken);
        if (project is null) return;
        if (!string.IsNullOrWhiteSpace(fullPath) && IsInsideRoot(project.RootPath, fullPath))
        {
            var relativePath = GraphIdentity.NormalizePath(
                Path.GetRelativePath(project.RootPath, fullPath));
            _pendingFiles
                .GetOrAdd(projectId, _ => new ConcurrentDictionary<string, byte>(
                    StringComparer.OrdinalIgnoreCase))[relativePath] = 0;
        }
        if (project.IndexStatus != ProjectIndexStatus.Indexing)
            project.IndexStatus = ProjectIndexStatus.PendingChanges;
        project.PendingFileCount = PendingFiles(projectId).Count;
        project.IndexError = null;
        await _projects.SaveAsync(project, cancellationToken);
    }

    /// <summary>取得 SQLite manifest、最新 attempt 與 watcher pending files。</summary>
    public async Task<ProjectIndexDiagnostics> GetDiagnosticsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await ReconcileManifestAsync(projectId, cancellationToken);
        var current = await _manifests.GetCurrentAsync(projectId, cancellationToken);
        var latest = await _manifests.GetLatestAttemptAsync(projectId, cancellationToken);
        var pending = PendingFiles(projectId);
        return new ProjectIndexDiagnostics(
            current,
            latest,
            pending,
            pending.Count > 0 ||
            latest is { Status: IndexManifestStatus.Failed or IndexManifestStatus.Stale });
    }

    /// <summary>
    /// 以 raw byte hash 補捉 watcher 漏失事件。mtime 只作顯示，不作內容相同證據。
    /// </summary>
    public async Task<bool> CatchUpAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await RequiredProjectAsync(projectId, cancellationToken);
        var current = await _manifests.GetCurrentAsync(projectId, cancellationToken);
        if (current is null) return false;
        var files = EnumerateSourceFiles(project.RootPath);
        var live = await HashFilesAsync(
            project.RootPath, files, current.Files, cancellationToken);
        var currentByPath = current.Files.ToDictionary(
            item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var liveByPath = live.ToDictionary(
            item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var changed = live
            .Where(item => !currentByPath.TryGetValue(item.RelativePath, out var old) ||
                           !string.Equals(item.ContentHash, old.ContentHash, StringComparison.Ordinal))
            .Select(item => item.RelativePath)
            .Concat(currentByPath.Keys.Except(
                liveByPath.Keys, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in changed)
            await MarkPendingChangesAsync(
                projectId, Path.Combine(project.RootPath, path), cancellationToken);
        return changed.Count > 0;
    }

    /// <summary>執行 full/no-op 決策與安全發布。</summary>
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
        }
    }

    /// <summary>
    /// Watcher 入口。無內容變更時回傳 null；目前不確定的變更一律退回 full，
    /// 不以不完整 delta 冒險遺失反向呼叫或資料依賴。
    /// </summary>
    public async Task<ProjectEntity?> IncrementalIndexAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await RequiredProjectAsync(projectId, cancellationToken);
        var current = await _manifests.GetCurrentAsync(projectId, cancellationToken);
        if (current is not null && !await CatchUpAsync(projectId, cancellationToken))
        {
            _pendingFiles.TryRemove(projectId, out _);
            project.PendingFileCount = 0;
            project.IndexStatus = current.Status == IndexManifestStatus.Partial
                ? ProjectIndexStatus.Partial
                : ProjectIndexStatus.Indexed;
            await _projects.SaveAsync(project, cancellationToken);
            return null;
        }
        return await IndexProjectAsync(projectId, cancellationToken);
    }

    private async Task<ProjectEntity> IndexCoreAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var project = await RequiredProjectAsync(projectId, cancellationToken);
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var stages = new Dictionary<string, long>(StringComparer.Ordinal);
        var mode = "full";
        ProjectIndexManifest? attempt = null;
        var previousState = CaptureProjectState(project);
        try
        {
            if (!await _neo4jRuntime.EnsureAvailableAsync(
                    null, cancellationToken))
                throw new InvalidOperationException(
                    _neo4jRuntime.LastError ??
                    "Neo4j V3 runtime 不可用，已保留上一個成功圖譜。");
            await ReconcileManifestAsync(projectId, cancellationToken);
            project.IndexStatus = ProjectIndexStatus.Indexing;
            project.IndexError = null;
            await _projects.SaveAsync(project, cancellationToken);
            Progress(projectId, runId, mode, "scan", "正在計算原始檔案與資料庫指紋。", 5, stopwatch);

            var stage = Stopwatch.StartNew();
            var files = EnumerateSourceFiles(project.RootPath);
            var current = await _manifests.GetCurrentAsync(
                projectId, cancellationToken);
            var fileManifests = await HashFilesAsync(
                project.RootPath, files, current?.Files, cancellationToken);
            var databaseSource = await _databaseSources.GetAsync(
                project, cancellationToken);
            var databaseFingerprint = databaseSource is null
                ? "none"
                : await _sqlExtractor.ComputeDatabaseFingerprintAsync(
                    databaseSource, cancellationToken);
            var workingFingerprint = WorkingFingerprint(
                fileManifests, databaseFingerprint);
            stages["scan"] = stage.ElapsedMilliseconds;

            var neo4jManifest = await _graphStore.GetActiveManifestAsync(
                projectId, cancellationToken);
            if (CanNoOp(
                    current, neo4jManifest, workingFingerprint, databaseSource,
                    databaseFingerprint))
            {
                mode = "no-op";
                _pendingFiles.TryRemove(projectId, out _);
                project.PendingFileCount = 0;
                project.IndexStatus = current!.Status == IndexManifestStatus.Partial
                    ? ProjectIndexStatus.Partial
                    : ProjectIndexStatus.Indexed;
                project.IndexError = null;
                await _projects.SaveAsync(project, cancellationToken);
                stopwatch.Stop();
                Progress(projectId, runId, mode, "complete", "內容完全相同，已安全略過重建。", 100, stopwatch);
                var noOpRun = new GraphIndexRun(
                    projectId, runId, mode, "succeeded", startedAt, DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds, project.NodeCount, project.EdgeCount,
                    project.IndexManifestVersion, current.AnalysisSnapshotHash,
                    stages);
                _runs[projectId] = noOpRun;
                return project;
            }

            var activeSnapshot = _activeSnapshots.TryGetValue(projectId, out var memorySnapshot)
                ? memorySnapshot
                : null;
            var bodyDeltaPlan = TryPlanBodyDelta(
                current,
                neo4jManifest,
                fileManifests,
                databaseSource,
                databaseFingerprint,
                activeSnapshot);
            var csharpDeltaExtractor = _extractors.FirstOrDefault(extractor =>
                string.Equals(extractor.Id, "csharp-roslyn-v3", StringComparison.Ordinal));
            // Body-delta 重建依賴正式 C# extractor。測試替身或自訂 extractor
            // 不具備相同語意時必須安全退回完整抽取，不能假設特定 ID 一定存在。
            var bodyDelta = bodyDeltaPlan is not null && csharpDeltaExtractor is not null;
            mode = bodyDelta ? "body-delta" : "full";
            var manifestVersion = Guid.NewGuid().ToString("N");
            attempt = new ProjectIndexManifest(
                projectId,
                manifestVersion,
                project.RootPath,
                HeadCommit(project.RootPath),
                workingFingerprint,
                [],
                fileManifests,
                PendingFiles(projectId),
                IndexerVersion,
                startedAt,
                null,
                IndexManifestStatus.Indexing,
                GraphSchemaVersion: GraphAssembler.SchemaVersion,
                IndexMode: mode);
            await _manifests.SaveAttemptAsync(attempt, cancellationToken);

            stage.Restart();
            Progress(
                projectId,
                runId,
                mode,
                "extract",
                bodyDelta
                    ? "已確認只有 C# method body 變更，正在重算完整 C# type graph。"
                    : "正在抽取 C#、Java、前端、SQL 與動態業務關係。",
                25,
                stopwatch);
            List<GraphFragment> fragments;
            if (bodyDelta)
            {
                fragments =
                [
                    CreateNonCSharpBaseFragment(
                        activeSnapshot!,
                        bodyDeltaPlan!.ChangedPaths),
                    await csharpDeltaExtractor!.ExtractAsync(
                        project.RootPath, files, cancellationToken),
                ];
                if (_extractors.Any(extractor => string.Equals(
                        extractor.Id, _sqlExtractor.Id, StringComparison.Ordinal)))
                {
                    var changedFiles = bodyDeltaPlan.ChangedPaths
                        .Select(path => Path.GetFullPath(
                            Path.Combine(project.RootPath, path)))
                        .ToList();
                    fragments.Add(await _sqlExtractor.ExtractAsync(
                        project.RootPath, changedFiles, cancellationToken));
                }
            }
            else
            {
                var extractionTasks = _extractors.Select(async extractor =>
                {
                    var extractorClock = Stopwatch.StartNew();
                    var fragment = await extractor.ExtractAsync(
                        project.RootPath, files, cancellationToken);
                    extractorClock.Stop();
                    return (extractor.Id, Fragment: fragment, extractorClock.ElapsedMilliseconds);
                });
                var extractionResults = await Task.WhenAll(extractionTasks);
                fragments = extractionResults.Select(result => result.Fragment).ToList();
                foreach (var result in extractionResults)
                    stages[$"extract:{result.Id}"] = result.ElapsedMilliseconds;
            }
            if (!bodyDelta && databaseSource is not null)
            {
                var databaseClock = Stopwatch.StartNew();
                fragments.Add(await _sqlExtractor.ExtractDatabaseAsync(
                    databaseSource, cancellationToken));
                databaseClock.Stop();
                stages["extract:sqlserver-live-database-v3"] =
                    databaseClock.ElapsedMilliseconds;
            }
            stages["extract"] = stage.ElapsedMilliseconds;

            stage.Restart();
            Progress(projectId, runId, mode, "assemble", "正在合併證據並執行 V3 品質閘門。", 60, stopwatch);
            var artifacts = fileManifests.Select(item => new GraphArtifact(
                    $"file:{item.RelativePath}",
                    item.RelativePath,
                    item.Language,
                    item.Length,
                    item.ContentHash,
                    item.Status.ToLowerInvariant(),
                    item.Reason))
                .ToList();
            if (databaseSource is not null)
                artifacts.Add(new GraphArtifact(
                    $"db:{GraphIdentity.NormalizeRequiredToken(databaseSource.DatabaseName, "databaseName")}",
                    $"db:{GraphIdentity.NormalizeRequiredToken(databaseSource.DatabaseName, "databaseName")}",
                    "database",
                    0,
                    databaseFingerprint ?? "unavailable",
                    databaseFingerprint is null ? "failed" : "indexed",
                    databaseFingerprint is null ? "資料庫 fingerprint 暫時不可用" : null));
            var descriptor = new GraphIndexerDescriptor(
                IndexerVersion,
                _extractors.ToDictionary(
                    extractor => extractor.Id,
                    extractor => extractor.Version,
                    StringComparer.Ordinal));
            var snapshot = GraphAssembler.Assemble(
                projectId,
                manifestVersion,
                DateTimeOffset.UtcNow,
                descriptor,
                workingFingerprint,
                mode,
                artifacts,
                fragments);
            stages["assemble"] = stage.ElapsedMilliseconds;

            var partial = snapshot.CapabilityGaps.Count > 0 ||
                          snapshot.Diagnostics.Any(item =>
                              item.Severity == GraphDiagnosticSeverity.Warning);
            var ready = attempt with
            {
                CompletedAt = DateTimeOffset.UtcNow,
                Status = partial ? IndexManifestStatus.Partial : IndexManifestStatus.Fresh,
                NodeCount = snapshot.Nodes.Count,
                EdgeCount = snapshot.Edges.Count,
                GraphSchemaVersion = snapshot.SchemaVersion,
                AnalysisSnapshotHash = snapshot.CanonicalDigest,
                IndexMode = mode,
                RequiresRetry = partial,
            };
            await _manifests.SaveAttemptAsync(ready, cancellationToken);

            stage.Restart();
            Progress(projectId, runId, mode, "publish", "正在 staging、驗證並原子切換 Neo4j active graph。", 80, stopwatch);
            // 在切換 active manifest 前讀取上一版 AI community cache；新報告只有在
            // member evidence＋prompt version 的 CacheKey 完全相同時才可沿用摘要。
            var cachedCommunityReports = await _graphStore.ListCommunityReportsAsync(
                projectId,
                cancellationToken);
            await _graphStore.PublishAsync(snapshot, cancellationToken);
            await _manifests.PromoteAsync(ready, cancellationToken);
            var primaryReports =
                GraphCommunityBuilder.BuildPrimaryReportsValidated(snapshot);
            var leidenGroups = await _graphStore.TryDetectLeidenCommunitiesAsync(
                projectId,
                cancellationToken);
            var secondaryReports = leidenGroups is null
                ? GraphCommunityBuilder.BuildSecondaryReportsValidated(snapshot)
                : GraphCommunityBuilder.BuildSecondaryReportsFromGroupsValidated(
                    snapshot,
                    leidenGroups,
                    "leiden");
            var reports = primaryReports
                .Concat(secondaryReports)
                .OrderBy(report => report.Kind, StringComparer.Ordinal)
                .ThenBy(report => report.CommunityId, StringComparer.Ordinal)
                .ToList();
            var cachedByKey = cachedCommunityReports
                .Where(report =>
                    report.AiEnriched &&
                    !string.IsNullOrWhiteSpace(report.CacheKey))
                .GroupBy(report => report.CacheKey!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(report => report.CommunityId, StringComparer.Ordinal)
                        .First(),
                    StringComparer.Ordinal);
            reports = reports.Select(report =>
                cachedByKey.TryGetValue(report.CacheKey ?? string.Empty, out var cached)
                    ? report with
                    {
                        Summary = cached.Summary,
                        AiEnriched = true,
                    }
                    : report)
                .ToList();
            await _graphStore.SaveCommunityReportsAsync(
                projectId, manifestVersion, reports, cancellationToken);
            stages["publish"] = stage.ElapsedMilliseconds;

            _activeSnapshots[projectId] = snapshot;
            _pendingFiles.TryRemove(projectId, out _);
            project.IndexManifestVersion = manifestVersion;
            project.IndexedAt = DateTimeOffset.UtcNow;
            project.NodeCount = snapshot.Nodes.Count;
            project.EdgeCount = snapshot.Edges.Count;
            project.PendingFileCount = 0;
            project.IndexError = null;
            project.IndexStatus = partial
                ? ProjectIndexStatus.Partial
                : ProjectIndexStatus.Indexed;
            project.Languages = DetectLanguages(fileManifests);
            await _projects.SaveAsync(project, cancellationToken);

            stopwatch.Stop();
            Progress(
                projectId, runId, mode, "complete",
                partial ? "V3 圖譜已發布，但部分外部能力降級。" : "V3 圖譜已完成並原子發布。",
                100, stopwatch);
            _runs[projectId] = new GraphIndexRun(
                projectId, runId, mode, partial ? "partial" : "succeeded",
                startedAt, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds,
                snapshot.Nodes.Count, snapshot.Edges.Count, manifestVersion,
                snapshot.CanonicalDigest, stages);
            return project;
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(
                project, previousState, attempt, "索引已取消，上一個成功圖譜保持不變。",
                cancellationToken: CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var safeError = SafeError(exception);
            var requiresReconciliation = await RecordFailureAsync(
                project, previousState, attempt, safeError,
                cancellationToken: CancellationToken.None);
            stopwatch.Stop();
            Progress(projectId, runId, mode, "failed", safeError, 100, stopwatch);
            _runs[projectId] = new GraphIndexRun(
                projectId, runId, mode, "failed", startedAt, DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, previousState.NodeCount,
                previousState.EdgeCount, previousState.ManifestVersion, null,
                stages, safeError);
            _logger.LogError(
                requiresReconciliation
                    ? "GraphRAG V3 Neo4j 已發布，但 SQLite promote 失敗；等待自動對帳。Project={ProjectId}, ExceptionType={ExceptionType}"
                    : "GraphRAG V3 索引失敗；已保留上一個 active graph。Project={ProjectId}, ExceptionType={ExceptionType}",
                projectId,
                exception.GetType().Name);
            throw;
        }
    }

    private async Task<bool> RecordFailureAsync(
        ProjectEntity project,
        ProjectState previous,
        ProjectIndexManifest? attempt,
        string error,
        CancellationToken cancellationToken)
    {
        var publishedAttemptRequiresReconciliation = false;
        if (attempt is not null)
        {
            try
            {
                // Neo4j promote 與 SQLite promote 無法共用跨資料庫 transaction。
                // 若 Neo4j 已原子切換成功，保留先前寫入的 Fresh/Partial attempt，
                // 讓下次 diagnostics 依 active anchor 冪等修復 SQLite；不可把它覆寫成
                // Failed，否則 reconcile 永遠找不到可 promote 的 manifest。
                var active = await _graphStore.GetActiveManifestAsync(
                    project.Id, CancellationToken.None);
                publishedAttemptRequiresReconciliation = string.Equals(
                    active, attempt.Version, StringComparison.Ordinal);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "GraphRAG V3 失敗處理無法確認 Neo4j active manifest；採保守 failed 狀態。Project={ProjectId}, ExceptionType={ExceptionType}",
                    project.Id,
                    exception.GetType().Name);
            }

            if (!publishedAttemptRequiresReconciliation)
            {
                await _manifests.SaveAttemptAsync(attempt with
                {
                    CompletedAt = DateTimeOffset.UtcNow,
                    Status = IndexManifestStatus.Failed,
                    Error = error,
                    RequiresRetry = true,
                }, cancellationToken);
            }
        }
        project.IndexStatus = previous.ManifestVersion is null
            ? ProjectIndexStatus.Failed
            : ProjectIndexStatus.Stale;
        project.IndexError = error;
        project.IndexManifestVersion = previous.ManifestVersion;
        project.NodeCount = previous.NodeCount;
        project.EdgeCount = previous.EdgeCount;
        project.IndexedAt = previous.IndexedAt;
        project.IndexError = publishedAttemptRequiresReconciliation
            ? "Neo4j 已完成原子發布；SQLite manifest 將於下次診斷自動對帳。"
            : error;
        await _projects.SaveAsync(project, cancellationToken);
        return publishedAttemptRequiresReconciliation;
    }

    private async Task ReconcileManifestAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var active = await _graphStore.GetActiveManifestAsync(
            projectId, cancellationToken);
        if (active is null) return;
        var current = await _manifests.GetCurrentAsync(projectId, cancellationToken);
        if (string.Equals(current?.Version, active, StringComparison.Ordinal)) return;
        var neo4jVersion = await _manifests.GetByVersionAsync(
            projectId, active, cancellationToken);
        if (neo4jVersion is { Status: IndexManifestStatus.Fresh or IndexManifestStatus.Partial })
        {
            await _manifests.PromoteAsync(neo4jVersion, cancellationToken);
            var project = await _projects.GetAsync(projectId, cancellationToken);
            if (project is null) return;
            project.IndexManifestVersion = neo4jVersion.Version;
            project.IndexedAt = neo4jVersion.CompletedAt;
            project.NodeCount = neo4jVersion.NodeCount;
            project.EdgeCount = neo4jVersion.EdgeCount;
            project.IndexError = null;
            project.IndexStatus = neo4jVersion.Status == IndexManifestStatus.Partial
                ? ProjectIndexStatus.Partial
                : ProjectIndexStatus.Indexed;
            await _projects.SaveAsync(project, cancellationToken);
        }
    }

    private bool CanNoOp(
        ProjectIndexManifest? current,
        string? neo4jManifest,
        string workingFingerprint,
        SqlServerGraphSource? databaseSource,
        string? databaseFingerprint) =>
        _options.EnableNoOpFastPath &&
        !_options.ForceFullIndex &&
        current is { Status: IndexManifestStatus.Fresh or IndexManifestStatus.Partial } &&
        string.Equals(current.Version, neo4jManifest, StringComparison.Ordinal) &&
        string.Equals(current.WorkingTreeFingerprint, workingFingerprint, StringComparison.Ordinal) &&
        string.Equals(current.IndexerVersion, IndexerVersion, StringComparison.Ordinal) &&
        string.Equals(current.GraphSchemaVersion, GraphAssembler.SchemaVersion, StringComparison.Ordinal) &&
        (databaseSource is null || databaseFingerprint is not null);

    /// <summary>
    /// 僅在既有 C# 檔案的 raw bytes 改變、宣告 surface 完全相同且上一張 canonical snapshot
    /// 仍在本 process 中時允許 body-delta。新增、刪除、rename、route／attribute／signature、
    /// 非 C#、DB fingerprint、extractor 或 schema 任一改變都保守退回 full。
    /// </summary>
    private BodyDeltaPlan? TryPlanBodyDelta(
        ProjectIndexManifest? current,
        string? neo4jManifest,
        IReadOnlyList<IndexedFileManifest> liveFiles,
        SqlServerGraphSource? databaseSource,
        string? databaseFingerprint,
        GraphSnapshot? activeSnapshot)
    {
        if (_options.ForceFullIndex ||
            current is not { Status: IndexManifestStatus.Fresh or IndexManifestStatus.Partial } ||
            activeSnapshot is null ||
            !string.Equals(current.Version, neo4jManifest, StringComparison.Ordinal) ||
            !string.Equals(activeSnapshot.ManifestVersion, current.Version, StringComparison.Ordinal) ||
            !string.Equals(current.IndexerVersion, IndexerVersion, StringComparison.Ordinal) ||
            !string.Equals(current.GraphSchemaVersion, GraphAssembler.SchemaVersion, StringComparison.Ordinal) ||
            !DescriptorMatchesCurrentExtractors(activeSnapshot.Indexer) ||
            !DatabaseFingerprintMatches(
                activeSnapshot, databaseSource, databaseFingerprint))
            return null;

        var oldByPath = current.Files.ToDictionary(
            file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var liveByPath = liveFiles.ToDictionary(
            file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (oldByPath.Count != liveByPath.Count ||
            oldByPath.Keys.Except(liveByPath.Keys, StringComparer.OrdinalIgnoreCase).Any())
            return null;

        var changed = liveFiles.Where(file =>
                !string.Equals(
                    oldByPath[file.RelativePath].ContentHash,
                    file.ContentHash,
                    StringComparison.Ordinal))
            .ToList();
        if (changed.Count == 0 ||
            changed.Any(file =>
            {
                var old = oldByPath[file.RelativePath];
                return !string.Equals(file.Language, "csharp", StringComparison.Ordinal) ||
                       string.IsNullOrWhiteSpace(file.DeclarationHash) ||
                       !string.Equals(
                           file.DeclarationHash,
                           old.DeclarationHash,
                           StringComparison.Ordinal);
            }))
            return null;
        return new BodyDeltaPlan(
            changed.Select(file => file.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private bool DescriptorMatchesCurrentExtractors(GraphIndexerDescriptor descriptor) =>
        string.Equals(descriptor.IndexerVersion, IndexerVersion, StringComparison.Ordinal) &&
        descriptor.Extractors.Count == _extractors.Count &&
        _extractors.All(extractor =>
            descriptor.Extractors.TryGetValue(extractor.Id, out var version) &&
            string.Equals(version, extractor.Version, StringComparison.Ordinal));

    private static bool DatabaseFingerprintMatches(
        GraphSnapshot snapshot,
        SqlServerGraphSource? databaseSource,
        string? databaseFingerprint)
    {
        var databaseArtifacts = snapshot.Artifacts
            .Where(artifact => string.Equals(
                artifact.Kind, "database", StringComparison.Ordinal))
            .ToList();
        if (databaseSource is null)
            return databaseArtifacts.Count == 0;
        return databaseFingerprint is not null &&
               databaseArtifacts.Count == 1 &&
               string.Equals(
                   databaseArtifacts[0].ContentHash,
                   databaseFingerprint,
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// 將上一張 snapshot 轉回不含 C# compiler 觀察的 base fragment。
    /// DB 產生的 unresolved plugin Code 會保留；只有 artifact 為 .cs 的 Compiler／AST／Framework
    /// evidence 被移除，再與本次完整 C# fragment 合併，因此 body-delta 與 clean full 具有相同 domain graph。
    /// </summary>
    private static GraphFragment CreateNonCSharpBaseFragment(
        GraphSnapshot snapshot,
        IReadOnlySet<string> changedPaths)
    {
        var fragment = new GraphFragment();
        foreach (var node in snapshot.Nodes)
        {
            var evidence = node.Evidence.Where(item =>
                    !IsCSharpExtractorEvidence(item) &&
                    !changedPaths.Contains(
                        GraphIdentity.NormalizePath(item.Artifact)))
                .ToList();
            if (evidence.Count > 0)
                fragment.Nodes.Add(node with { Evidence = evidence });
        }
        foreach (var edge in snapshot.Edges)
        {
            var evidence = edge.Evidence.Where(item =>
                    !IsCSharpExtractorEvidence(item) &&
                    !changedPaths.Contains(
                        GraphIdentity.NormalizePath(item.Artifact)))
                .ToList();
            if (evidence.Count > 0)
                fragment.Edges.Add(edge with { Evidence = evidence });
        }
        fragment.Diagnostics.AddRange(snapshot.Diagnostics.Where(diagnostic =>
            !(diagnostic.Code.StartsWith("CSHARP_", StringComparison.Ordinal) ||
              changedPaths.Contains(
                  GraphIdentity.NormalizePath(diagnostic.Artifact)))));
        fragment.CapabilityGaps.AddRange(snapshot.CapabilityGaps);
        return fragment;
    }

    private static bool IsCSharpExtractorEvidence(GraphEvidence evidence) =>
        evidence.Artifact.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        (evidence.Source == GraphEvidenceSource.Compiler ||
         evidence.Source == GraphEvidenceSource.Framework &&
         evidence.Reason.Contains("Controller", StringComparison.Ordinal));

    private static async Task<IReadOnlyList<IndexedFileManifest>> HashFilesAsync(
        string root,
        IReadOnlyList<string> files,
        IReadOnlyList<IndexedFileManifest>? previousFiles,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<IndexedFileManifest>();
        var boundedCSharpDeclarations = files.Count(file =>
            string.Equals(
                Path.GetExtension(file), ".cs", StringComparison.OrdinalIgnoreCase)) > 2_000;
        var previousByPath = previousFiles?.ToDictionary(
            file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase) ??
            new Dictionary<string, IndexedFileManifest>(
                StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            },
            async (file, token) =>
            {
                await using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, token))
                    .ToLowerInvariant();
                var info = new FileInfo(file);
                var relativePath = GraphIdentity.NormalizePath(
                    Path.GetRelativePath(root, file));
                previousByPath.TryGetValue(relativePath, out var previous);
                var isCSharp = string.Equals(
                    Path.GetExtension(file), ".cs", StringComparison.OrdinalIgnoreCase);
                var declarationHash =
                    isCSharp &&
                    (!boundedCSharpDeclarations ||
                     CSharpGraphExtractor.IsLargeRepositoryCallPathFile(root, file))
                        ? previous is not null &&
                          string.Equals(
                              previous.ContentHash, hash, StringComparison.Ordinal) &&
                          !string.IsNullOrWhiteSpace(previous.DeclarationHash)
                            ? previous.DeclarationHash
                            : await ComputeCSharpDeclarationHashAsync(file, token)
                        : null;
                results.Add(new IndexedFileManifest(
                    relativePath,
                    LanguageForExtension(Path.GetExtension(file)),
                    info.Length,
                    hash,
                    LastWriteAt: info.LastWriteTimeUtc,
                    DeclarationHash: declarationHash));
            });
        return results.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 將 C# method／constructor／accessor body 移除後再正規化雜訊並雜湊。
    /// attribute、route、modifier、base type、interface、parameter、return type 與 member 宣告仍保留；
    /// 因此只有不改變圖譜 identity／entry surface 的實作內容才可能通過 body-delta。
    /// </summary>
    private static async Task<string> ComputeCSharpDeclarationHashAsync(
        string file,
        CancellationToken cancellationToken)
    {
        var source = await File.ReadAllTextAsync(file, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);
        var declarationOnly = new CSharpDeclarationSurfaceRewriter()
            .Visit(root)!;
        // NormalizeWhitespace 會重新建構整份近萬檔 trunk 的文字，成本高且 hash
        // 實際只需要語法 token。RawKind＋ValueText 保留 identifier、attribute、
        // signature 與 literal 的語意，同時自然忽略註解/空白；method body 已由
        // rewriter 移除，因此單純 implementation change 仍得到相同 declaration hash。
        var declarationTokens = new StringBuilder();
        foreach (var token in declarationOnly.DescendantTokens())
            declarationTokens.Append(token.RawKind)
                .Append(':')
                .Append(token.ValueText.Length)
                .Append(':')
                .Append(token.ValueText)
                .Append('\0');
        return GraphIdentity.Sha256(declarationTokens.ToString());
    }

    private IReadOnlyList<string> EnumerateSourceFiles(string root)
    {
        var normalizedRoot = Path.GetFullPath(root);
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException($"專案根目錄不存在：{normalizedRoot}");
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> childFiles;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory);
                childFiles = Directory.EnumerateFiles(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            foreach (var child in childDirectories)
            {
                var info = new DirectoryInfo(child);
                if (ExcludedDirectories.Contains(info.Name) ||
                    info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                pending.Push(child);
            }
            foreach (var file in childFiles)
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(file)) ||
                    IsTestArtifact(normalizedRoot, file))
                    continue;
                result.Add(file);
                if (result.Count > _options.MaximumFiles)
                    throw new InvalidOperationException(
                        $"專案可索引檔案超過安全上限 {_options.MaximumFiles}；請確認 RootPath 是否正確。");
            }
        }
        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsTestArtifact(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var segments = relative.Split('/');
        var fileName = Path.GetFileName(relative);
        return segments.Any(segment =>
                   segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("__tests__", StringComparison.OrdinalIgnoreCase)) ||
               fileName.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".spec.js", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase);
    }

    private static string WorkingFingerprint(
        IReadOnlyList<IndexedFileManifest> files,
        string? databaseFingerprint)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
            builder.Append(file.RelativePath).Append('\0')
                .Append(file.Length).Append('\0')
                .Append(file.ContentHash).Append('\n');
        builder.Append("db\0").Append(databaseFingerprint ?? "unavailable");
        return GraphIdentity.Sha256(builder.ToString());
    }

    private static string DetectLanguages(IReadOnlyList<IndexedFileManifest> files) =>
        string.Join(',', files.Select(item => item.Language)
            .Where(value => value is "csharp" or "java" or "frontend" or "sql")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));

    private static string LanguageForExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".java" => "java",
            ".js" or ".jsx" or ".ts" or ".tsx" => "frontend",
            ".sql" => "sql",
            _ => "unknown",
        };

    private static string? HeadCommit(string root)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", "-C . rev-parse HEAD")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            if (!process.WaitForExit(2_000) || process.ExitCode != 0) return null;
            var value = process.StandardOutput.ReadToEnd().Trim();
            return value.Length == 40 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeError(Exception exception) => exception switch
    {
        DirectoryNotFoundException => "專案根目錄不存在，上一個成功圖譜保持不變。",
        UnauthorizedAccessException => "部分專案檔案無法讀取，索引已中止且上一個成功圖譜保持不變。",
        InvalidOperationException => "GraphRAG 品質閘門或發布驗證失敗，上一個成功圖譜保持不變。",
        _ => "GraphRAG 索引失敗，上一個成功圖譜保持不變。",
    };

    private static bool IsInsideRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static ProjectState CaptureProjectState(ProjectEntity project) =>
        new(
            project.IndexManifestVersion,
            project.NodeCount,
            project.EdgeCount,
            project.IndexedAt);

    private IReadOnlyList<string> PendingFiles(string projectId) =>
        _pendingFiles.TryGetValue(projectId, out var values)
            ? values.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    private async Task<ProjectEntity> RequiredProjectAsync(
        string projectId,
        CancellationToken cancellationToken) =>
        await _projects.GetAsync(projectId, cancellationToken) ??
        throw new InvalidOperationException($"專案不存在：{projectId}");

    private void Progress(
        string projectId,
        string runId,
        string mode,
        string phase,
        string message,
        int percent,
        Stopwatch stopwatch) =>
        _progress[projectId] = new GraphIndexProgress(
            projectId,
            phase,
            message,
            Math.Clamp(percent, 0, 100),
            runId,
            mode,
            stopwatch.ElapsedMilliseconds);

    private static void ValidateOptions(GraphIndexingOptions options)
    {
        if (options.WatcherDebounceMilliseconds is < 100 or > 30_000)
            throw new InvalidOperationException(
                "GraphRAG WatcherDebounceMilliseconds 必須介於 100 到 30000。");
        if (options.MaximumFiles is < 1_000 or > 1_000_000)
            throw new InvalidOperationException(
                "GraphRAG MaximumFiles 必須介於 1000 到 1000000。");
    }

    private sealed record ProjectState(
        string? ManifestVersion,
        int NodeCount,
        int EdgeCount,
        DateTimeOffset? IndexedAt);

    private sealed record BodyDeltaPlan(
        IReadOnlySet<string> ChangedPaths);

    /// <summary>
    /// 只移除可安全視為 implementation body 的語法；宣告、attribute 與所有 public surface 均保留。
    /// 這個 rewriter 只用於 eligibility hash，不參與 graph extraction。
    /// </summary>
    private sealed class CSharpDeclarationSurfaceRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            base.VisitMethodDeclaration(node.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            base.VisitConstructorDeclaration(node.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

        public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node) =>
            base.VisitDestructorDeclaration(node.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) =>
            base.VisitOperatorDeclaration(node.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

        public override SyntaxNode? VisitConversionOperatorDeclaration(
            ConversionOperatorDeclarationSyntax node) =>
            base.VisitConversionOperatorDeclaration(
                node.WithBody(null).WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node) =>
            base.VisitAccessorDeclaration(node.WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
            base.VisitPropertyDeclaration(node.WithExpressionBody(null)
                .WithSemicolonToken(node.AccessorList is null
                    ? SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                    : default));

        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node) =>
            base.VisitIndexerDeclaration(node.WithExpressionBody(null)
                .WithSemicolonToken(node.AccessorList is null
                    ? SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                    : default));
    }
}

/// <summary>
/// 監看已註冊專案的可索引檔案並觸發 V3 incremental 決策。
/// Watcher 只標記 pending；真正的 hash、驗證與原子發布永遠由 <see cref="GraphIndexingService"/> 執行。
/// </summary>
public sealed class GraphIndexWatcherService(
    IProjectRepository projects,
    GraphIndexingService indexing,
    IOptions<GraphIndexingOptions> options,
    ILogger<GraphIndexWatcherService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounces =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var allProjects = await projects.ListAsync(stoppingToken);
            var activeIds = allProjects.Select(project => project.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var project in allProjects)
            {
                if (_watchers.ContainsKey(project.Id) || !Directory.Exists(project.RootPath))
                    continue;
                var watcher = new FileSystemWatcher(project.RootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += (_, args) => Schedule(project.Id, args.FullPath, stoppingToken);
                watcher.Created += (_, args) => Schedule(project.Id, args.FullPath, stoppingToken);
                watcher.Deleted += (_, args) => Schedule(project.Id, args.FullPath, stoppingToken);
                watcher.Renamed += (_, args) => Schedule(project.Id, args.FullPath, stoppingToken);
                if (!_watchers.TryAdd(project.Id, watcher)) watcher.Dispose();
            }
            foreach (var stale in _watchers.Keys.Where(id => !activeIds.Contains(id)).ToList())
                if (_watchers.TryRemove(stale, out var watcher)) watcher.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private void Schedule(
        string projectId,
        string path,
        CancellationToken stoppingToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".cs" or ".java" or ".js" or ".jsx" or ".ts" or ".tsx" or ".sql"))
            return;
        if (_debounces.TryRemove(projectId, out var prior))
        {
            prior.Cancel();
            prior.Dispose();
        }
        var debounce = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
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
                    "GraphRAG watcher 增量索引失敗。Project={ProjectId}, ExceptionType={ExceptionType}",
                    projectId, exception.GetType().Name);
            }
            finally
            {
                if (_debounces.TryGetValue(projectId, out var current) &&
                    ReferenceEquals(current, debounce))
                    _debounces.TryRemove(projectId, out _);
                debounce.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        foreach (var debounce in _debounces.Values)
        {
            debounce.Cancel();
            debounce.Dispose();
        }
        base.Dispose();
    }
}
