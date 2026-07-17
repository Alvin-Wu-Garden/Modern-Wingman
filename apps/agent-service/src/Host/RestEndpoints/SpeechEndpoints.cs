using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Host.RestEndpoints;

public static class SpeechEndpoints
{
    public static IEndpointRouteBuilder MapSpeechEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/speech");

        group.MapGet("/status", GetStatus);
        group.MapPut("/settings", SaveSettings);
        group.MapPost("/models/download", DownloadModel);
        group.MapPost("/models/import-path", ImportModelPath);
        group.MapPost("/transcribe", Transcribe);

        return app;
    }

    private static async Task<IResult> GetStatus(
        ISpeechModelManager modelManager,
        CancellationToken ct) =>
        Results.Ok(await modelManager.GetStatusAsync(ct));

    private static async Task<IResult> SaveSettings(
        SpeechSettingsRequest request,
        ISpeechModelManager modelManager,
        CancellationToken ct) =>
        Results.Ok(await modelManager.SaveSettingsAsync(request, ct));

    private static async Task<IResult> DownloadModel(
        SpeechDownloadRequest request,
        ISpeechModelManager modelManager,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await modelManager.DownloadModelAsync(request.ModelId, request.Url, ct));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ImportModelPath(
        SpeechImportPathRequest request,
        ISpeechModelManager modelManager,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await modelManager.ImportModelAsync(request.Path, request.ModelId, ct));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> Transcribe(
        HttpRequest request,
        ISpeechToTextService speechToText,
        CancellationToken ct)
    {
        try
        {
            var contentType = request.ContentType ?? "application/octet-stream";
            var result = await speechToText.TranscribeAsync(request.Body, contentType, ct);
            return Results.Ok(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
