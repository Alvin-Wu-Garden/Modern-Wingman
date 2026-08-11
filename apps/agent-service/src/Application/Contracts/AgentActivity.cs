using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace AgentService.Application.Contracts;

/// <summary>
/// 一次 Agent 執行期間傳給前端的安全進度事件。
/// 事件只描述目前正在執行的階段與工具，不包含私有 Chain-of-Thought、完整 Prompt、
/// 原始工具參數或工具輸出的原始碼內容。
/// </summary>
public sealed record AgentActivityEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("activityId")] string ActivityId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("elapsedMs")] long? ElapsedMs,
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);

/// <summary>
/// 建立單一問答要求的 Agent 進度事件，並為每個活動維持唯一識別碼與耗時。
/// 此類別只存在於目前 HTTP request 的生命週期，不會寫入 SQLite 或 Neo4j。
/// </summary>
public sealed class AgentActivityReporter
{
    private readonly Func<AgentActivityEvent, Task> _publishAsync;
    private readonly ConcurrentDictionary<string, ActivityState> _activities = new(
        StringComparer.Ordinal);
    private int _sequence;

    /// <summary>
    /// 建立活動事件回報器。
    /// </summary>
    /// <param name="runId">目前 Agent 執行的唯一識別碼。</param>
    /// <param name="publishAsync">將事件傳送到目前 SSE 回應的回呼。</param>
    public AgentActivityReporter(
        string runId,
        Func<AgentActivityEvent, Task> publishAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(publishAsync);
        RunId = runId;
        _publishAsync = publishAsync;
    }

    /// <summary>目前 Agent 執行的唯一識別碼。</summary>
    public string RunId { get; }

    /// <summary>
    /// 發出活動開始事件，並回傳後續完成或失敗事件使用的 activityId。
    /// </summary>
    public async Task<string> StartAsync(
        string type,
        string label,
        string? tool = null,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var activityId = Guid.NewGuid().ToString("N");
        _activities[activityId] = new ActivityState(
            type,
            label,
            tool,
            Stopwatch.GetTimestamp());
        await PublishAsync(
            type,
            activityId,
            "started",
            label,
            tool,
            detail,
            null);
        return activityId;
    }

    /// <summary>
    /// 發出活動完成事件，並計算從 StartAsync 到現在的耗時。
    /// </summary>
    public Task CompleteAsync(
        string activityId,
        string? detail = null,
        string? label = null) =>
        PublishTerminalAsync(activityId, "completed", detail, label);

    /// <summary>
    /// 發出活動失敗事件。錯誤文字必須由呼叫端先遮罩成安全摘要。
    /// </summary>
    public Task FailAsync(
        string activityId,
        string detail) =>
        PublishTerminalAsync(activityId, "failed", detail, null);

    /// <summary>
    /// 發出不建立新活動的階段狀態事件，例如「正在整理答案」或「找到候選節點」。
    /// </summary>
    public Task StatusAsync(
        string type,
        string label,
        string? detail = null,
        string? activityId = null) =>
        PublishAsync(
            type,
            activityId ?? Guid.NewGuid().ToString("N"),
            "status",
            label,
            null,
            detail,
            null);

    private async Task PublishTerminalAsync(
        string activityId,
        string status,
        string? detail,
        string? labelOverride)
    {
        _activities.TryRemove(activityId, out var activity);
        var elapsedMs = activity is null
            ? null
            : (long?)Stopwatch.GetElapsedTime(activity.StartedAt).TotalMilliseconds;
        var terminalType = activity?.Type.EndsWith(
                ".started",
                StringComparison.OrdinalIgnoreCase) == true
            ? activity.Type[..^".started".Length] + "." + status
            : "activity." + status;
        await PublishAsync(
            terminalType,
            activityId,
            status,
            labelOverride ?? activity?.Label ?? string.Empty,
            activity?.Tool,
            detail,
            elapsedMs);
    }

    private Task PublishAsync(
        string type,
        string activityId,
        string status,
        string label,
        string? tool,
        string? detail,
        long? elapsedMs)
    {
        var activity = new AgentActivityEvent(
            Type: type,
            RunId: RunId,
            ActivityId: activityId,
            Status: status,
            Label: label,
            Tool: tool,
            Detail: detail,
            ElapsedMs: elapsedMs,
            Sequence: Interlocked.Increment(ref _sequence),
            Timestamp: DateTimeOffset.UtcNow);

        // SSE 用戶端中途斷線時，事件回呼可能拋出例外。進度事件是旁路資訊，
        // 不得讓專案工具的 in-flight 任務卡住或把原本成功的查詢變成失敗。
        return PublishSafelyAsync(activity);
    }

    private async Task PublishSafelyAsync(AgentActivityEvent activity)
    {
        try
        {
            // 回呼通常只是寫入無界 Channel；逾時可避免傳輸層永久阻塞工具。
            await _publishAsync(activity).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // 進度事件不可影響主要回答；前端斷線或通道關閉時安全忽略即可。
        }
    }

    private sealed record ActivityState(
        string Type,
        string Label,
        string? Tool,
        long StartedAt);
}
