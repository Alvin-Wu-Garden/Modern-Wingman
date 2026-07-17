using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// Run 事件匯流排介面。
/// Infrastructure 層的 RunEventBus 實作此介面（System.Threading.Channels）。
/// GrpcService 透過 SubscribeAsync 取得 ChannelReader 並 stream 給 Rust 層。
/// </summary>
public interface IRunEventBus
{
    /// <summary>訂閱指定 runId 的事件流；回傳 ChannelReader 直到 run 結束。</summary>
    System.Threading.Channels.ChannelReader<RunStreamEvent> Subscribe(string runId);

    /// <summary>發佈一個事件（fire-and-forget 友善）。</summary>
    ValueTask PublishAsync(RunStreamEvent evt, CancellationToken ct = default);

    /// <summary>將指定 runId 的 channel 標記為完成（所有訂閱者將結束迭代）。</summary>
    void Complete(string runId);
}
