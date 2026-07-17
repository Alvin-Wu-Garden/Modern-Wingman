using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public enum AgentHookStage
{
    BeforeTool,
    AfterTool,
    AfterFileChange,
    BeforeRunComplete,
}

public sealed record AgentHookContext(
    AgentHookStage Stage,
    string RunId,
    string? ToolName = null,
    string? WorkspacePath = null,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    ToolExecutionResult? Result = null);

public interface IAgentHook
{
    string Name { get; }
    ValueTask InvokeAsync(AgentHookContext context, CancellationToken ct = default);
}

public interface IAgentHookDispatcher
{
    ValueTask DispatchAsync(AgentHookContext context, CancellationToken ct = default);
}
