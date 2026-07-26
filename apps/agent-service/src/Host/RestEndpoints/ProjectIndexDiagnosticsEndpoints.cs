using AgentService.Application.Contracts;
using AgentService.Modules.GraphRAG;

namespace AgentService.Host.RestEndpoints;

public static class ProjectIndexDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapProjectIndexDiagnosticsEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");
        group.MapGet("/{id}/index/manifest", GetDiagnostics);
        group.MapGet("/{id}/index/run", GetRun);
        group.MapPost("/{id}/index/catch-up", CatchUp);
        return app;
    }

    private static async Task<IResult> GetRun(
        string id,
        IProjectRepository projects,
        GraphIndexingService indexService,
        CancellationToken ct)
    {
        if (await projects.GetAsync(id, ct) is null)
            return Results.NotFound();
        return indexService.GetLastRun(id) is { } run
            ? Results.Ok(run)
            : Results.NotFound();
    }

    private static async Task<IResult> GetDiagnostics(
        string id,
        IProjectRepository projects,
        GraphIndexingService indexService,
        CancellationToken ct)
    {
        if (await projects.GetAsync(id, ct) is null)
            return Results.NotFound();
        return Results.Ok(await indexService.GetDiagnosticsAsync(id, ct));
    }

    private static async Task<IResult> CatchUp(
        string id,
        IProjectRepository projects,
        GraphIndexingService indexService,
        CancellationToken ct)
    {
        if (await projects.GetAsync(id, ct) is null)
            return Results.NotFound();
        var changed = await indexService.CatchUpAsync(id, ct);
        return Results.Ok(new { changed, diagnostics = await indexService.GetDiagnosticsAsync(id, ct) });
    }
}
