namespace AgentService.Application.Contracts;

public interface IRunReplayGuard
{
    Task EnsureReplayAllowedAsync(string runId, CancellationToken ct = default);
}
