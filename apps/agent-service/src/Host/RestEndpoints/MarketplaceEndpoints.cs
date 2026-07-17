using AgentService.Infrastructure.Marketplace;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Host.RestEndpoints;

public static class MarketplaceEndpoints
{
    public sealed record FavoriteRequest(bool IsFavorite);
    public sealed record FolderImportRequest(string FolderPath);
    public sealed record ArchiveImportRequest(string ArchivePath);
    public sealed record CodexMarketplaceImportRequest(string MarketplaceJsonPath);
    public sealed record GitHubRepositoryImportRequest(string RepositoryUrl, string? Reference);
    public sealed record DeploySkillRequest(IReadOnlyList<MarketplaceDeploymentRequest> Requests);
    public sealed record ConfigureMcpRequest(IReadOnlyList<MarketplaceDeploymentRequest> Requests);
    public sealed record ApplyUpdateRequest(string ExpectedCommitSha);
    public sealed record PluginConfigurationRequest(IReadOnlyDictionary<string, string> Values);

    public static IEndpointRouteBuilder MapMarketplaceEndpoints(this IEndpointRouteBuilder app)
    {
        var marketplace = app.MapGroup("/api/marketplace");
        marketplace.MapGet("/discover", async (
            string? kind,
            string? search,
            string? category,
            bool? includeStale,
            int? take,
            int? skip,
            IMarketplaceService service,
            CancellationToken ct) =>
        {
            MarketplaceArtifactKind? parsedKind = null;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                if (!Enum.TryParse<MarketplaceArtifactKind>(kind, true, out var value))
                    return Results.BadRequest(new { error = "Unsupported marketplace artifact kind." });
                parsedKind = value;
            }
            return Results.Ok(await service.ListAsync(new(parsedKind, search, category, includeStale ?? false, false, take ?? 100, skip ?? 0), ct));
        });
        marketplace.MapGet("/discover/{id}", async (string id, IMarketplaceService service, CancellationToken ct) =>
        {
            var item = await service.GetAsync(id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });
        marketplace.MapPost("/refresh", async (IMarketplaceService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.RefreshAsync(ct)); }
            catch (MarketplacePrerequisiteException ex) { return Results.Conflict(new { error = ex.Message, code = "GitHubPatRequired" }); }
        });
        marketplace.MapPut("/discover/{id}/favorite", async (string id, FavoriteRequest request, IMarketplaceService service, CancellationToken ct) =>
        {
            try { await service.SetFavoriteAsync(id, request.IsFavorite, ct); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        marketplace.MapGet("/artifacts", async (IMarketplaceArtifactService service, CancellationToken ct) => Results.Ok(await service.ListArtifactsAsync(ct)));
        marketplace.MapPost("/updates/check", async (IMarketplaceUpdateService service, CancellationToken ct) => Results.Ok(await service.CheckAsync(ct)));
        marketplace.MapGet("/updates/history", async (string? artifactId, int? take, IMarketplaceUpdateService service, CancellationToken ct) =>
            Results.Ok(await service.ListHistoryAsync(artifactId, take ?? 100, ct)));
        marketplace.MapPost("/artifacts/{artifactId}/updates/apply", async (string artifactId, ApplyUpdateRequest request, IMarketplaceUpdateService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ApplyAsync(artifactId, request.ExpectedCommitSha, ct)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or HttpRequestException or IOException or KeyNotFoundException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapGet("/activity", async (int? take, IMarketplaceActivityRecorder recorder, CancellationToken ct) =>
            Results.Ok(await recorder.ListAsync(take ?? 100, ct)));
        marketplace.MapPost("/import/folder", async (FolderImportRequest request, IMarketplaceArtifactService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ImportFolderAsync(request.FolderPath, ct)); }
            catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapPost("/import/archive", async (ArchiveImportRequest request, IMarketplaceArtifactService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ImportArchiveAsync(request.ArchivePath, cancellationToken: ct)); }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapPost("/import/codex-marketplace", async (CodexMarketplaceImportRequest request, ICodexMarketplaceImportService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ImportAsync(request.MarketplaceJsonPath, ct)); }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapPost("/import/github", async (GitHubRepositoryImportRequest request, IGitHubRepositoryImportService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ImportAsync(request.RepositoryUrl, request.Reference, ct)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or HttpRequestException or IOException) { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapGet("/targets", async (IMarketplaceDeploymentService service, CancellationToken ct) => Results.Ok(await service.ListTargetsAsync(ct)));
        marketplace.MapPost("/deploy/skills", async (DeploySkillRequest request, IMarketplaceDeploymentService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.DeploySkillAsync(request.Requests, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapPost("/deploy/skills/preview", async (DeploySkillRequest request, IMarketplaceDeploymentService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.PreviewSkillAsync(request.Requests, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapPost("/deploy/mcp", async (ConfigureMcpRequest request, IMarketplaceMcpDeploymentService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ConfigureAsync(request.Requests, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapPost("/deploy/mcp/preview", async (ConfigureMcpRequest request, IMarketplaceMcpDeploymentService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.PreviewAsync(request.Requests, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapGet("/artifacts/{artifactId}/deployments", async (string artifactId, IMarketplaceDeploymentService service, CancellationToken ct) =>
            Results.Ok(await service.ListDeploymentStatesAsync(artifactId, ct)));
        marketplace.MapDelete("/artifacts/{artifactId}/deployments", async (string artifactId, IMarketplaceDeploymentService service, CancellationToken ct) => Results.Ok(await service.RemoveFromAllManagedTargetsAsync(artifactId, ct)));
        marketplace.MapDelete("/artifacts/{artifactId}/mcp-deployments", async (string artifactId, IMarketplaceMcpDeploymentService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.RemoveFromAllManagedTargetsAsync(artifactId, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapGet("/plugins", async (IMarketplacePluginService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));
        marketplace.MapGet("/plugins/{installationId}/preview", async (string installationId, IMarketplacePluginService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.PreviewAsync(installationId, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapPost("/plugins/{artifactId}/install", async (string artifactId, IMarketplacePluginService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.InstallAsync(artifactId, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or KeyNotFoundException) { return Results.BadRequest(new { error = ex.Message }); }
        });
        marketplace.MapPut("/plugins/{installationId}/enabled/{enabled:bool}", async (string installationId, bool enabled, IMarketplacePluginService service, CancellationToken ct) =>
        {
            try { await service.SetEnabledAsync(installationId, enabled, ct); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        marketplace.MapGet("/plugins/{installationId}/configuration", async (string installationId, IMarketplacePluginService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.GetConfigurationAsync(installationId, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        marketplace.MapPut("/plugins/{installationId}/configuration", async (string installationId, PluginConfigurationRequest request, IMarketplacePluginService service, CancellationToken ct) =>
        {
            try { await service.SaveConfigurationAsync(installationId, request.Values, ct); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        return app;
    }
}
