using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.CodeGraph;

namespace AgentService.Host.RestEndpoints;

public static class DataIntelligenceEndpoints
{
    public static IEndpointRouteBuilder MapDataIntelligenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/data-intelligence");
        group.MapGet("/glossary", ListGlossary);
        group.MapPost("/glossary", ProposeGlossary);
        group.MapPut("/glossary/{entryId}/review", ReviewGlossary);
        group.MapPost("/scan", ScanStaticData);
        group.MapGet("/runtime/status", RuntimeStatus);
        group.MapPost("/runtime/find-configuration", FindConfiguration);
        group.MapPost("/runtime/read-configuration", ReadConfiguration);
        group.MapPost("/runtime/inspect-schema", InspectSchema);
        return app;
    }

    private static async Task<IResult> ListGlossary(string projectId, string? status, IProjectRepository projects, IDomainGlossaryStore store, CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null) return Results.NotFound();
        GlossaryProposalStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<GlossaryProposalStatus>(status, true, out var parsedStatus))
                return Results.BadRequest(new { error = "Glossary status 無效。" });
            parsed = parsedStatus;
        }
        return Results.Ok(await store.ListAsync(projectId, parsed, ct));
    }

    private static async Task<IResult> ProposeGlossary(string projectId, ProposeGlossaryEntryRequest request, IProjectRepository projects, IDomainGlossaryStore store, CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null) return Results.NotFound();
        try { return Results.Created($"/api/projects/{projectId}/data-intelligence/glossary", await store.ProposeAsync(projectId, request, ct)); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    private static async Task<IResult> ReviewGlossary(string projectId, string entryId, ReviewGlossaryEntryRequest request, IProjectRepository projects, IDomainGlossaryStore store, CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null) return Results.NotFound();
        try { return Results.Ok(await store.ReviewAsync(projectId, entryId, request, ct)); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    private static async Task<IResult> ScanStaticData(string projectId, IProjectRepository projects, ProjectIndexService indexService, CancellationToken ct)
    {
        var project = await projects.GetAsync(projectId, ct);
        if (project is null) return Results.NotFound();
        await indexService.IndexProjectAsync(projectId, ct);
        var result = indexService.GetLastDataScanReport(projectId)
            ?? new DataScanReport(0, 0, [], ["資料結構掃描未產生報告。"], [], []);
        return Results.Ok(new
        {
            nodeCount = result.NodeCount, edgeCount = result.EdgeCount,
            diagnostics = result.Diagnostics, capabilityGaps = result.CapabilityGaps,
            scannedFiles = result.ScannedFiles, skippedFiles = result.SkippedFiles,
        });
    }

    private static async Task<IResult> RuntimeStatus(string projectId, bool? refresh, IProjectRepository projects, IDatabaseRuntimeEvidenceCoordinator runtime, CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null) return Results.NotFound();
        return Results.Ok(await runtime.GetStatusAsync(projectId, refresh == true, ct));
    }

    private static async Task<IResult> FindConfiguration(string projectId, DatabaseConfigurationLookup request, IProjectRepository projects, IDatabaseRuntimeEvidenceCoordinator runtime, CancellationToken ct) =>
        await RuntimeCall(projectId, projects, ct, () => runtime.FindConfigurationAsync(projectId, request, ct));

    private static async Task<IResult> ReadConfiguration(string projectId, DatabaseConfigurationLookup request, IProjectRepository projects, IDatabaseRuntimeEvidenceCoordinator runtime, CancellationToken ct) =>
        await RuntimeCall(projectId, projects, ct, () => runtime.ReadConfigurationAsync(projectId, request, ct));

    private static async Task<IResult> InspectSchema(string projectId, DatabaseSchemaInspectionRequest request, IProjectRepository projects, IDatabaseRuntimeEvidenceCoordinator runtime, CancellationToken ct) =>
        await RuntimeCall(projectId, projects, ct, () => runtime.InspectSchemaAsync(projectId, request, ct));

    private static async Task<IResult> RuntimeCall(string projectId, IProjectRepository projects, CancellationToken ct, Func<Task<IReadOnlyList<RuntimeEvidence>>> action)
    {
        if (await projects.GetAsync(projectId, ct) is null) return Results.NotFound();
        try { return Results.Ok(await action()); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidDataException) { return Results.Json(new { error = "Database Runtime Plugin 回應不符合安全證據契約。" }, statusCode: StatusCodes.Status502BadGateway); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.Json(new { error = "Database Runtime Plugin 查詢逾時。" }, statusCode: StatusCodes.Status504GatewayTimeout); }
    }
}
