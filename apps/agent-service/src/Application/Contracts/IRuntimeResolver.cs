using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IRuntimeResolver
{
    Task<ResolvedRuntime?> ResolveAsync(
        RuntimeResolutionRequest request,
        CancellationToken ct = default);
}
