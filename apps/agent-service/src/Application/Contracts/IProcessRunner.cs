using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken ct = default);
}
