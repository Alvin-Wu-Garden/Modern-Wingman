using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 單筆 Community AI 摘要工作。Work 只負責一次模型呼叫與 CAS 寫回；
/// timeout、重試、去重、並行及狀態統計由佇列統一控制。
/// </summary>
/// <param name="ProjectId">Modern Wingman 專案 ID。</param>
/// <param name="GraphVersion">工作所屬 immutable graph version。</param>
/// <param name="CommunityId">C0/C1/C2 community ID。</param>
/// <param name="CacheKey">結構內容與 prompt version 的穩定去重鍵。</param>
/// <param name="Work">執行單次摘要並以 cacheKey CAS 寫回的工作。</param>
/// <param name="InitialRetryCount">
/// 工作排入前已經消耗的 retry 次數。Host 重啟時由 persisted report 還原，
/// 避免每次重啟都重新取得兩次 retry 配額。
/// </param>
/// <param name="OnTerminalFailure">
/// 非 Host 關機造成的失敗耗盡重試時才呼叫，用來持久化 failed template。
/// Host cancellation 不會執行此 callback，讓 queued/running 可在重啟後續跑。
/// </param>
public sealed record GraphCommunitySummaryJob(
    string ProjectId,
    string GraphVersion,
    string CommunityId,
    string CacheKey,
    Func<CancellationToken, Task> Work,
    int InitialRetryCount = 0,
    Func<int, CancellationToken, Task>? OnTerminalFailure = null);

/// <summary>右下角 UI 與 Progress API 共用的背景摘要統計。</summary>
public sealed record GraphCommunitySummaryProgress(
    string ProjectId,
    int Total,
    int Queued,
    int Running,
    int Completed,
    int Failed,
    int Percent,
    bool StructuralIndexAvailable,
    string? Message = null);

/// <summary>Community AI 背景佇列的最小契約。</summary>
public interface IGraphCommunitySummaryQueue
{
    /// <summary>
    /// 排入尚未完成且未在執行的 cache key；重複或超過重試上限時回傳 false。
    /// 本方法不等待 AI，確保 publish 與檢索不被摘要阻塞。
    /// </summary>
    bool TryEnqueue(GraphCommunitySummaryJob job);

    /// <summary>
    /// 切換目前 Graph version 並標記結構索引可用性。
    /// Progress 只統計此版本，舊工作即使稍後完成也會被 graphVersion/cacheKey CAS 拒絕。
    /// </summary>
    void ActivateGraphVersion(
        string projectId,
        string graphVersion,
        bool structuralIndexAvailable);

    /// <summary>
    /// 由 active graph 的 persisted community reports 還原 terminal progress。
    /// 預設只啟用版本，讓簡化測試替身與既有外掛不必實作持久化恢復。
    /// </summary>
    void RestoreGraphVersion(
        string projectId,
        string graphVersion,
        bool structuralIndexAvailable,
        IReadOnlyList<GraphCommunityReportV4> reports) =>
        ActivateGraphVersion(projectId, graphVersion, structuralIndexAvailable);

    /// <summary>清除已刪除專案的背景狀態；預設 no-op 讓既有測試替身保持相容。</summary>
    void ForgetProject(string projectId)
    {
    }

    /// <summary>取得指定專案的即時 bounded queue 統計。</summary>
    GraphCommunitySummaryProgress GetProgress(string projectId);
}

