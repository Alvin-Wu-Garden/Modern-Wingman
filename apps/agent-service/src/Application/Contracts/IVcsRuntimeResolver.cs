using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public sealed record VcsRuntimeInfo(
    VcsType VcsType,
    bool Available,
    string? ExecutablePath,
    string? Version,
    string? Source,
    string? Error = null);

public interface IVcsRuntimeResolver
{
    Task<VcsRuntimeInfo> ResolveAsync(VcsType type, CancellationToken ct = default);
}
