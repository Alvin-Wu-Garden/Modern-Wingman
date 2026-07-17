using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using System.Text.Json;

namespace AgentService.Infrastructure.Streaming;

/// <summary>
/// 基於 System.Threading.Channels 的 Run 事件匯流排。
/// RunOrchestrator 發布事件；RunGrpcService 訂閱並 stream 給 Rust 層。
///
/// Channel 在 run 建立時一起建立（緩衝 1024 事件），
/// 確保早期事件（run:started）不因 gRPC stream 稍晚連接而遺失。
/// </summary>
public sealed class RunEventBus(IRunEventRepository repository,IRunStepRepository steps,ILogger<RunEventBus> logger) : IRunEventBus
{
    private readonly ConcurrentDictionary<string, Channel<RunStreamEvent>> _channels = new();

    /// <summary>
    /// 為指定 runId 建立一個新 channel 並回傳訂閱用的 ChannelReader。
    /// 應在 run 執行開始前呼叫。
    /// </summary>
    public ChannelReader<RunStreamEvent> Subscribe(string runId)
    {
        var channel = _channels.GetOrAdd(runId,_=>Channel.CreateBounded<RunStreamEvent>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true,
        }));
        return channel.Reader;
    }

    /// <summary>
    /// 發布一個事件到對應 runId 的 channel。
    /// 若 channel 尚未建立（race condition），靜默跳過。
    /// </summary>
    public async ValueTask PublishAsync(RunStreamEvent evt, CancellationToken ct = default)
    {
        try{await repository.AppendAsync(evt,ct);}catch(Exception ex)when(ex is not OperationCanceledException){logger.LogWarning(ex,"Failed to persist run event {Type}",evt.EventType);}
        try{await ProjectStepAsync(evt,ct);}catch(Exception ex)when(ex is not OperationCanceledException){logger.LogWarning(ex,"Failed to project run step {Type}",evt.EventType);}
        if (_channels.TryGetValue(evt.RunId, out var channel))
        {
            await channel.Writer.WriteAsync(evt, ct);
        }
    }

    private async Task ProjectStepAsync(RunStreamEvent evt,CancellationToken ct)
    {
        if(evt.EventType=="run:phase")
        {
            var active=await steps.GetActiveAsync(evt.RunId,ct);if(active is not null){active.Status="succeeded";active.EndedAt=evt.Timestamp;await steps.SaveAsync(active,ct);}
            using var payload=JsonDocument.Parse(evt.PayloadJson);var phase=payload.RootElement.TryGetProperty("phase",out var value)?value.GetString()??"unknown":"unknown";await steps.SaveAsync(new RunStep{RunId=evt.RunId,Phase=phase,StartedAt=evt.Timestamp},ct);return;
        }
        if(evt.EventType=="run:verify")
        {
            using var payload=JsonDocument.Parse(evt.PayloadJson);var success=payload.RootElement.TryGetProperty("success",out var value)&&value.GetBoolean();var attempt=payload.RootElement.TryGetProperty("attempt",out var attemptValue)?attemptValue.GetInt32():1;var active=await steps.GetActiveAsync(evt.RunId,ct);if(active is not null){active.Status=success?"succeeded":"failed";active.EndedAt=evt.Timestamp;active.ErrorSanitized=success?null:"Verification failed.";await steps.SaveAsync(active,ct);}else await steps.SaveAsync(new RunStep{RunId=evt.RunId,Phase="verify",Attempt=attempt,Status=success?"succeeded":"failed",StartedAt=evt.Timestamp,EndedAt=evt.Timestamp,ErrorSanitized=success?null:"Verification failed."},ct);return;
        }
        if(evt.EventType is "run:completed" or "run:failed" or "run:cancelled")
        {
            var active=await steps.GetActiveAsync(evt.RunId,ct);if(active is null)return;active.Status=evt.EventType=="run:completed"?"succeeded":evt.EventType=="run:cancelled"?"cancelled":"failed";active.EndedAt=evt.Timestamp;if(evt.EventType=="run:failed"){using var payload=JsonDocument.Parse(evt.PayloadJson);active.ErrorSanitized=payload.RootElement.TryGetProperty("error",out var error)?error.GetString():"Run failed.";}await steps.SaveAsync(active,ct);
        }
    }

    /// <summary>
    /// 將 channel 標記為完成；ChannelReader 的 ReadAllAsync 將結束迴圈。
    /// 應在 run 完成（completed / failed / cancelled）時呼叫。
    /// </summary>
    public void Complete(string runId)
    {
        if (_channels.TryRemove(runId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}
