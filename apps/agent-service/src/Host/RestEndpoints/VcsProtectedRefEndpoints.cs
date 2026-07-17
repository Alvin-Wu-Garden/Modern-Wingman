using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Host.RestEndpoints;

public static class VcsProtectedRefEndpoints
{
    public sealed record SaveProtectedRefRequest(
        string VcsType,
        string Pattern,
        string? ProjectId = null);

    public static IEndpointRouteBuilder MapVcsProtectedRefEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vcs/protected-refs");
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapDelete("/{id}", Delete);
        return app;
    }

    private static async Task<IResult> List(
        IVcsStateRepository repository,
        CancellationToken ct)
    {
        var git = await repository.ListProtectedRefsAsync(VcsType.Git, null, ct);
        var svn = await repository.ListProtectedRefsAsync(VcsType.Svn, null, ct);
        return Results.Ok(git.Concat(svn).Select(ToDto));
    }

    private static async Task<IResult> Create(
        SaveProtectedRefRequest request,
        IVcsStateRepository repository,
        CancellationToken ct)
    {
        if (!Enum.TryParse<VcsType>(request.VcsType, true, out var type))
            return Results.BadRequest(new { error = "VcsType must be Git or Svn." });
        var pattern = request.Pattern.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(pattern) || pattern.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return Results.BadRequest(new { error = "Protected ref pattern is invalid." });

        var rule = new VcsProtectedRef
        {
            VcsType = type,
            ProjectId = string.IsNullOrWhiteSpace(request.ProjectId) ? null : request.ProjectId,
            Pattern = pattern,
        };
        await repository.SaveProtectedRefAsync(rule, ct);
        return Results.Created($"/api/vcs/protected-refs/{rule.Id}", ToDto(rule));
    }

    private static async Task<IResult> Delete(
        string id,
        IVcsStateRepository repository,
        CancellationToken ct)
    {
        await repository.DeleteProtectedRefAsync(id, ct);
        return Results.NoContent();
    }

    private static object ToDto(VcsProtectedRef rule) => new
    {
        rule.Id,
        vcsType = rule.VcsType.ToString().ToLowerInvariant(),
        rule.ProjectId,
        rule.Pattern,
        rule.Enabled,
    };
}
