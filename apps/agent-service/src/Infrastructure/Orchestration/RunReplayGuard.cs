using AgentService.Application.Contracts;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Orchestration;

public sealed class RunReplayGuard(IDbContextFactory<AppDbContext> factory) : IRunReplayGuard
{
    public async Task EnsureReplayAllowedAsync(string runId, CancellationToken ct = default)
    {
        var sideEffectTypes = new[] { "vcs", "mcp" };
        await using var db = await factory.CreateDbContextAsync(ct);
        var hasSuccessfulSideEffect = await db.AiToolCallLogs.AsNoTracking().AnyAsync(
            tool => tool.RequestLog!.RunId == runId &&
                    tool.Status == "succeeded" &&
                    sideEffectTypes.Contains(tool.ToolType),
            ct);
        if (hasSuccessfulSideEffect)
        {
            throw new InvalidOperationException(
                "This run completed external side effects and cannot be replayed automatically. " +
                "Start a new run from the current workspace state.");
        }
    }
}
