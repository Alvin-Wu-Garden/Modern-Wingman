using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.Orchestration;

namespace AgentService.Host.RestEndpoints;

/// <summary>只處理不綁定專案的通用對話。</summary>
public static class GeneralConversationEndpoints
{
    public static IEndpointRouteBuilder MapGeneralConversationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations");
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{id}", Get);
        group.MapDelete("/{id}", Delete);
        group.MapPatch("/{id}/title", SetTitle);
        group.MapPost("/{id}/messages", SendMessage);
        return app;
    }

    private static async Task<IResult> List(
        IConversationRepository conversations,
        CancellationToken ct)
    {
        var items = await conversations.ListGeneralAsync(ct);
        return Results.Ok(items.Select(item =>
            ConversationEndpointSupport.ToDto(item)).ToList());
    }

    private static async Task<IResult> Create(
        CreateConversationRequest? request,
        IConversationRepository conversations,
        CancellationToken ct)
    {
        var conversation = await conversations.CreateGeneralAsync(
            request?.ProviderProfileId,
            ct);
        return Results.Created(
            $"/api/conversations/{conversation.Id}",
            ConversationEndpointSupport.ToDto(conversation));
    }

    private static async Task<IResult> Get(
        string id,
        IConversationRepository conversations,
        CancellationToken ct)
    {
        var conversation = await conversations.GetGeneralAsync(id, ct);
        return conversation is null
            ? Results.NotFound()
            : Results.Ok(ConversationEndpointSupport.ToDto(
                conversation,
                conversation.Messages.Select(ToMessageDto).ToList()));
    }

    private static async Task<IResult> Delete(
        string id,
        IConversationRepository conversations,
        CancellationToken ct)
    {
        if (await conversations.GetGeneralAsync(id, ct) is null)
            return Results.NotFound();
        await conversations.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetTitle(
        string id,
        SetConversationTitleRequest request,
        IConversationRepository conversations,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return Results.BadRequest(new { error = "對話標題不可為空。" });
        if (await conversations.GetGeneralAsync(id, ct) is null)
            return Results.NotFound();
        await conversations.SetTitleAsync(id, request.Title.Trim(), ct);
        return Results.NoContent();
    }

    private static async Task SendMessage(
        string id,
        SendMessageRequest request,
        IConversationRepository conversations,
        IModelProviderService providers,
        ConversationExecutionService execution,
        HttpContext http,
        CancellationToken ct)
    {
        var conversation = await conversations.GetGeneralAsync(id, ct);
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

        await ConversationEndpointSupport.WriteStreamAsync(
            http,
            execution.ExecuteAsync(
                new ConversationExecutionRequest(
                    conversation,
                    request,
                    profile,
                    modelId),
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
