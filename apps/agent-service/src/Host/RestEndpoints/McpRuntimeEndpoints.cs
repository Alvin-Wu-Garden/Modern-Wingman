using AgentService.Application.Contracts;
namespace AgentService.Host.RestEndpoints;
public static class McpRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapMcpRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/mcp/runtime");
        group.MapGet("/status",(IMcpToolCatalog catalog)=>Results.Ok(new{servers=catalog.Health,tools=catalog.Tools}));
        group.MapPost("/refresh",async(IMcpToolCatalog catalog,CancellationToken ct)=>{await catalog.RefreshAsync(ct);return Results.Ok(new{servers=catalog.Health,tools=catalog.Tools});});
        return app;
    }
}
