using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Host.RestEndpoints;

public static class GitEndpoints
{
    public sealed record RemoteRequest(string ProfileId, string RepositoryUrl);
    public sealed record CloneRequest(
        string ProfileId, string RepositoryUrl, string Branch, string DestinationPath);
    public sealed record RepositoryRequest(string RepositoryPath);
    public sealed record RemoteRepositoryRequest(string ProfileId, string RepositoryPath, string Remote);
    public sealed record PullRequest(string ProfileId, string RepositoryPath, string Remote, string Branch);
    public sealed record SwitchRequest(string RepositoryPath, string Branch, bool Create, string? StartPoint);
    public sealed record WorktreeRequest(
        string RepositoryPath, string WorktreePath, string BranchName, string StartPoint);
    public sealed record CommitRequest(string ProfileId, string RepositoryPath, string Message);
    public sealed record PushRequest(string ProfileId, string RepositoryPath, string Remote, string Branch);

    public static IEndpointRouteBuilder MapGitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vcs/git");
        group.MapPost("/test",TestConnection);
        group.MapPost("/branches", async (RemoteRequest request, IGitClient git, CancellationToken ct) =>
            Results.Ok(await git.ListRemoteBranchesAsync(request.ProfileId, request.RepositoryUrl, ct)));
        group.MapPost("/clone", async (CloneRequest request, IGitClient git, CancellationToken ct) =>
            ToResult(await git.CloneAsync(request.ProfileId, request.RepositoryUrl, request.Branch, request.DestinationPath, ct)));
        group.MapPost("/fetch", async (RemoteRepositoryRequest request, IGitClient git, CancellationToken ct) =>
            ToResult(await git.FetchAsync(request.ProfileId, request.RepositoryPath, request.Remote, ct)));
        group.MapPost("/pull", async (PullRequest request, IGitClient git, CancellationToken ct) =>
            ToResult(await git.PullAsync(request.ProfileId, request.RepositoryPath, request.Remote, request.Branch, ct)));
        group.MapPost("/switch", async (SwitchRequest request, IGitClient git, CancellationToken ct) =>
            ToResult(await git.SwitchAsync(request.RepositoryPath, request.Branch, request.Create, request.StartPoint, ct)));
        group.MapPost("/worktrees", async (WorktreeRequest request, IGitClient git, CancellationToken ct) =>
            ToResult(await git.CreateWorktreeAsync(request.RepositoryPath, request.WorktreePath, request.BranchName, request.StartPoint, ct)));
        group.MapPost("/status", async (RepositoryRequest request, IGitClient git, CancellationToken ct) =>
            ToResult(await git.StatusAsync(request.RepositoryPath, ct)));
        group.MapPost("/diff", async (RepositoryRequest request, bool staged, IGitClient git, CancellationToken ct) =>
            ToResult(await git.DiffAsync(request.RepositoryPath, staged, ct)));
        group.MapPost("/commit", async (CommitRequest request, IGitClient git, CancellationToken ct) =>
            ToResult(await git.CommitAsync(request.ProfileId, request.RepositoryPath, request.Message, ct)));
        group.MapPost("/push", async (PushRequest request, IGitClient git, CancellationToken ct) =>
            ToResult(await git.PushAsync(request.ProfileId, request.RepositoryPath, request.Remote, request.Branch, ct)));
        return app;
    }

    private static IResult ToResult(GitCommandResult result) => result.Success
        ? Results.Ok(result)
        : Results.BadRequest(result);

    private static async Task<IResult> TestConnection(RemoteRequest request,IGitClient git,IVcsProfileRepository profiles,IAuditEventRecorder audit,CancellationToken ct)
    {
        var profile=await profiles.GetAsync(request.ProfileId,ct);if(profile is null)return Results.NotFound();var result=await git.TestConnectionAsync(request.ProfileId,request.RepositoryUrl,ct);profile.LastTestStatus=result.Success?"success":"failed";profile.LastTestError=result.Error;profile.LastTestedAt=DateTimeOffset.UtcNow;await profiles.SaveAsync(profile,ct);if(!profile.SslVerificationEnabled)await audit.RecordAsync(new("vcs.ssl_verification_disabled","vcs_profile",profile.Id,"connection_test",result.Success?"success":"failed",DetailsJson:"{\"sslVerificationEnabled\":false}"),ct);return ToResult(result);
    }
}
