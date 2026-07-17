using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IToolRegistry
{
    IReadOnlyList<ToolDescriptor> ListTools();
    bool TryGet(string name, out IAgentTool? tool);
    Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default);
    void Register(IAgentTool tool);
}

/// <summary>
/// Allows a managed capability provider to replace only the tools it owns.
/// This keeps plugin enable/disable from mutating built-in or user configured tools.
/// </summary>
public interface IManagedToolRegistry : IToolRegistry
{
    void ReplaceSource(string source, IReadOnlyList<IAgentTool> tools);
}
