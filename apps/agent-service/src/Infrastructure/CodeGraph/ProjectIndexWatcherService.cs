using System.Collections.Concurrent;
using AgentService.Application.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.CodeGraph;

/// <summary>
/// 保持已納管 workspace 的 Code Graph 新鮮。以 OS 檔案事件快速偵測變更，
/// 再以 debounce 合併連續儲存；每 30 秒對齊 project 清單，支援執行期新增／移除專案。
///
/// 此服務只觀察，不直接解析檔案。實際重建永遠交給 <see cref="ProjectIndexService"/>
/// 以統一 Neo4j lifecycle、錯誤處理與正確性策略。
/// </summary>
public sealed class ProjectIndexWatcherService(
    IProjectRepository projects,
    ProjectIndexService indexService,
    ILogger<ProjectIndexWatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly string[] RelevantExtensions =
        [".cs", ".java", ".csproj", ".sln", ".slnx", ".props", ".targets",
         ".json", ".yaml", ".yml", ".xml", ".properties", ".gradle", ".kts", ".sql"];
    private static readonly string[] IgnoredDirectoryNames =
        [".git", ".vs", "bin", "obj", "node_modules", "target", "dist", "build", "out", "packages"];

    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectGates = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReconcileWatchersAsync(stoppingToken);
                await Task.Delay(ReconcileInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止。
        }
    }

    private async Task ReconcileWatchersAsync(CancellationToken ct)
    {
        var active = await projects.ListAsync(ct);
        var activeIds = active.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var stale in _watchers.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            if (_watchers.TryRemove(stale, out var watcher))
                watcher.Dispose();
        }

        foreach (var project in active)
        {
            if (_watchers.ContainsKey(project.Id) || !Directory.Exists(project.RootPath))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(project.RootPath)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    Filter = "*.*",
                };
                watcher.Changed += (_, args) => Queue(project.Id, project.RootPath, args.FullPath);
                watcher.Created += (_, args) => Queue(project.Id, project.RootPath, args.FullPath);
                watcher.Deleted += (_, args) => Queue(project.Id, project.RootPath, args.FullPath);
                watcher.Renamed += (_, args) =>
                {
                    Queue(project.Id, project.RootPath, args.OldFullPath);
                    Queue(project.Id, project.RootPath, args.FullPath);
                };
                watcher.Error += (_, args) =>
                {
                    if (_watchers.TryRemove(project.Id, out var failed))
                        failed.Dispose();
                    logger.LogWarning(
                        args.GetException(),
                        "專案 {ProjectId} 的檔案 watcher 發生錯誤；下次對齊時會重新建立",
                        project.Id);
                };

                if (!_watchers.TryAdd(project.Id, watcher))
                    watcher.Dispose();
                else
                {
                    logger.LogInformation("已啟用專案索引 watcher: {ProjectId}", project.Id);
                    if (await indexService.CatchUpAsync(project.Id, ct))
                        Schedule(project.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "無法建立專案索引 watcher: {ProjectId}", project.Id);
            }
        }
    }

    private void Queue(string projectId, string projectRoot, string fullPath)
    {
        if (!IsRelevantSourcePath(fullPath, projectRoot))
            return;

        _ = indexService.MarkPendingChangesAsync(projectId, fullPath);

        Schedule(projectId);
    }

    private void Schedule(string projectId)
    {
        var cts = new CancellationTokenSource();
        _debounces.AddOrUpdate(projectId, cts, (_, existing) =>
        {
            existing.Cancel();
            return cts;
        });
        _ = DebounceAndReindexAsync(projectId, cts);
    }

    private async Task DebounceAndReindexAsync(string projectId, CancellationTokenSource cts)
    {
        try
        {
            await indexService.MarkPendingChangesAsync(projectId, null, cts.Token);
            await Task.Delay(Debounce, cts.Token);

            var gate = _projectGates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cts.Token);
            try
            {
                // FileSystemWatcher also covers folders that are not Git repositories.  Do not
                // route this path through git status: a non-Git workspace would otherwise remain
                // permanently stale after a source-file save.
                await indexService.IncrementalIndexAsync(projectId, cts.Token);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 新一輪儲存會重設 debounce。
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "自動重建專案索引失敗: {ProjectId}", projectId);
        }
        finally
        {
            if (_debounces.TryGetValue(projectId, out var current) && ReferenceEquals(current, cts))
            {
                _debounces.TryRemove(projectId, out _);
                cts.Dispose();
            }
        }
    }

    internal static bool IsRelevantSourcePath(string path, string? projectRoot = null)
    {
        if (!RelevantExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return false;

        var scopedPath = path;
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var root = Path.GetFullPath(projectRoot);
            var candidate = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, candidate);
            if (Path.IsPathRooted(relative) ||
                relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                return false;
            scopedPath = relative;
        }

        var segments = scopedPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !segments.Any(segment => IgnoredDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    public override void Dispose()
    {
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        foreach (var cts in _debounces.Values)
            cts.Cancel();
        foreach (var gate in _projectGates.Values)
            gate.Dispose();
        base.Dispose();
    }
}
