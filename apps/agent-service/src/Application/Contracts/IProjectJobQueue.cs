namespace AgentService.Application.Contracts;

/// <summary>
/// 專案索引與業務摘要共用的單一背景工作佇列。
/// 它不代表 Agent Run，也不接受任意工具工作，只避免長時間索引占住 HTTP request。
/// </summary>
public interface IProjectJobQueue
{
    /// <summary>排入一個受 Host 關機 token 控制的專案背景工作。</summary>
    ValueTask EnqueueAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}
