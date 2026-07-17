namespace AgentService.Application.Contracts;

public sealed record WorkspaceActionResult(bool Success,string Action,string? Output=null,string? Error=null,bool RequiresProtectedConfirmation=false);
public sealed record WorkspaceActionPreview(string? VcsType,string? Remote,string? Target,string? Revision,bool Protected);

public interface IRunWorkspaceLifecycleService
{
    Task<WorkspaceActionResult> ExecuteAsync(string runId,string action,string? message,bool protectedConfirmed,CancellationToken ct=default);
    Task<WorkspaceActionPreview> PreviewAsync(string runId,CancellationToken ct=default);
}
