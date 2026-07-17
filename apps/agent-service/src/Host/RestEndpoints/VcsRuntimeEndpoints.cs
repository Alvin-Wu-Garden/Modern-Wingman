using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Host.RestEndpoints;

public static class VcsRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapVcsRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/vcs/runtimes", async (
            IVcsRuntimeResolver resolver,
            CancellationToken ct) => Results.Ok(new[]
            {
                await resolver.ResolveAsync(VcsType.Git, ct),
                await resolver.ResolveAsync(VcsType.Svn, ct),
            }));
        return app;
    }
}
