using AgentService.Application.Contracts;

namespace AgentService.Host.RestEndpoints;

public static class ExtensionEndpoints
{
    public sealed record ValidatePluginRequest(string RootPath);

    public static IEndpointRouteBuilder MapExtensionEndpoints(this IEndpointRouteBuilder app)
    {
        var plugins = app.MapGroup("/api/plugins");
        plugins.MapGet("/", async (IPluginCatalog catalog, CancellationToken ct) => Results.Ok(await catalog.ListAsync(ct)));
        plugins.MapPost("/validate", async (ValidatePluginRequest request, IPluginCatalog catalog, CancellationToken ct) =>
        {
            try { return Results.Ok(await catalog.ValidateAsync(request.RootPath, ct)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapGet("/api/evals/summary", async (DateTimeOffset? from, DateTimeOffset? to, IAgentEvalService evals, CancellationToken ct) =>
        {
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddDays(-30);
            try { return Results.Ok(await evals.GetSummaryAsync(start, end, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        return app;
    }
}
