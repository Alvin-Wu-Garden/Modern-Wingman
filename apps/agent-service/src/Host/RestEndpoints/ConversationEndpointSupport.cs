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

        try
        {
            await foreach (var item in events.WithCancellation(ct))
            {
                object payload;
                switch (item)
                {
                    case ConversationStartedEvent started:
                        payload = new
                        {
                            type = "started",
                            resolvedProviderId = started.ResolvedProviderId,
                            resolvedModelId = started.ResolvedModelId,
                            started.RunId,
                            graphStatus = started.GraphStatus,
                            graphWarning = started.GraphWarning,
                            graphVersion = started.GraphVersion,
                            turnId = started.TurnId,
                        };
                        break;
                    case ConversationActivityStreamEvent activity:
                        payload = new { type = "activity", activity = activity.Activity, turnId = activity.TurnId };
                        break;
                    case ConversationTokenEvent token:
                        payload = new { type = "token", token = token.Token, turnId = token.TurnId };
                        break;
                    case ConversationUsageEvent usage:
                        payload = new
                        {
                            type = "usage",
                            usage = new
                            {
                                inputTokens = usage.Usage.InputTokens,
                                outputTokens = usage.Usage.OutputTokens,
                                totalTokens = usage.Usage.TotalTokens,
                            },
                            turnId = usage.TurnId,
                        };
                        break;
                    case ConversationCompletedEvent completed:
                        payload = new { type = "completed", done = true, turnId = completed.TurnId };
                        break;
                    case ConversationErrorEvent error:
                        payload = new
                        {
                            type = "error",
                            error = error.Error,
                            code = error.Code,
                            retryable = error.Retryable,
                            stage = error.Stage,
                            turnId = error.TurnId,
                        };
                        break;
                    default:
                        throw new InvalidOperationException("不支援的對話串流事件。");
                }

                await WriteSseAsync(http, payload, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 瀏覽器關閉串流時是正常取消，不應把 TaskCanceledException 當成
            // 未處理的後端錯誤寫入 ASP.NET 例外 Log。
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
