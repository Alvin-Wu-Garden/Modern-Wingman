using System.Text.Json;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Host.RestEndpoints;

/// <summary>一般對話與專案對話共用的 DTO 及 SSE 輸出工具。</summary>
internal static class ConversationEndpointSupport
{
    public static ConversationDto ToDto(
        ConversationEntity conversation,
        List<MessageDto>? messages = null) =>
        new(
            conversation.Id,
            conversation.Title,
            conversation.ProviderProfileId,
            conversation.ProjectId,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            messages);

    public static async Task WriteStreamAsync(
        HttpContext http,
        IAsyncEnumerable<ConversationStreamEvent> events,
        CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Response.Headers.AccessControlAllowOrigin = "*";

        await foreach (var item in events.WithCancellation(ct))
        {
            object payload;
            switch (item)
            {
                case ConversationStartedEvent started:
                    payload = new
                    {
                        resolvedProviderId = started.ResolvedProviderId,
                        resolvedModelId = started.ResolvedModelId,
                        started.RunId,
                        graphStatus = started.GraphStatus,
                        graphWarning = started.GraphWarning,
                    };
                    break;
                case ConversationActivityStreamEvent activity:
                    payload = new { activity = activity.Activity };
                    break;
                case ConversationTokenEvent token:
                    payload = new { token = token.Token };
                    break;
                case ConversationUsageEvent usage:
                    payload = new
                    {
                        usage = new
                        {
                            inputTokens = usage.Usage.InputTokens,
                            outputTokens = usage.Usage.OutputTokens,
                            totalTokens = usage.Usage.TotalTokens,
                        },
                    };
                    break;
                case ConversationCompletedEvent:
                    payload = new { done = true };
                    break;
                case ConversationErrorEvent error:
                    payload = new
                    {
                        error = error.Error,
                        code = error.Code,
                        retryable = error.Retryable,
                        stage = error.Stage,
                    };
                    break;
                default:
                    throw new InvalidOperationException("不支援的對話串流事件。");
            }

            await WriteSseAsync(http, payload, ct);
        }
    }

    private static async Task WriteSseAsync(
        HttpContext http,
        object payload,
        CancellationToken ct)
    {
        await http.Response.WriteAsync(
            $"data: {JsonSerializer.Serialize(payload)}\n\n",
            ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
