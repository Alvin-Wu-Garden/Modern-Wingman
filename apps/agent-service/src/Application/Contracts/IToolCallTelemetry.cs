using AgentService.Application.Models;
namespace AgentService.Application.Contracts;
public interface IToolCallTelemetry
{
    Task<string?> StartAsync(ToolExecutionRequest request,CancellationToken ct=default);
    Task CompleteAsync(string? id,ToolExecutionResult result,CancellationToken ct=default);
}
