using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.CodeGraph;

public sealed class ProjectIndexOptimizationOptions
{
    public const string SectionName = "ProjectIndexOptimization";
    public bool EnableNoOpFastPath { get; set; } = true;
    public bool ForceFullIndex { get; set; }
    public bool EnableBodyOnlyIncremental { get; set; } = true;
}

/// <summary>索引進度事件（前端 FTUE 進度視覺化用，P1）。</summary>
public sealed record IndexProgressEvent(
    string ProjectId, string Phase, string Message, int Percent,
    string? RunId = null,
    string? Mode = null,
    long ElapsedMilliseconds = 0,
    IReadOnlyDictionary<string, long>? StageDurationsMilliseconds = null);

/// <summary>
/// 專案索引管線（WS3.2）：
///   掃描檔案 → 語言分析器（Strategy）→ Neo4j 寫入 → 社群偵測 → LLM 摘要。
///
/// 檔案變更偵測：以 git diff（HEAD 對 working tree + 未追蹤檔案）決定是否需要重建。
/// </summary>
public sealed class ProjectIndexService(
    IEnumerable<ICodeAnalyzer> analyzers,
    ICodeGraphStore graphStore,
    IProjectRepository projectRepository,
    IProjectIndexManifestStore manifestStore,
    IDataSchemaExtractor dataSchemaExtractor,
    IDomainGlossaryStore glossaryStore,
    Neo4jLifecycleService neo4jLifecycle,
    ILogger<ProjectIndexService> logger,
    IOptions<ProjectIndexOptimizationOptions> optimizationOptions)
{
    private static readonly string[] ExcludedDirs =
        ["bin", "obj", "node_modules", "target", ".git", ".vs", "dist", "build", "out", "packages"];

    private readonly ConcurrentDictionary<string, IndexProgressEvent> _progress = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _pendingFiles = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexGates = new();
    private readonly ConcurrentDictionary<string, DataScanReport> _dataReports = new();
    private readonly ConcurrentDictionary<string, IndexRunTelemetry> _runs = new();
    private readonly ConcurrentDictionary<string, GraphSnapshotV2> _activeSnapshots = new();
    private static readonly string IndexerVersion =
        typeof(ProjectIndexService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ProjectIndexService).Assembly.GetName().Version?.ToString()
        ?? "development";

    /// <summary>取得目前索引進度（輪詢用）。</summary>
    public IndexProgressEvent? GetProgress(string projectId) =>
        _progress.TryGetValue(projectId, out var p) ? p : null;

    public DataScanReport? GetLastDataScanReport(string projectId) =>
        _dataReports.TryGetValue(projectId, out var report) ? report : null;

    public IndexRunTelemetry? GetLastRun(string projectId) =>
        _runs.TryGetValue(projectId, out var run) ? run : null;

    internal GraphSnapshotV2? GetActiveSnapshotForDiagnostics(string projectId) =>
        _activeSnapshots.TryGetValue(projectId, out var snapshot) ? snapshot : null;

    public void ForgetProjectState(string projectId)
    {
        _activeSnapshots.TryRemove(projectId, out _);
        _pendingFiles.TryRemove(projectId, out _);
        _progress.TryRemove(projectId, out _);
        _dataReports.TryRemove(projectId, out _);
        _runs.TryRemove(projectId, out _);
    }

    /// <summary>檔案 watcher 偵測到可索引變更時標記；不會覆寫進行中的索引。</summary>
    public async Task MarkPendingChangesAsync(
        string projectId,
        string? fullPath = null,
        CancellationToken ct = default)
    {
        var project = await projectRepository.GetAsync(projectId, ct);
        if (project is null)
            return;

        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            var relativePath = Path.GetRelativePath(project.RootPath, fullPath)
                .Replace('\\', '/');
            _pendingFiles
                .GetOrAdd(projectId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))
                [relativePath] = 0;
        }

        if (project.IndexStatus != ProjectIndexStatus.Indexing)
            project.IndexStatus = ProjectIndexStatus.PendingChanges;
        project.PendingFileCount = GetPendingFiles(projectId).Count;
        project.IndexError = null;
        await projectRepository.SaveAsync(project, ct);
    }

    public async Task<ProjectIndexDiagnostics> GetDiagnosticsAsync(
        string projectId, CancellationToken ct = default)
    {
        await ReconcileManifestStateAsync(projectId, ct);
        var current = await manifestStore.GetCurrentAsync(projectId, ct);
        var latest = await manifestStore.GetLatestAttemptAsync(projectId, ct);
        var pending = GetPendingFiles(projectId);
        return new ProjectIndexDiagnostics(
            current,
            latest,
            pending,
            pending.Count > 0 || latest is { Status: IndexManifestStatus.Failed or IndexManifestStatus.Stale });
    }

    /// <summary>Watcher 啟動或分析 session 建立時，以內容 hash 補捉漏掉的檔案事件。</summary>
    public async Task<bool> CatchUpAsync(string projectId, CancellationToken ct = default)
    {
        var project = await projectRepository.GetAsync(projectId, ct)
            ?? throw new InvalidOperationException($"專案不存在: {projectId}");
        await ReconcileManifestStateAsync(projectId, ct);
        var current = await manifestStore.GetCurrentAsync(projectId, ct);
        if (current is null)
            return false;

        var currentByPath = current.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var liveFiles = EnumerateManifestFiles(project.RootPath)
            .ToDictionary(
                file => Path.GetRelativePath(project.RootPath, file).Replace('\\', '/'),
                file => file,
                StringComparer.OrdinalIgnoreCase);
        // Catch-up is the safety net for missed/coalesced watcher events. It must not
        // inherit filesystem timestamp weaknesses, so prove equality with exact hashes.
        var hashedLiveFiles = await BuildFileManifestAsync(
            project.RootPath,
            liveFiles.Values.ToList(),
            ct);
        var changed = hashedLiveFiles
            .Where(file => !currentByPath.TryGetValue(file.RelativePath, out var old) || old.ContentHash != file.ContentHash)
            .Select(file => file.RelativePath)
            .Concat(currentByPath.Keys.Except(liveFiles.Keys, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (changed.Count == 0)
            return false;

        foreach (var relativePath in changed)
            await MarkPendingChangesAsync(projectId, Path.Combine(project.RootPath, relativePath), ct);
        return true;
    }

    /// <summary>全量索引專案。</summary>
    public async Task<ProjectEntity> IndexProjectAsync(
        string projectId, CancellationToken ct = default)
    {
        var gate = _indexGates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await IndexProjectCoreAsync(projectId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProjectEntity> IndexProjectCoreAsync(
        string projectId,
        CancellationToken ct,
        string? scopeEscalationReason = null)
    {
        await ReconcileManifestStateAsync(projectId, ct);
        var project = await projectRepository.GetAsync(projectId, ct)
            ?? throw new InvalidOperationException($"專案不存在: {projectId}");
        var version = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var runClock = Stopwatch.StartNew();
        var stageClock = Stopwatch.StartNew();
        var stageDurations = new Dictionary<string, long>(StringComparer.Ordinal);
        string? activePhase = null;
        var runNodeCount = 0;
        var runEdgeCount = 0;
        string? runSnapshotHash = null;
        var runMode = "full";
        ProjectIndexManifest attempt = new(
            projectId, version, Path.GetFullPath(project.RootPath), null, "pending",
            [], [], GetPendingFiles(projectId), IndexerVersion, startedAt, null,
            IndexManifestStatus.Indexing);

        void Report(string phase, string message, int percent)
        {
            if (!string.Equals(activePhase, phase, StringComparison.Ordinal))
            {
                if (activePhase is not null)
                    stageDurations[activePhase] = stageDurations.GetValueOrDefault(activePhase) + stageClock.ElapsedMilliseconds;
                activePhase = phase;
                stageClock.Restart();
            }
            var timings = new Dictionary<string, long>(stageDurations, StringComparer.Ordinal);
            var evt = new IndexProgressEvent(
                projectId, phase, message, percent, version, runMode,
                runClock.ElapsedMilliseconds, timings);
            _progress[projectId] = evt;
            var terminal = phase is "done" or "failed" or "canceled";
            _runs[projectId] = new IndexRunTelemetry(
                projectId,
                version,
                runMode,
                phase,
                phase switch
                {
                    "done" => "ready",
                    "failed" => "failed",
                    "canceled" => "canceled",
                    _ => "running",
                },
                startedAt,
                terminal ? DateTimeOffset.UtcNow : null,
                runClock.ElapsedMilliseconds,
                timings,
                runNodeCount,
                runEdgeCount,
                GraphSchemaV2.Version,
                runSnapshotHash,
                ScopeEscalationReason: scopeEscalationReason,
                ErrorCategory: phase is "failed" or "canceled" ? phase : null,
                Error: phase is "failed" or "canceled" ? message : null);
            logger.LogInformation("[Index {ProjectId}] {Phase} {Percent}% — {Message}",
                projectId, phase, percent, message);
        }

        try
        {
            project.IndexStatus = ProjectIndexStatus.Indexing;
            project.IndexError = null;
            await projectRepository.SaveAsync(project, ct);
            await manifestStore.SaveAttemptAsync(attempt, ct);

            // ── 0. Neo4j 可用性 ────────────────────────────────────────────
            Report("neo4j", "確認 Neo4j 可用...", 5);
            var progress = new Progress<string>(msg => Report("neo4j", msg, 5));
            if (!await neo4jLifecycle.EnsureAvailableAsync(progress, ct))
                throw new InvalidOperationException(
                    neo4jLifecycle.LastError ??
                    "Neo4j 無法啟動。請檢查設定或將離線安裝包放入指定目錄。");

            // ── 1. 掃描檔案 ────────────────────────────────────────────────
            Report("scan", "掃描專案檔案...", 10);
            var filesByLanguage = ScanFiles(project.RootPath);
            var languages = filesByLanguage.Keys.ToList();
            project.Languages = string.Join(",", languages);

            if (languages.Count == 0)
                throw new InvalidOperationException("未找到支援的原始碼檔案（.cs / .java）");

            var currentManifest = await manifestStore.GetCurrentAsync(projectId, ct);
            var allFiles = filesByLanguage.SelectMany(pair => pair.Value).ToList();
            // A no-op is a correctness claim, not a timestamp heuristic. Hash every
            // tracked artifact so same-size content with a restored mtime cannot reuse
            // a stale graph.
            var fileManifest = await BuildFileManifestAsync(project.RootPath, allFiles, ct);
            var supportFiles = EnumerateManifestFiles(project.RootPath)
                .Except(allFiles, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToList();
            var supportManifest = (await BuildFileManifestAsync(
                project.RootPath, supportFiles, ct))
                .Select(file => file with
                {
                    Language = Path.GetExtension(file.RelativePath).TrimStart('.').ToLowerInvariant(),
                    Status = "Tracked",
                })
                .ToList();
            var unreadable = fileManifest.Concat(supportManifest)
                .Where(file => string.Equals(file.Status, "Skipped", StringComparison.OrdinalIgnoreCase) ||
                               string.IsNullOrWhiteSpace(file.ContentHash))
                .Select(file => $"{file.RelativePath}: {file.Reason ?? "read failed"}")
                .ToList();
            if (unreadable.Count > 0)
                throw new IOException(
                    "索引輸入無法完整讀取；保留上一個成功圖譜。" + Environment.NewLine +
                    string.Join(Environment.NewLine, unreadable));
            var git = GetGitSnapshot(project.RootPath, fileManifest);
            attempt = new ProjectIndexManifest(
                projectId, version, Path.GetFullPath(project.RootPath), git.HeadCommit,
                git.WorkingTreeFingerprint, git.UntrackedFiles, fileManifest,
                GetPendingFiles(projectId), IndexerVersion, startedAt, null,
                IndexManifestStatus.Indexing);
            attempt = attempt with { Files = fileManifest.Concat(supportManifest).ToList() };
            await manifestStore.SaveAttemptAsync(attempt, ct);

            if (optimizationOptions.Value.EnableNoOpFastPath &&
                currentManifest is not null &&
                (currentManifest.Status == IndexManifestStatus.Fresh ||
                 currentManifest.Status == IndexManifestStatus.Partial && currentManifest.RequiresRetry == false) &&
                !string.IsNullOrWhiteSpace(currentManifest.AnalysisSnapshotHash) &&
                string.Equals(currentManifest.IndexerVersion, IndexerVersion, StringComparison.Ordinal) &&
                string.Equals(currentManifest.GraphSchemaVersion, GraphSchemaV2.Version, StringComparison.Ordinal) &&
                HaveSameArtifactInputs(currentManifest.Files, attempt.Files))
            {
                runMode = "no-op";
                runNodeCount = currentManifest.NodeCount;
                runEdgeCount = currentManifest.EdgeCount;
                runSnapshotHash = currentManifest.AnalysisSnapshotHash;
                var completedAt = DateTimeOffset.UtcNow;
                await manifestStore.SaveAttemptAsync(attempt with
                {
                    CompletedAt = completedAt,
                    Status = currentManifest.Status,
                    NodeCount = currentManifest.NodeCount,
                    EdgeCount = currentManifest.EdgeCount,
                    Error = currentManifest.Error,
                    GraphSchemaVersion = GraphSchemaV2.Version,
                    AnalysisSnapshotHash = currentManifest.AnalysisSnapshotHash,
                    IndexMode = "no-op",
                    RequiresRetry = currentManifest.RequiresRetry,
                }, ct);
                project.NodeCount = currentManifest.NodeCount;
                project.EdgeCount = currentManifest.EdgeCount;
                project.IndexStatus = currentManifest.Status == IndexManifestStatus.Partial
                    ? ProjectIndexStatus.Partial
                    : ProjectIndexStatus.Indexed;
                project.IndexManifestVersion = currentManifest.Version;
                project.IndexedAt = currentManifest.CompletedAt;
                project.PendingFileCount = 0;
                project.IndexError = currentManifest.Error;
                _pendingFiles.TryRemove(projectId, out _);
                await projectRepository.SaveAsync(project, ct);
                Report("done", $"索引內容未變，沿用 {currentManifest.NodeCount} 節點、{currentManifest.EdgeCount} 關係", 100);
                return project;
            }

            // ── 2. 先完成所有分析，再以單一 Neo4j transaction 切換版本 ──────
            Report("analyze", "分析程式語意...", 20);
            var componentDurations = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            var dataAnalysisTask = MeasureAnalysisAsync(
                "analyze:data",
                () => dataSchemaExtractor.ExtractAsync(project.RootPath, ct));
            var glossaryTask = glossaryStore.ListAsync(projectId, GlossaryProposalStatus.Confirmed, ct);
            var analysisTasks = filesByLanguage.Select(async pair =>
            {
                ct.ThrowIfCancellationRequested();
                var (language, files) = pair;
                var analyzer = analyzers.FirstOrDefault(a => a.Language == language);
                if (analyzer is null)
                    return new CodeAnalysisResult();

                var sw = Stopwatch.StartNew();
                var result = await analyzer.AnalyzeAsync(project.RootPath, files, ct);
                componentDurations[$"analyze:{language}"] = sw.ElapsedMilliseconds;
                logger.LogInformation("{Language} 分析耗時 {Elapsed}ms", language, sw.ElapsedMilliseconds);
                return result;
            }).ToList();
            var analyzed = await Task.WhenAll(analysisTasks);
            var dataAnalysis = await dataAnalysisTask;
            var confirmedTerms = await glossaryTask;
            Report("canonicalize", "合成並驗證 canonical graph...", 60);
            foreach (var duration in componentDurations)
                stageDurations[duration.Key] = duration.Value;
            _dataReports[projectId] = new DataScanReport(
                dataAnalysis.Graph.Nodes.Count,
                dataAnalysis.Graph.Edges.Count,
                dataAnalysis.Diagnostics,
                dataAnalysis.CapabilityGaps,
                dataAnalysis.ScannedFiles ?? [],
                dataAnalysis.SkippedFiles ?? []);
            var rawGraph = new CodeAnalysisResult();
            foreach (var result in analyzed.Append(dataAnalysis.Graph))
            {
                foreach (var node in result.Nodes)
                    rawGraph.Nodes.Add(node);
                foreach (var edge in result.Edges)
                    rawGraph.Edges.Add(edge);
            }
            var nodeKeys = rawGraph.Nodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
            var edgeKeys = rawGraph.Edges
                .Select(edge => $"{edge.SourceKey}\0{edge.Kind}\0{edge.TargetKey}")
                .ToHashSet(StringComparer.Ordinal);
            AppendGlossaryGraph(rawGraph, nodeKeys, edgeKeys, confirmedTerms);

            var dataFiles = ToIndexedFileManifests(project.RootPath, dataAnalysis, fileManifest);
            var hasDataGaps = dataAnalysis.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(diagnostic.Severity, "warning", StringComparison.OrdinalIgnoreCase));
            var hasRetryableDataErrors = dataAnalysis.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase));
            var indexedFiles = fileManifest.Concat(dataFiles).Concat(supportManifest)
                .DistinctBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var profile = new GraphAnalysisProfile(
                IndexerVersion,
                GraphSchemaV2.Version,
                analyzers
                    .Select(analyzer => analyzer.GetType())
                    .Append(dataSchemaExtractor.GetType())
                    .Distinct()
                    .Select(type => new GraphAnalyzerIdentity(
                        type.FullName ?? type.Name,
                        type.Assembly.GetName().Version?.ToString() ?? "development"))
                    .ToList());
            var snapshot = GraphSnapshotCanonicalizer.Create(
                projectId,
                version,
                startedAt,
                profile,
                git.WorkingTreeFingerprint,
                "full",
                indexedFiles,
                rawGraph,
                dataAnalysis.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Severity}:{diagnostic.AdapterId}:{diagnostic.FilePath}:{diagnostic.Message}").ToList(),
                dataAnalysis.CapabilityGaps,
                git.HeadCommit,
                hasDataGaps ? "ready-with-data-gaps" : "ready");
            var aggregate = GraphSnapshotCanonicalizer.ToAnalysisResult(snapshot);
            runNodeCount = aggregate.Nodes.Count;
            runEdgeCount = aggregate.Edges.Count;
            runSnapshotHash = snapshot.Snapshot.AnalysisSnapshotHash;
            attempt = attempt with
            {
                NodeCount = aggregate.Nodes.Count,
                EdgeCount = aggregate.Edges.Count,
                Files = indexedFiles,
                Error = hasDataGaps
                    ? "部分資料結構 artifact 無法解析；程式碼圖譜仍可使用。"
                    : null,
                GraphSchemaVersion = GraphSchemaV2.Version,
                AnalysisSnapshotHash = snapshot.Snapshot.AnalysisSnapshotHash,
                IndexMode = snapshot.Snapshot.Mode,
                RequiresRetry = hasRetryableDataErrors,
            };
            await manifestStore.SaveAttemptAsync(attempt, ct);

            Report("store", $"分批建立新圖譜並原子切換版本（{aggregate.Nodes.Count} 節點）...", 70);
            await graphStore.ReplaceProjectAsync(
                projectId,
                new GraphPublishDescriptor(
                    version,
                    version,
                    snapshot.SchemaVersion,
                    snapshot.Snapshot.AnalysisSnapshotHash,
                    snapshot.Snapshot.NodeCount,
                    snapshot.Snapshot.EdgeCount),
                aggregate,
                ct);

            // ── 3. 統計 ────────────────────────────────────────────────────
            Report("stats", "提交索引 manifest...", 90);
            var nodes = aggregate.Nodes.Count;
            var edges = aggregate.Edges.Count;
            project.NodeCount = nodes;
            project.EdgeCount = edges;
            project.IndexStatus = ProjectIndexStatus.Indexed;
            project.IndexedAt = DateTimeOffset.UtcNow;
            project.IndexManifestVersion = version;
            project.PendingFileCount = 0;
            project.IndexError = null;
            _pendingFiles.TryRemove(projectId, out _);
            var completed = attempt with
            {
                CompletedAt = project.IndexedAt,
                Status = hasDataGaps ? IndexManifestStatus.Partial : IndexManifestStatus.Fresh,
                NodeCount = nodes,
                EdgeCount = edges,
                PendingFiles = Array.Empty<string>(),
                Files = indexedFiles,
            };
            if (hasDataGaps)
            {
                project.IndexStatus = ProjectIndexStatus.Partial;
                project.IndexError = "部分資料結構 artifact 無法解析；程式碼圖譜仍可使用。";
            }
            await manifestStore.PromoteAsync(completed, ct);
            await projectRepository.SaveAsync(project, ct);
            _activeSnapshots[projectId] = snapshot;

            Report("done", $"索引完成：{nodes} 節點、{edges} 關係", 100);
            return project;

            async Task<T> MeasureAnalysisAsync<T>(string name, Func<Task<T>> action)
            {
                var clock = Stopwatch.StartNew();
                try { return await action(); }
                finally { componentDurations[name] = clock.ElapsedMilliseconds; }
            }
        }
        catch (OperationCanceledException)
        {
            await RecordFailedAttemptAsync(attempt, "索引已取消", CancellationToken.None);
            project.IndexStatus = project.IndexManifestVersion is null
                ? ProjectIndexStatus.Failed
                : ProjectIndexStatus.Stale;
            project.IndexError = "索引已取消；上一個成功版本仍可使用。";
            await projectRepository.SaveAsync(project, CancellationToken.None);
            Report("canceled", project.IndexError, 100);
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailedAttemptAsync(attempt, ex.Message, CancellationToken.None);
            project.IndexStatus = project.IndexManifestVersion is null
                ? ProjectIndexStatus.Failed
                : ProjectIndexStatus.Stale;
            project.IndexError = ex.Message;
            await projectRepository.SaveAsync(project, CancellationToken.None);
            Report("failed", ex.Message, 100);
            throw;
        }
    }

    private async Task RecordFailedAttemptAsync(
        ProjectIndexManifest? attempt, string error, CancellationToken ct)
    {
        if (attempt is null) return;
        await manifestStore.SaveAttemptAsync(attempt with
        {
            Status = IndexManifestStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            Error = error,
        }, ct);
    }

    /// <summary>
    /// Neo4j 與 SQLite 無法共享 distributed transaction。若圖譜交易已提交、但程序在
    /// SQLite promote 前中斷，下一次查詢會利用 Neo4j manifestVersion 與已保存 attempt
    /// 完成冪等復原；不會把舊 manifest 套在新圖譜上。
    /// </summary>
    private async Task ReconcileManifestStateAsync(string projectId, CancellationToken ct)
    {
        try
        {
            var graphVersion = await graphStore.GetProjectManifestVersionAsync(projectId, ct);
            var current = await manifestStore.GetCurrentAsync(projectId, ct);
            var project = await projectRepository.GetAsync(projectId, ct);
            if (project is null) return;
            if (string.IsNullOrWhiteSpace(graphVersion))
            {
                if (current is not null || !string.IsNullOrWhiteSpace(project.IndexManifestVersion))
                {
                    project.IndexStatus = ProjectIndexStatus.Stale;
                    project.IndexError = "SQLite manifest 存在，但 Neo4j active graph 遺失或不一致；請重新索引。";
                    await projectRepository.SaveAsync(project, ct);
                }
                return;
            }

            if (string.Equals(current?.Version, graphVersion, StringComparison.Ordinal))
            {
                if (!string.Equals(project.IndexManifestVersion, graphVersion, StringComparison.Ordinal))
                {
                    project.IndexManifestVersion = graphVersion;
                    project.NodeCount = current!.NodeCount;
                    project.EdgeCount = current.EdgeCount;
                    project.IndexedAt = current.CompletedAt;
                    project.IndexStatus = current.Status == IndexManifestStatus.Partial
                        ? ProjectIndexStatus.Partial
                        : ProjectIndexStatus.Indexed;
                    await projectRepository.SaveAsync(project, ct);
                }
                return;
            }

            var publishedAttempt = await manifestStore.GetByVersionAsync(projectId, graphVersion, ct);
            if (publishedAttempt is null)
            {
                project.IndexStatus = ProjectIndexStatus.Stale;
                project.IndexError = "圖譜版本與 Index Manifest 不一致，請重新索引。";
                await projectRepository.SaveAsync(project, ct);
                return;
            }

            var (nodes, edges) = await graphStore.GetStatsAsync(projectId, ct);
            var recovered = publishedAttempt with
            {
                Status = string.IsNullOrWhiteSpace(publishedAttempt.Error)
                    ? IndexManifestStatus.Fresh
                    : IndexManifestStatus.Partial,
                CompletedAt = publishedAttempt.CompletedAt ?? DateTimeOffset.UtcNow,
                NodeCount = nodes,
                EdgeCount = edges,
                PendingFiles = Array.Empty<string>(),
            };
            await manifestStore.PromoteAsync(recovered, ct);
            project.IndexManifestVersion = recovered.Version;
            project.NodeCount = nodes;
            project.EdgeCount = edges;
            project.IndexedAt = recovered.CompletedAt;
            project.IndexStatus = recovered.Status == IndexManifestStatus.Partial
                ? ProjectIndexStatus.Partial
                : ProjectIndexStatus.Indexed;
            project.IndexError = recovered.Error;
            project.PendingFileCount = 0;
            await projectRepository.SaveAsync(project, ct);
            logger.LogWarning(
                "已復原 Neo4j/SQLite 索引提交: project={ProjectId}, manifest={ManifestVersion}",
                projectId, graphVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "無法對齊專案 {ProjectId} 的 Neo4j/SQLite manifest", projectId);
        }
    }

    /// <summary>
    /// 變更後重建索引。
    ///
    /// 目前所有 analyzer 都可能建立「未修改檔案 → 修改檔案」的跨檔案邊。
    /// 直接刪除變更檔案子圖會 DETACH 掉這些 caller 邊，而只重解析變更檔案無法補回，
    /// 因此會產生低估 blast radius 的危險假陰性。P0 先採保守全量重建，等各 analyzer
    /// 能提供完整的反向依賴 closure 後，才可安全改回真正的 incremental re-index。
    ///
    /// 回傳 null 表示 working tree 無可索引的變更。
    /// </summary>
    public async Task<ProjectEntity?> IncrementalIndexAsync(
        string projectId, CancellationToken ct = default)
    {
        var gate = _indexGates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await ReconcileManifestStateAsync(projectId, ct);
            var project = await projectRepository.GetAsync(projectId, ct)
                ?? throw new InvalidOperationException($"專案不存在: {projectId}");
            var current = await manifestStore.GetCurrentAsync(projectId, ct);
            if (current is null || optimizationOptions.Value.ForceFullIndex)
                return await IndexProjectCoreAsync(
                    projectId,
                    ct,
                    current is null ? "No current manifest is available." : "ForceFullIndex kill switch is enabled.");

            var changes = await DetectArtifactChangesAsync(project, current, ct);
            if (changes.ChangedPaths.Count == 0)
            {
                _pendingFiles.TryRemove(projectId, out _);
                project.PendingFileCount = 0;
                project.IndexStatus = current.Status == IndexManifestStatus.Partial
                    ? ProjectIndexStatus.Partial
                    : ProjectIndexStatus.Indexed;
                await projectRepository.SaveAsync(project, ct);
                logger.LogInformation("專案 {ProjectId} 內容無變更，跳過增量索引", projectId);
                return null;
            }

            _activeSnapshots.TryGetValue(projectId, out var activeSnapshot);
            var bodyOnlyCandidate = optimizationOptions.Value.EnableBodyOnlyIncremental &&
                changes.ChangedPaths.Count <= 200 &&
                current.Status == IndexManifestStatus.Fresh &&
                string.Equals(current.GraphSchemaVersion, GraphSchemaV2.Version, StringComparison.Ordinal) &&
                activeSnapshot is not null &&
                activeSnapshot.Diagnostics.Count == 0 &&
                string.Equals(
                    activeSnapshot.Snapshot.AnalysisSnapshotHash,
                    current.AnalysisSnapshotHash,
                    StringComparison.Ordinal) &&
                changes.ChangedPaths.All(path =>
                    Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
                    changes.PreviousByPath.ContainsKey(path) &&
                    changes.LiveByPath.ContainsKey(path));

            if (bodyOnlyCandidate)
            {
                var incremental = await TryIndexBodyDeltaAsync(
                    project,
                    current,
                    activeSnapshot!,
                    changes,
                    ct);
                if (incremental is not null)
                    return incremental;
            }

            var escalationReason = bodyOnlyCandidate
                ? "C# body delta safety gate rejected the change; a clean full rebuild is required."
                : "Change is outside the eligible Fresh, gap-free, existing C# body-only delta scope.";
            logger.LogInformation(
                "偵測到 {Count} 個變更，增量安全門檻未通過；升級完整重建: {Reason}",
                changes.ChangedPaths.Count,
                escalationReason);
            return await IndexProjectCoreAsync(projectId, ct, escalationReason);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProjectEntity?> TryIndexBodyDeltaAsync(
        ProjectEntity project,
        ProjectIndexManifest current,
        GraphSnapshotV2 activeSnapshot,
        ArtifactChangeSet changes,
        CancellationToken ct)
    {
        var roslyn = analyzers.OfType<RoslynCodeAnalyzer>().SingleOrDefault();
        if (roslyn is null) return null;
        var startedAt = DateTimeOffset.UtcNow;
        var version = Guid.NewGuid().ToString("N");
        var totalClock = Stopwatch.StartNew();
        if (!await neo4jLifecycle.EnsureAvailableAsync(null, ct))
            throw new InvalidOperationException("Neo4j 無法啟動");

        var changedAbsolutePaths = changes.ChangedPaths
            .Select(path => Path.Combine(project.RootPath, path))
            .ToList();
        var allCSharpHashes = changes.UpdatedFiles
            .Where(file => file.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file.RelativePath, file => file.ContentHash, StringComparer.OrdinalIgnoreCase);
        var activeNodeKeys = activeSnapshot.Nodes
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        _progress[project.Id] = new IndexProgressEvent(
            project.Id,
            "analyze",
            $"增量分析 {changes.ChangedPaths.Count} 個 C# 檔案...",
            25,
            version,
            "incremental-body",
            totalClock.ElapsedMilliseconds);
        var analysisClock = Stopwatch.StartNew();
        var code = await roslyn.AnalyzeBodyChangesAsync(
            project.RootPath,
            changedAbsolutePaths,
            allCSharpHashes,
            activeNodeKeys,
            ct);
        if (!code.Applied || code.Graph is null)
        {
            logger.LogInformation(
                "C# 增量安全門檻未通過，升級完整重建: {Reason}",
                code.EscalationReason);
            return null;
        }

        var data = await dataSchemaExtractor.ExtractFilesAsync(
            project.RootPath,
            changedAbsolutePaths,
            ct);
        analysisClock.Stop();
        var raw = new CodeAnalysisResult();
        foreach (var graph in new[] { code.Graph, data.Graph })
        {
            raw.Nodes.AddRange(graph.Nodes);
            raw.Edges.AddRange(graph.Edges);
        }

        // Fragment canonicalization needs both endpoints, while the fragment itself only
        // owns nodes produced by changed artifacts. Add base placeholders solely for
        // validation, then filter them back out before composition.
        var fragmentNodeKeys = raw.Nodes.Select(node => node.Key)
            .ToHashSet(StringComparer.Ordinal);
        var basePublished = GraphSnapshotCanonicalizer.ToAnalysisResult(activeSnapshot);
        var baseNodesByKey = basePublished.Nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        foreach (var key in raw.Edges
            .SelectMany(edge => new[] { edge.SourceKey, edge.TargetKey })
            .Where(key => !fragmentNodeKeys.Contains(key))
            .Distinct(StringComparer.Ordinal))
        {
            if (!baseNodesByKey.TryGetValue(key, out var placeholder))
            {
                logger.LogWarning("增量 fragment 產生未知 endpoint {NodeKey}，升級完整重建", key);
                return null;
            }
            raw.Nodes.Add(placeholder);
        }

        var sourceFiles = changes.UpdatedFiles
            .Where(file => file.Language is "csharp" or "java")
            .ToList();
        var git = GetGitSnapshot(project.RootPath, sourceFiles);
        GraphSnapshotV2 fragmentSnapshot;
        try
        {
            fragmentSnapshot = GraphSnapshotCanonicalizer.Create(
                project.Id,
                version,
                startedAt,
                activeSnapshot.AnalysisProfile,
                git.WorkingTreeFingerprint,
                "incremental-fragment",
                changes.UpdatedFiles,
                raw,
                data.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Severity}:{diagnostic.AdapterId}:{diagnostic.FilePath}:{diagnostic.Message}").ToList(),
                data.CapabilityGaps,
                git.HeadCommit);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "增量 fragment canonicalization 失敗，升級完整重建");
            return null;
        }

        var rawEdgeIds = raw.Edges.Select(edge =>
                $"{edge.SourceKey}\0{edge.Kind}\0{edge.TargetKey}\0forward")
            .ToHashSet(StringComparer.Ordinal);
        var fragment = new GraphSnapshotFragmentV2(
            fragmentSnapshot.Nodes.Where(node => fragmentNodeKeys.Contains(node.Id)).ToList(),
            fragmentSnapshot.Edges.Where(edge =>
                rawEdgeIds.Contains($"{edge.SourceId}\0{edge.Kind}\0{edge.TargetId}\0forward")).ToList(),
            fragmentSnapshot.Diagnostics,
            fragmentSnapshot.CapabilityGaps);
        var changedArtifactIds = changes.ChangedPaths
            .Select(path => $"file:{path.Replace('\\', '/')}")
            .ToList();

        GraphSnapshotV2 composed;
        try
        {
            composed = GraphSnapshotDeltaComposer.Compose(
                activeSnapshot,
                changedArtifactIds,
                fragment,
                version,
                startedAt,
                git.WorkingTreeFingerprint,
                "incremental-body",
                fragmentSnapshot.Artifacts,
                headCommit: git.HeadCommit,
                status: data.Diagnostics.Any(item =>
                    string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase))
                    ? "ready-with-data-gaps"
                    : "ready");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "增量 snapshot composition 失敗，升級完整重建");
            return null;
        }

        var finalPublished = GraphSnapshotCanonicalizer.ToAnalysisResult(composed);
        var delta = BuildPublishDelta(current.Version, activeSnapshot, composed, finalPublished);
        var descriptor = new GraphPublishDescriptor(
            version,
            version,
            composed.SchemaVersion,
            composed.Snapshot.AnalysisSnapshotHash,
            composed.Snapshot.NodeCount,
            composed.Snapshot.EdgeCount);
        var attempt = current with
        {
            Version = version,
            RepositoryRoot = Path.GetFullPath(project.RootPath),
            HeadCommit = git.HeadCommit,
            WorkingTreeFingerprint = git.WorkingTreeFingerprint,
            UntrackedFiles = git.UntrackedFiles,
            Files = changes.UpdatedFiles,
            PendingFiles = changes.ChangedPaths,
            StartedAt = startedAt,
            CompletedAt = null,
            Status = IndexManifestStatus.Indexing,
            NodeCount = composed.Snapshot.NodeCount,
            EdgeCount = composed.Snapshot.EdgeCount,
            Error = null,
            GraphSchemaVersion = composed.SchemaVersion,
            AnalysisSnapshotHash = composed.Snapshot.AnalysisSnapshotHash,
            IndexMode = composed.Snapshot.Mode,
        };

        project.IndexStatus = ProjectIndexStatus.Indexing;
        project.IndexError = null;
        await projectRepository.SaveAsync(project, ct);
        await manifestStore.SaveAttemptAsync(attempt, ct);
        var publishClock = Stopwatch.StartNew();
        try
        {
            await graphStore.ApplyProjectDeltaAsync(project.Id, descriptor, delta, ct);
        }
        catch (NotSupportedException ex)
        {
            await RecordFailedAttemptAsync(attempt, ex.Message, CancellationToken.None);
            logger.LogInformation("Graph store 不支援原子 delta，升級完整重建");
            return null;
        }
        catch (Exception ex)
        {
            await RecordFailedAttemptAsync(attempt, ex.Message, CancellationToken.None);
            project.IndexStatus = ProjectIndexStatus.Stale;
            project.IndexError = $"增量索引失敗；上一個成功版本仍可使用。{ex.Message}";
            await projectRepository.SaveAsync(project, CancellationToken.None);
            throw;
        }
        publishClock.Stop();

        project.NodeCount = composed.Snapshot.NodeCount;
        project.EdgeCount = composed.Snapshot.EdgeCount;
        project.IndexStatus = ProjectIndexStatus.Indexed;
        project.IndexedAt = DateTimeOffset.UtcNow;
        project.IndexManifestVersion = version;
        project.PendingFileCount = 0;
        project.IndexError = null;
        var completed = attempt with
        {
            CompletedAt = project.IndexedAt,
            Status = IndexManifestStatus.Fresh,
            PendingFiles = [],
        };
        await manifestStore.PromoteAsync(completed, ct);
        await projectRepository.SaveAsync(project, ct);
        _activeSnapshots[project.Id] = composed;
        _pendingFiles.TryRemove(project.Id, out _);

        totalClock.Stop();
        var elapsed = totalClock.ElapsedMilliseconds;
        var timings = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["analyze"] = analysisClock.ElapsedMilliseconds,
            ["store"] = publishClock.ElapsedMilliseconds,
        };
        _runs[project.Id] = new IndexRunTelemetry(
            project.Id,
            version,
            "incremental-body",
            "done",
            "ready",
            startedAt,
            project.IndexedAt,
            elapsed,
            timings,
            project.NodeCount,
            project.EdgeCount,
            composed.SchemaVersion,
            composed.Snapshot.AnalysisSnapshotHash);
        _progress[project.Id] = new IndexProgressEvent(
            project.Id,
            "done",
            $"增量索引完成：{changes.ChangedPaths.Count} 個檔案",
            100,
            version,
            "incremental-body",
            elapsed,
            timings);
        return project;
    }

    private static GraphPublishDelta BuildPublishDelta(
        string baseManifestVersion,
        GraphSnapshotV2 before,
        GraphSnapshotV2 after,
        CodeAnalysisResult afterPublished)
    {
        var beforeNodes = before.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var afterNodes = after.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var beforeEdges = before.Edges.ToDictionary(
            edge => new GraphEdgeIdentity(edge.SourceId, edge.Kind, edge.TargetId));
        var afterEdges = after.Edges.ToDictionary(
            edge => new GraphEdgeIdentity(edge.SourceId, edge.Kind, edge.TargetId));
        var publishedNodes = afterPublished.Nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var publishedEdges = afterPublished.Edges.ToDictionary(
            edge => new GraphEdgeIdentity(edge.SourceKey, edge.Kind, edge.TargetKey));

        var removedNodes = beforeNodes.Keys.Except(afterNodes.Keys, StringComparer.Ordinal).ToList();
        var upsertNodes = afterNodes
            .Where(pair => !beforeNodes.TryGetValue(pair.Key, out var old) ||
                !CanonicalEquals(old, pair.Value))
            .Select(pair => publishedNodes[pair.Key])
            .ToList();
        var removedEdges = beforeEdges.Keys.Except(afterEdges.Keys).ToList();
        var upsertEdges = afterEdges
            .Where(pair => !beforeEdges.TryGetValue(pair.Key, out var old) ||
                !CanonicalEquals(old, pair.Value))
            .Select(pair => publishedEdges[pair.Key])
            .ToList();
        return new GraphPublishDelta(
            baseManifestVersion,
            removedNodes,
            upsertNodes,
            removedEdges,
            upsertEdges);

        static bool CanonicalEquals<T>(T left, T right) => string.Equals(
            JsonSerializer.Serialize(left),
            JsonSerializer.Serialize(right),
            StringComparison.Ordinal);
    }

    // ── 檔案掃描 ──────────────────────────────────────────────────────────────

    internal Dictionary<string, IReadOnlyList<string>> ScanFiles(string rootPath)
    {
        var result = new Dictionary<string, List<string>>();
        var extToLang = analyzers
            .SelectMany(a => a.FileExtensions.Select(ext => (ext, a.Language)))
            .ToDictionary(x => x.ext, x => x.Language, StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateSourceFiles(rootPath))
        {
            var ext = Path.GetExtension(file);
            if (extToLang.TryGetValue(ext, out var lang))
            {
                if (!result.TryGetValue(lang, out var list))
                    result[lang] = list = [];
                list.Add(file);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var dirName = Path.GetFileName(dir);
            if (ExcludedDirs.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                continue;

            IEnumerable<string> files;
            IEnumerable<string> subdirs;
            try
            {
                files = Directory.EnumerateFiles(dir).ToArray();
                subdirs = Directory.EnumerateDirectories(dir).ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var f in files)
                yield return f;
            foreach (var d in subdirs)
                stack.Push(d);
        }
    }

    private IReadOnlyList<string> GetPendingFiles(string projectId) =>
        _pendingFiles.TryGetValue(projectId, out var files)
            ? files.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    private static async Task<IReadOnlyList<IndexedFileManifest>> BuildFileManifestAsync(
        string rootPath,
        IReadOnlyList<string> files,
        CancellationToken ct)
    {
        var results = new ConcurrentBag<IndexedFileManifest>();
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Math.Min(Environment.ProcessorCount, 8)),
            CancellationToken = ct,
        }, async (file, token) =>
        {
            try
            {
                var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                var info = new FileInfo(file);
                await using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await SHA256.HashDataAsync(stream, token);
                results.Add(new IndexedFileManifest(
                    relativePath,
                    Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? "csharp" : "java",
                    stream.Length,
                    Convert.ToHexString(hash).ToLowerInvariant(),
                    LastWriteAt: File.GetLastWriteTimeUtc(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new IndexedFileManifest(
                    Path.GetRelativePath(rootPath, file).Replace('\\', '/'),
                    "unknown", 0, "", "Skipped", ex.Message));
            }
        });
        return results.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool HaveSameArtifactInputs(
        IReadOnlyList<IndexedFileManifest> expected,
        IReadOnlyList<IndexedFileManifest> actual)
    {
        if (expected.Count != actual.Count) return false;
        var expectedByPath = expected.ToDictionary(
            file => file.RelativePath,
            file => file,
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in actual)
        {
            if (!expectedByPath.TryGetValue(file.RelativePath, out var old) ||
                old.Length != file.Length ||
                !string.Equals(old.ContentHash, file.ContentHash, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private async Task<ArtifactChangeSet> DetectArtifactChangesAsync(
        ProjectEntity project,
        ProjectIndexManifest current,
        CancellationToken ct)
    {
        var livePaths = EnumerateManifestFiles(project.RootPath).ToList();
        // Incremental correctness uses exact content hashes, even when size/mtime are
        // unchanged. The scan is bounded and parallel; nopCommerce is below one second.
        var live = await BuildFileManifestAsync(project.RootPath, livePaths, ct);
        var liveByPath = live.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var currentByPath = current.Files.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var changed = currentByPath.Keys
            .Union(liveByPath.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path =>
                !currentByPath.TryGetValue(path, out var old) ||
                !liveByPath.TryGetValue(path, out var candidate) ||
                old.Length != candidate.Length ||
                !string.Equals(old.ContentHash, candidate.ContentHash, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var updated = new List<IndexedFileManifest>(live.Count);
        foreach (var candidate in live)
        {
            if (currentByPath.TryGetValue(candidate.RelativePath, out var old))
            {
                updated.Add(old with
                {
                    Length = candidate.Length,
                    ContentHash = candidate.ContentHash,
                    LastWriteAt = candidate.LastWriteAt,
                });
            }
            else
            {
                updated.Add(candidate);
            }
        }
        return new ArtifactChangeSet(changed, updated, currentByPath, liveByPath);
    }

    private sealed record ArtifactChangeSet(
        IReadOnlyList<string> ChangedPaths,
        IReadOnlyList<IndexedFileManifest> UpdatedFiles,
        IReadOnlyDictionary<string, IndexedFileManifest> PreviousByPath,
        IReadOnlyDictionary<string, IndexedFileManifest> LiveByPath);

    private IEnumerable<string> EnumerateManifestFiles(string rootPath)
    {
        var supported = analyzers.SelectMany(analyzer => analyzer.FileExtensions)
            .Concat([".sql", ".xml", ".csproj", ".sln", ".slnx", ".props", ".targets",
                     ".json", ".yaml", ".yml", ".properties", ".gradle", ".kts"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return EnumerateSourceFiles(rootPath)
            .Where(file => supported.Contains(Path.GetExtension(file)));
    }

    private static long TryGetFileLength(string rootPath, string relativePath)
    {
        try { return new FileInfo(Path.Combine(rootPath, relativePath)).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<IndexedFileManifest> ToIndexedFileManifests(
        string rootPath,
        DataExtractionResult dataAnalysis,
        IReadOnlyList<IndexedFileManifest> sourceFiles) =>
        (dataAnalysis.ScannedFiles ?? [])
            .Concat(dataAnalysis.SkippedFiles ?? [])
            .Where(item => sourceFiles.All(existing =>
                !string.Equals(existing.RelativePath, item.Path, StringComparison.OrdinalIgnoreCase)))
            .Select(item => new IndexedFileManifest(
                item.Path,
                item.Technology,
                TryGetFileLength(rootPath, item.Path),
                item.ContentHash,
                string.Equals(item.Status, "indexed", StringComparison.OrdinalIgnoreCase) ? "Indexed" : "Skipped",
                item.Reason,
                File.GetLastWriteTimeUtc(Path.Combine(rootPath, item.Path))))
            .ToList();

    private static void AppendGlossaryGraph(
        CodeAnalysisResult graph,
        ISet<string> nodeKeys,
        ISet<string> edgeKeys,
        IReadOnlyList<DomainGlossaryEntry> terms)
    {
        var indexedAt = DateTimeOffset.UtcNow;
        foreach (var term in terms)
        {
            var termKey = $"domain-term:{term.Id}";
            var contentHash = StableHash($"{term.Term}\n{term.Definition}\n{string.Join('|', term.Aliases)}");
            if (nodeKeys.Add(termKey))
            {
                graph.Nodes.Add(new CodeNode
                {
                    Key = termKey,
                    Kind = CodeNodeKind.DomainTerm,
                    Name = term.Term,
                    Signature = term.Definition,
                    Language = "domain",
                    Technology = "wingman-glossary",
                    SourceKind = GraphSourceKind.ItConfirmed,
                    Confidence = GraphConfidence.Confirmed,
                    ExtractorId = "wingman-domain-glossary",
                    ExtractorVersion = "1",
                    IndexedAt = indexedAt,
                    ContentHash = contentHash,
                    Reason = $"IT confirmed by {term.ReviewedBy}",
                });
            }

            foreach (var evidenceKey in term.EvidenceKeys.Where(nodeKeys.Contains))
                AddGlossaryEdge(graph, edgeKeys, termKey, evidenceKey, CodeEdgeKind.SupportedBy, indexedAt, contentHash);
            foreach (var alias in term.Aliases)
            {
                var aliasKey = $"domain-term-alias:{StableHash($"{term.Id}:{alias}")[..16]}";
                if (nodeKeys.Add(aliasKey))
                {
                    graph.Nodes.Add(new CodeNode
                    {
                        Key = aliasKey,
                        Kind = CodeNodeKind.DomainTerm,
                        Name = alias,
                        Language = "domain",
                        Technology = "wingman-glossary",
                        SourceKind = GraphSourceKind.ItConfirmed,
                        Confidence = GraphConfidence.Confirmed,
                        ExtractorId = "wingman-domain-glossary",
                        ExtractorVersion = "1",
                        IndexedAt = indexedAt,
                        ContentHash = contentHash,
                    });
                }
                AddGlossaryEdge(graph, edgeKeys, aliasKey, termKey, CodeEdgeKind.Aliases, indexedAt, contentHash);
            }
        }
    }

    private static void AddGlossaryEdge(
        CodeAnalysisResult graph,
        ISet<string> edgeKeys,
        string source,
        string target,
        CodeEdgeKind kind,
        DateTimeOffset indexedAt,
        string contentHash)
    {
        if (!edgeKeys.Add($"{source}\0{kind}\0{target}")) return;
        graph.Edges.Add(new CodeEdge
        {
            SourceKey = source,
            TargetKey = target,
            Kind = kind,
            SourceKind = GraphSourceKind.ItConfirmed,
            Confidence = GraphConfidence.Confirmed,
            ExtractorId = "wingman-domain-glossary",
            ExtractorVersion = "1",
            IndexedAt = indexedAt,
            ContentHash = contentHash,
        });
    }

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string? GetLanguageForFile(string file)
    {
        var ext = Path.GetExtension(file);
        return analyzers.FirstOrDefault(a =>
            a.FileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))?.Language;
    }

    // ── Git diff ──────────────────────────────────────────────────────────────

    /// <summary>取得 git 變更檔案（staged + unstaged + untracked），相對路徑。</summary>
    internal static List<string> GetGitChangedFiles(string repoRoot)
    {
        var output = RunGit(repoRoot, "status --porcelain=v1");
        if (output is null)
            return [];
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line[3..].Trim().Trim('"') : "")
            .Select(path => path.Contains(" -> ", StringComparison.Ordinal)
                ? path[(path.IndexOf(" -> ", StringComparison.Ordinal) + 4)..]
                : path)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static GitSnapshot GetGitSnapshot(
        string repoRoot, IReadOnlyList<IndexedFileManifest> files)
    {
        var head = RunGit(repoRoot, "rev-parse HEAD")?.Trim();
        var status = RunGit(repoRoot, "status --porcelain=v1 -z --untracked-files=all") ?? "";
        var entries = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var untracked = entries
            .Where(entry => entry.StartsWith("?? ", StringComparison.Ordinal))
            .Select(entry => entry[3..].Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        using var sha = SHA256.Create();
        var fingerprintInput = new StringBuilder(status.Length + files.Count * 96);
        fingerprintInput.Append(status);
        foreach (var file in files)
            fingerprintInput.Append('\n').Append(file.RelativePath).Append(':').Append(file.ContentHash);
        var fingerprint = Convert.ToHexString(
            sha.ComputeHash(Encoding.UTF8.GetBytes(fingerprintInput.ToString())))
            .ToLowerInvariant();
        return new GitSnapshot(
            string.IsNullOrWhiteSpace(head) ? null : head,
            fingerprint,
            untracked);
    }

    private static string? RunGit(string repoRoot, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record GitSnapshot(
        string? HeadCommit,
        string WorkingTreeFingerprint,
        IReadOnlyList<string> UntrackedFiles);
}
