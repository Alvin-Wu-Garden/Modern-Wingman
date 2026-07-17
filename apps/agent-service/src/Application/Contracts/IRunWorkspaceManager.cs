using AgentService.Domain.Models;
namespace AgentService.Application.Contracts;
public sealed record PreparedRunWorkspace(string? Path,string? Branch,string? BaseRevision);
public interface IRunWorkspaceManager
{
    WorkspaceStrategy ResolveStrategy(string workspacePath, WorkspaceStrategy requested);
    Task<PreparedRunWorkspace> PrepareAsync(RunEntity run, CancellationToken ct = default);
}
