using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// Run 狀態持久化（WS2）。
/// 讓 Run 歷史在服務重啟後仍可查詢（審計 / 追溯需求）。
/// </summary>
public interface IRunRepository
{
    Task SaveAsync(RunEntity run, CancellationToken ct = default);
    Task<RunEntity?> GetAsync(string runId, CancellationToken ct = default);
    Task<IReadOnlyList<RunEntity>> ListBySessionAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<RunEntity>> ListByStatusesAsync(IReadOnlyCollection<RunStatus> statuses,CancellationToken ct=default);
}
