using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.Orchestration;

namespace AgentService.Host.RestEndpoints;

/// <summary>只處理綁定單一專案的解析對話。</summary>
public static class ProjectConversationEndpoints
{
    public static IEndpointRouteBuilder MapProjectConversationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/conversations");
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{id}", Get);
        group.MapDelete("/{id}", Delete);
        group.MapPatch("/{id}/title", SetTitle);
        group.MapPost("/{id}/messages", SendMessage);
        return app;
    }

    private static async Task<IResult> List(
        string projectId,
        IProjectRepository projects,
        IConversationRepository conversations,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound(new { error = "找不到指定的專案。" });
        var items = await conversations.ListProjectAsync(projectId, ct);
        return Results.Ok(items.Select(item =>
            ConversationEndpointSupport.ToDto(item)).ToList());
    }

    private static async Task<IResult> Create(
        string projectId,
        CreateConversationRequest? request,
        IProjectRepository projects,
        IConversationRepository conversations,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound(new { error = "找不到指定的專案。" });

        var conversation = await conversations.CreateProjectAsync(
            projectId,
            request?.ProviderProfileId,
            ct);
        return Results.Created(
            $"/api/projects/{projectId}/conversations/{conversation.Id}",
            ConversationEndpointSupport.ToDto(conversation));
    }

    private static async Task<IResult> Get(
        string projectId,
        string id,
        IProjectRepository projects,
        IConversationRepository conversations,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound();
        var conversation = await conversations.GetProjectAsync(projectId, id, ct);
        return conversation is null
            ? Results.NotFound()
            : Results.Ok(ConversationEndpointSupport.ToDto(
                conversation,
                conversation.Messages.Select(ToMessageDto).ToList()));
    }

    private static async Task<IResult> Delete(
        string projectId,
        string id,
        IConversationRepository conversations,
        CancellationToken ct)
    {
        if (await conversations.GetProjectAsync(projectId, id, ct) is null)
            return Results.NotFound();
        await conversations.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetTitle(
        string projectId,
        string id,
        SetConversationTitleRequest request,
        IConversationRepository conversations,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return Results.BadRequest(new { error = "對話標題不可為空。" });
        if (await conversations.GetProjectAsync(projectId, id, ct) is null)
            return Results.NotFound();
        await conversations.SetTitleAsync(id, request.Title.Trim(), ct);
        return Results.NoContent();
    }

    private static async Task SendMessage(
        string projectId,
        string id,
        SendMessageRequest request,
        IProjectRepository projects,
        IConversationRepository conversations,
        IModelProviderService providers,
        ProjectConversationPreparationService preparation,
        ConversationExecutionService execution,
        HttpContext http,
        CancellationToken ct)
    {
        var project = await projects.GetAsync(projectId, ct);
        if (project is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var conversation = await conversations.GetProjectAsync(projectId, id, ct);
        if (conversation is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var profile = await providers.GetProfileAsync(
            request.ProviderProfileId ?? conversation.ProviderProfileId,
            ct);
        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? profile.ModelId
            : request.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(
                new { error = "目前 Provider 沒有可用的模型。" },
                ct);
            return;
        }

        await ConversationEndpointSupport.WriteStreamAsync(
            http,
            execution.ExecuteAsync(
                new ConversationExecutionRequest(
                    conversation,
                    request,
                    profile,
                    modelId,
                    (activity, preparationCt) => preparation.PrepareAsync(
                        project,
                        request.UserMessage,
                        profile,
                        modelId,
                        activity,
                        preparationCt),
                    EmitRuntimeActivities: true),
                ct),
            ct);
    }

    private static MessageDto ToMessageDto(Domain.Models.MessageEntity message) =>
        new(
            message.Id,
            message.Role == Domain.Models.MessageRole.User ? "user" : "assistant",
            message.Content,
            message.CreatedAt);

    private sealed record SetConversationTitleRequest(string Title);
}
