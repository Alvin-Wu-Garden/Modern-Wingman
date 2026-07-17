using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Host.RestEndpoints;

public static class SvnEndpoints
{
    public sealed record RemoteRequest(string ProfileId, string RepositoryUrl);
    public sealed record CheckoutRequest(string ProfileId, string RepositoryUrl, string DestinationPath);
    public sealed record WorkingCopyRequest(string WorkingCopyPath);
    public sealed record AuthWorkingCopyRequest(string ProfileId, string WorkingCopyPath);
    public sealed record SwitchRequest(string ProfileId, string WorkingCopyPath, string RepositoryUrl);
    public sealed record PathRequest(string WorkingCopyPath, string Path);
    public sealed record MoveRequest(string WorkingCopyPath, string Source, string Destination);
    public sealed record CommitRequest(string ProfileId, string WorkingCopyPath, string Message);

    public static IEndpointRouteBuilder MapSvnEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vcs/svn");
        group.MapPost("/test",TestConnection);
        group.MapPost("/browse", async (RemoteRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.BrowseAsync(r.ProfileId, r.RepositoryUrl, ct)));
        group.MapPost("/checkout", async (CheckoutRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.CheckoutAsync(r.ProfileId, r.RepositoryUrl, r.DestinationPath, ct)));
        group.MapPost("/update", async (AuthWorkingCopyRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.UpdateAsync(r.ProfileId, r.WorkingCopyPath, ct)));
        group.MapPost("/switch", async (SwitchRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.SwitchAsync(r.ProfileId, r.WorkingCopyPath, r.RepositoryUrl, ct)));
        group.MapPost("/status", async (WorkingCopyRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.StatusAsync(r.WorkingCopyPath, ct)));
        group.MapPost("/diff", async (WorkingCopyRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.DiffAsync(r.WorkingCopyPath, ct)));
        group.MapPost("/add", async (PathRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.AddAsync(r.WorkingCopyPath, r.Path, ct)));
        group.MapPost("/delete", async (PathRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.DeleteAsync(r.WorkingCopyPath, r.Path, ct)));
        group.MapPost("/move", async (MoveRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.MoveAsync(r.WorkingCopyPath, r.Source, r.Destination, ct)));
        group.MapPost("/commit", async (CommitRequest r, ISvnClient svn, CancellationToken ct) => Result(await svn.CommitAsync(r.ProfileId, r.WorkingCopyPath, r.Message, ct)));
        return app;
    }

    private static IResult Result(SvnCommandResult result) => result.Success ? Results.Ok(result) : Results.BadRequest(result);
    private static async Task<IResult> TestConnection(RemoteRequest request,ISvnClient svn,IVcsProfileRepository profiles,IAuditEventRecorder audit,CancellationToken ct){var profile=await profiles.GetAsync(request.ProfileId,ct);if(profile is null)return Results.NotFound();var result=await svn.TestConnectionAsync(request.ProfileId,request.RepositoryUrl,ct);profile.LastTestStatus=result.Success?"success":"failed";profile.LastTestError=result.Error;profile.LastTestedAt=DateTimeOffset.UtcNow;await profiles.SaveAsync(profile,ct);if(!profile.SslVerificationEnabled)await audit.RecordAsync(new("vcs.ssl_verification_disabled","vcs_profile",profile.Id,"connection_test",result.Success?"success":"failed",DetailsJson:"{\"sslVerificationEnabled\":false}"),ct);return Result(result);}
}
