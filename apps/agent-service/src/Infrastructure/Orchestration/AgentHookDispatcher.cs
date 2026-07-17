using AgentService.Application.Contracts;

namespace AgentService.Infrastructure.Orchestration;

public sealed class AgentHookDispatcher(
    IEnumerable<IAgentHook> hooks,
    ILogger<AgentHookDispatcher> logger) : IAgentHookDispatcher
{
    public async ValueTask DispatchAsync(AgentHookContext context, CancellationToken ct = default)
    {
        foreach (var hook in hooks)
        {
            try
            {
                await hook.InvokeAsync(context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Agent hook {HookName} failed at {HookStage}", hook.Name, context.Stage);
            }
        }
    }
}
