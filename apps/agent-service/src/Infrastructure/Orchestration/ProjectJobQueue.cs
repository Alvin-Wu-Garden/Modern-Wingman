using System.Threading.Channels;
using AgentService.Application.Contracts;

namespace AgentService.Infrastructure.Orchestration;

/// <summary>
/// 最多平行執行四個不同專案工作；同一專案的互斥由各協調器負責。
/// 正常關機取消不視為失敗；單一工作失敗只記錄類型，不讓佇列停止。
/// </summary>
public sealed class ProjectJobQueue(
    ILogger<ProjectJobQueue> logger) : BackgroundService, IProjectJobQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _queue =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>排入工作；若呼叫端已取消則不接受該工作。</summary>
    public ValueTask EnqueueAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default) =>
        _queue.Writer.WriteAsync(work, cancellationToken);

    /// <summary>持續消費工作直到 Host 關機；關機取消會安靜結束。</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, 4)
            .Select(_ => ConsumeAsync(stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await work(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        "專案背景工作失敗；佇列將繼續。ExceptionType={ExceptionType}",
                        exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // ReadAllAsync 在正常 Host 關機時會丟出取消；這不是 background service failure。
        }
    }
}
