using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IAgentTool
{
    ToolDescriptor Descriptor { get; }
    Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default);
}