/// <summary>
/// 有界、全域兩工人、每專案單工人的 Community AI 背景佇列。
/// 每筆工作 45 秒 timeout，失敗最多重試兩次；失敗只保留 template，不影響 Graph 可查詢性。
/// </summary>
public sealed class GraphCommunitySummaryQueue(
    ILogger<GraphCommunitySummaryQueue> logger)
    : BackgroundService, IGraphCommunitySummaryQueue
{
    private const int QueueCapacity = 512;
    private const int GlobalConcurrency = 2;
    private const int MaximumRetries = 2;
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(45);

    private readonly Channel<QueuedJob> _queue =
        Channel.CreateBounded<QueuedJob>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    private readonly ConcurrentDictionary<string, JobState> _states =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectGates =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _structuralAvailability =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeGraphVersions =
        new(StringComparer.Ordinal);

    /// <summary>測試與 diagnostics 使用的目前工作狀態筆數。</summary>
    internal int TrackedJobCount => _states.Count;

    /// <summary>測試與 diagnostics 使用的目前 per-project gate 數。</summary>
    internal int ProjectGateCount => _projectGates.Count;

    /// <inheritdoc />
    public bool TryEnqueue(GraphCommunitySummaryJob job)
    {
        ValidateJob(job);
        var key = JobKey(job);
        while (true)
        {
            if (_states.TryGetValue(key, out var existing))
            {
                if (existing.State is QueueState.Queued or QueueState.Running or QueueState.Completed)
                    return false;
                if (existing.RetryCount >= MaximumRetries)
                    return false;
            }

            var next = new JobState(
                job,
                QueueState.Queued,
                existing?.RetryCount ?? job.InitialRetryCount);
            if (existing is null)
            {
                if (!_states.TryAdd(key, next))
                    continue;
            }
            else if (!_states.TryUpdate(key, next, existing))
            {
                continue;
            }

            if (_queue.Writer.TryWrite(new QueuedJob(key, job)))
                return true;

            _states.TryUpdate(
                key,
                next with { State = QueueState.Failed },
                next);
            logger.LogWarning(
                "Community AI queue 已滿，保留 deterministic template。Project={ProjectId}, Community={CommunityId}",
                job.ProjectId,
                job.CommunityId);
            return false;
        }
    }

    /// <inheritdoc />
    public void ActivateGraphVersion(
        string projectId,
        string graphVersion,
        bool structuralIndexAvailable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphVersion);
        _activeGraphVersions[projectId] = graphVersion;
        _structuralAvailability[projectId] = structuralIndexAvailable;
        RemoveHistoricalStates(projectId, graphVersion);
    }

    /// <inheritdoc />
    public void RestoreGraphVersion(
        string projectId,
        string graphVersion,
        bool structuralIndexAvailable,
        IReadOnlyList<GraphCommunityReportV4> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ActivateGraphVersion(projectId, graphVersion, structuralIndexAvailable);

        foreach (var report in reports)
        {
            var terminalState = report.SummaryState switch
            {
                GraphCommunitySummaryStates.AiReady => QueueState.Completed,
                GraphCommunitySummaryStates.Failed
                    when report.RetryCount >= MaximumRetries => QueueState.Failed,
                _ => (QueueState?)null,
            };
            if (terminalState is null)
                continue;

            // Terminal report 已存在於 active immutable graph；placeholder 永遠不會執行，
            // 只用來讓重啟後的 UI total/completed/failed 與 persisted state 一致。
            var job = new GraphCommunitySummaryJob(
                projectId,
                graphVersion,
                report.CommunityId,
                report.CacheKey,
                _ => Task.CompletedTask,
                Math.Clamp(report.RetryCount, 0, MaximumRetries));
            var key = JobKey(job);
            _states.TryAdd(
                key,
                new JobState(job, terminalState.Value, job.InitialRetryCount));
        }
    }

    /// <inheritdoc />
    public GraphCommunitySummaryProgress GetProgress(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var activeVersion = _activeGraphVersions.GetValueOrDefault(projectId);
        var states = _states.Values
            .Where(value => string.Equals(
                value.Job.ProjectId,
                projectId,
                StringComparison.Ordinal))
            .Where(value => activeVersion is null || string.Equals(
                value.Job.GraphVersion,
                activeVersion,
                StringComparison.Ordinal))
            .ToList();
        var total = states.Count;
        var queued = states.Count(value => value.State == QueueState.Queued);
        var running = states.Count(value => value.State == QueueState.Running);
        var completed = states.Count(value => value.State == QueueState.Completed);
        var failed = states.Count(value => value.State == QueueState.Failed);
        var finished = completed + failed;
        var percent = total == 0
            ? 100
            : Math.Clamp((int)Math.Round(finished * 100d / total), 0, 100);
        return new GraphCommunitySummaryProgress(
            projectId,
            total,
            queued,
            running,
            completed,
            failed,
            percent,
            _structuralAvailability.GetValueOrDefault(projectId),
            failed > 0
                ? $"{failed} 個 AI 摘要失敗；結構模板仍可使用。"
                : queued + running > 0
                    ? $"AI 摘要背景補充中 {completed}/{total}"
                    : "AI 摘要背景工作完成。");
    }

    /// <inheritdoc />
    public void ForgetProject(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var hadRunningJob = _states.Values.Any(state =>
            string.Equals(
                state.Job.ProjectId,
                projectId,
                StringComparison.Ordinal) &&
            state.State == QueueState.Running);
        _activeGraphVersions.TryRemove(projectId, out _);
        _structuralAvailability.TryRemove(projectId, out _);
        foreach (var state in _states.Where(pair => string.Equals(
                     pair.Value.Job.ProjectId,
                     projectId,
                     StringComparison.Ordinal)))
            _states.TryRemove(state.Key, out _);
        if (!hadRunningJob)
            TryRemoveProjectGateIfIdle(projectId);
    }

    /// <summary>啟動固定兩個 worker；BackgroundService 關機取消不記為摘要失敗。</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, GlobalConcurrency)
            .Select(_ => ConsumeAsync(stoppingToken)));

    /// <summary>逐筆消費工作，並以 project gate 保證同一專案並行數不超過一。</summary>
    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
                await ExecuteJobAsync(item, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host 正常關機；queued/running 狀態由持久化 template 在下次啟動重新排程。
        }
    }

    /// <summary>執行單筆工作；失敗時最多重新排入兩次，最後保留 failed template。</summary>
    private async Task ExecuteJobAsync(QueuedJob item, CancellationToken stoppingToken)
    {
        if (!_states.TryGetValue(item.Key, out var queued) ||
            queued.State != QueueState.Queued)
            return;

        var gate = _projectGates.GetOrAdd(
            item.Job.ProjectId,
            _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(stoppingToken);
        try
        {
            if (!_states.TryUpdate(
                    item.Key,
                    queued with { State = QueueState.Running },
                    queued))
                return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(JobTimeout);
            await item.Job.Work(timeout.Token);
            UpdateState(item.Key, QueueState.Completed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            UpdateState(item.Key, QueueState.Queued);
        }
        catch (Exception exception)
        {
            // 切換 active graph 或刪除專案時，舊工作的 state 可能已被清除。
            // 此時結果本來就應捨棄，不能用索引器拋例外讓整個 consumer 停止。
            if (!_states.TryGetValue(item.Key, out var current))
                return;
            var retryCount = current.RetryCount + 1;
            var retry = retryCount <= MaximumRetries;
            var next = current with
            {
                State = retry ? QueueState.Queued : QueueState.Failed,
                RetryCount = retryCount,
            };
            _states[item.Key] = next;
            logger.LogWarning(
                "Community AI 摘要失敗。Project={ProjectId}, Community={CommunityId}, Retry={Retry}, ExceptionType={ExceptionType}",
                item.Job.ProjectId,
                item.Job.CommunityId,
                retryCount,
                exception.GetType().Name);
            if (!retry)
            {
                await NotifyTerminalFailureAsync(
                    item.Job,
                    Math.Min(MaximumRetries, retryCount));
            }
            else if (!_queue.Writer.TryWrite(item))
            {
                _states[item.Key] = next with { State = QueueState.Failed };
                await NotifyTerminalFailureAsync(
                    item.Job,
                    Math.Min(MaximumRetries, retryCount));
            }
        }
        finally
        {
            gate.Release();
            TryRemoveProjectGateIfIdle(item.Job.ProjectId);
        }
    }

    /// <summary>以不可變更新寫入 terminal 或重新排程狀態。</summary>
    private void UpdateState(string key, QueueState state)
    {
        while (_states.TryGetValue(key, out var current))
        {
            if (_states.TryUpdate(key, current with { State = state }, current))
                return;
        }
    }

    /// <summary>驗證工作不含空白 identity，避免不同空值錯誤共用 cache key。</summary>
    private static void ValidateJob(GraphCommunitySummaryJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.GraphVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.CommunityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.CacheKey);
        ArgumentNullException.ThrowIfNull(job.Work);
        if (job.InitialRetryCount is < 0 or > MaximumRetries)
            throw new ArgumentOutOfRangeException(
                nameof(job),
                $"InitialRetryCount 必須介於 0 與 {MaximumRetries}。");
    }

    /// <summary>
    /// active graph 切版時移除同專案舊版狀態。Channel 中尚未取出的舊工作因找不到
    /// state 會安全略過；正在執行者仍由 graphVersion/cacheKey CAS 拒絕舊結果。
    /// </summary>
    private void RemoveHistoricalStates(string projectId, string activeGraphVersion)
    {
        foreach (var state in _states.Where(pair =>
                     string.Equals(
                         pair.Value.Job.ProjectId,
                         projectId,
                         StringComparison.Ordinal) &&
                     !string.Equals(
                         pair.Value.Job.GraphVersion,
                         activeGraphVersion,
                         StringComparison.Ordinal)))
            _states.TryRemove(state.Key, out _);
    }

    /// <summary>
    /// 只有不存在 queued/running 工作時才移除 project gate。Semaphore 不主動 Dispose，
    /// 避免剛完成工作的 finally 或已取得參考的 waiter 發生 ObjectDisposedException。
    /// </summary>
    private void TryRemoveProjectGateIfIdle(string projectId)
    {
        if (_states.Values.Any(state =>
                string.Equals(
                    state.Job.ProjectId,
                    projectId,
                    StringComparison.Ordinal) &&
                state.State is QueueState.Queued or QueueState.Running))
            return;
        _projectGates.TryRemove(projectId, out _);
    }

    /// <summary>持久化最終 failed template；callback 自身失敗只記型別，不中斷 worker。</summary>
    private async Task NotifyTerminalFailureAsync(
        GraphCommunitySummaryJob job,
        int retryCount)
    {
        if (job.OnTerminalFailure is null)
            return;
        try
        {
            await job.OnTerminalFailure(retryCount, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Community AI 最終失敗狀態無法持久化。Project={ProjectId}, Community={CommunityId}, ExceptionType={ExceptionType}",
                job.ProjectId,
                job.CommunityId,
                exception.GetType().Name);
        }
    }

    /// <summary>同一專案、版本、community、cacheKey 只允許一個工作。</summary>
    private static string JobKey(GraphCommunitySummaryJob job) =>
        $"{job.ProjectId}\0{job.GraphVersion}\0{job.CommunityId}\0{job.CacheKey}";

    private sealed record QueuedJob(string Key, GraphCommunitySummaryJob Job);
    private sealed record JobState(
        GraphCommunitySummaryJob Job,
        QueueState State,
        int RetryCount);

    private enum QueueState
    {
        Queued,
        Running,
        Completed,
        Failed,
    }
}
