using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.AgentFramework;
using AgentService.Modules.GraphRAG;
using Microsoft.Extensions.AI;

namespace AgentService.Host.RestEndpoints;

/// <summary>
/// 一般聊天與專案解析共用的對話端點。
/// 兩種模式共用同一份對話、訊息、附件與串流契約，只在送出訊息時選擇不同的上下文來源：
/// 一般聊天直接交給通用 Agent；專案聊天先從 GraphRAG 取得證據，再交給相同 Agent 回答。
/// </summary>
public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations");
        group.MapGet("/", ListConversations);
        group.MapPost("/", CreateConversation);
        group.MapGet("/{id}", GetConversation);
        group.MapDelete("/{id}", DeleteConversation);
        group.MapPatch("/{id}/title", SetTitle);
        group.MapPost("/{id}/messages", SendMessage);
        return app;
    }

    private static async Task<IResult> ListConversations(
        IConversationRepository repository,
        CancellationToken ct)
    {
        var conversations = await repository.ListAsync(ct);
        return Results.Ok(conversations.Select(conversation => ToDto(conversation)).ToList());
    }

    private static async Task<IResult> CreateConversation(
        CreateConversationPayload? payload,
        IConversationRepository conversations,
        IProjectRepository projects,
        CancellationToken ct)
    {
        var scope = ParseScope(payload?.Scope);
        if (scope == ConversationScope.Project)
        {
            if (string.IsNullOrWhiteSpace(payload?.ProjectId))
                return Results.BadRequest(new { error = "專案對話必須指定 projectId。" });
            if (await projects.GetAsync(payload.ProjectId, ct) is null)
                return Results.NotFound(new { error = "找不到指定的專案。" });
        }

        var conversation = await conversations.CreateAsync(
            payload?.ProviderProfileId,
            scope,
            payload?.ProjectId,
            ct);
        return Results.Created($"/api/conversations/{conversation.Id}", ToDto(conversation));
    }

    private static async Task<IResult> GetConversation(
        string id,
        IConversationRepository repository,
        CancellationToken ct)
    {
        var conversation = await repository.GetAsync(id, ct);
        if (conversation is null)
            return Results.NotFound();

        return Results.Ok(ToDto(conversation, conversation.Messages
            .Select(message => new MessageDto(
                message.Id,
                message.Role == MessageRole.User ? "user" : "assistant",
                message.Content,
                message.CreatedAt))
            .ToList()));
    }

    private static async Task<IResult> DeleteConversation(
        string id,
        IConversationRepository repository,
        CancellationToken ct)
    {
        await repository.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetTitle(
        string id,
        SetTitlePayload payload,
        IConversationRepository repository,
        CancellationToken ct)
    {
        if (await repository.GetAsync(id, ct) is null)
            return Results.NotFound();
        await repository.SetTitleAsync(id, payload.Title.Trim(), ct);
        return Results.NoContent();
    }

    private static async Task SendMessage(
        string id,
        SendMessageRequest request,
        IConversationRepository conversations,
        IProjectRepository projects,
        IModelProviderService providers,
        GraphIndexingService indexing,
        GraphRetrievalService graphRag,
        IGraphStore graphStore,
        INeo4jRuntime neo4j,
        WingmanChatAgent agent,
        ILlmCompletionService llm,
        IGraphIndexWatcherRegistry watcherRegistry,
        HttpContext http,
        CancellationToken ct)
    {
        var conversation = await conversations.GetAsync(id, ct);
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
        var prompt = request.UserMessage;
        ProjectEntity? project = null;
        IReadOnlyList<AIFunction> projectTools = [];

        if (conversation.Scope == ConversationScope.Project)
        {
            project = string.IsNullOrWhiteSpace(conversation.ProjectId)
                ? null
                : await projects.GetAsync(conversation.ProjectId, ct);
            if (project is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!await neo4j.EnsureAvailableAsync(null, ct))
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsJsonAsync(new
                {
                    error = neo4j.LastError ?? "Neo4j 圖譜資料庫目前無法連線。",
                }, ct);
                return;
            }

            // 正常情況由 FileSystemWatcher 在檔案異動當下標記 PendingChanges，
            // 不應在每一次問答前重新列舉並 SHA-256 整個專案。
            // 只有索引已知過期，或 watcher 沒有成功註冊時，才啟用一次性 hash fallback。
            if (RequiresFullIndexRefreshForProjectQuestion(project))
            {
                project = await indexing.IndexProjectAsync(project.Id, ct);
            }
            else if (!watcherRegistry.IsRegistered(project.Id) &&
                     await indexing.CatchUpAsync(project.Id, ct))
            {
                project = await indexing.IndexProjectAsync(project.Id, ct);
            }
            if (project.IndexManifestVersion is null)
            {
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                await http.Response.WriteAsJsonAsync(new
                {
                    error = "此專案尚未有可用的成功索引，請先完成索引。",
                }, ct);
                return;
            }
            var activeVersion = await graphStore.GetActiveManifestAsync(project.Id, ct);
            if (!string.Equals(
                    activeVersion,
                    project.IndexManifestVersion,
                    StringComparison.Ordinal))
            {
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                await http.Response.WriteAsJsonAsync(new
                {
                    error = "專案索引版本與目前啟用的 Graph 版本不一致，暫時不開放問答。",
                    errorCode = "graph_manifest_mismatch",
                }, ct);
                return;
            }
        }

        var firstExchange = conversation.Messages.Count == 0;
        await conversations.AddMessageAsync(id, MessageRole.User, request.UserMessage, ct);
        if (conversation.Title == "新對話")
            await conversations.SetTitleAsync(id, ShortTitle(request.UserMessage), ct);

        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Response.Headers.AccessControlAllowOrigin = "*";

        var runId = Guid.NewGuid().ToString("N");
        var activity = new AgentActivityReporter(
            runId,
            eventValue => WriteSseAsync(
                http,
                new { activity = eventValue },
                ct));
        string? runActivityId = null;
        var fullResponse = new StringBuilder();
        TokenUsage? usage = null;
        try
        {
            await WriteSseAsync(http, new
            {
                resolvedProviderId = profile.Id,
                resolvedModelId = modelId,
                scope = ToWireValue(conversation.Scope),
                runId,
            }, ct);
            runActivityId = await activity.StartAsync(
                "run.started",
                "正在分析問題",
                detail: conversation.Scope == ConversationScope.Project
                    ? "準備 GraphRAG 與專案工具"
                    : "準備模型回應");

            if (project is not null)
            {
                // 只把本次問題拿去檢索；歷史訊息仍由共用 Agent 以正常對話歷史提供。
                // 這可避免舊問題污染 GraphRAG 關鍵字，同時保留多輪對話的語意連續性。
                prompt = await graphRag.BuildAnswerPromptAsync(
                    project.Id,
                    project.RootPath,
                    request.UserMessage,
                    ct,
                    profile.Id,
                    modelId,
                    activity: activity);

                // 工具實例綁定本輪 projectId 與 rootPath，不註冊成全域 Singleton，
                // 避免另一個對話誤查到不同專案，也不讓模型自行指定任意工作目錄。
                projectTools = new ProjectAnalysisTools(
                        project.Id,
                        project.RootPath,
                        graphStore,
                        activity)
                    .CreateTools();
            }

            await foreach (var token in agent.RunStreamingAsync(
                prompt,
                conversation.Messages,
                profile,
                modelId,
                onUsage: value => usage = value,
                attachments: request.Attachments,
                includeSkills: conversation.Scope == ConversationScope.General,
                tools: projectTools,
                activity: activity,
                ct: ct))
            {
                fullResponse.Append(token);
                await WriteSseAsync(http, new { token }, ct);
            }

            if (fullResponse.Length > 0)
                await conversations.AddMessageAsync(
                    id,
                    MessageRole.Assistant,
                    fullResponse.ToString(),
                    CancellationToken.None);

            if (usage is not null)
            {
                await WriteSseAsync(http, new
                {
                    usage = new
                    {
                        inputTokens = usage.InputTokens,
                        outputTokens = usage.OutputTokens,
                        totalTokens = usage.TotalTokens,
                    },
                }, ct);
            }

            if (firstExchange && fullResponse.Length > 0)
                await TryGenerateTitleAsync(
                    id,
                    request.UserMessage,
                    fullResponse.ToString(),
                    profile.Id,
                    modelId,
                    conversations,
                    llm);

            if (runActivityId is not null)
                await activity.CompleteAsync(
                    runActivityId,
                    "回答已完成");
            await WriteSseAsync(http, new { done = true }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 使用者中止串流屬正常操作，不把未完成回覆寫入歷史。
        }
        catch (Exception ex)
        {
            if (runActivityId is not null)
                await activity.FailAsync(
                    runActivityId,
                    "Agent 執行失敗，請查看錯誤訊息後重試");
            await WriteSseAsync(http, new { error = ex.Message }, CancellationToken.None);
        }
    }

    private static async Task TryGenerateTitleAsync(
        string conversationId,
        string userMessage,
        string assistantMessage,
        string providerProfileId,
        string? modelId,
        IConversationRepository conversations,
        ILlmCompletionService llm)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var prompt = $"""
                請用十個繁體中文字以內，為以下對話產生純文字標題，不要加標點。
                使用者：{Truncate(userMessage, 300)}
                助理：{Truncate(assistantMessage, 400)}
                """;
            var title = (await llm.CompleteAsync(
                    prompt,
                    providerProfileId,
                    modelId,
                    timeout.Token))
                .Trim()
                .Split('\n')[0]
                .Trim()
                .TrimEnd('。', '，', '、', '！', '？', '"', '\'', '：');
            if (!string.IsNullOrWhiteSpace(title))
                await conversations.SetTitleAsync(
                    conversationId,
                    Truncate(title, 30),
                    CancellationToken.None);
        }
        catch
        {
            // 標題生成失敗不影響對話；先前建立的短標題仍可正常使用。
        }
    }

    private static ConversationDto ToDto(
        ConversationEntity conversation,
        List<MessageDto>? messages = null) =>
        new(
            conversation.Id,
            conversation.Title,
            conversation.ProviderProfileId,
            ToWireValue(conversation.Scope),
            conversation.ProjectId,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            messages);

    private static ConversationScope ParseScope(string? value) =>
        string.Equals(value, "project", StringComparison.OrdinalIgnoreCase)
            ? ConversationScope.Project
            : ConversationScope.General;

    /// <summary>
    /// 判斷專案問答是否必須先執行完整索引。
    /// PendingChanges 包含檔案 watcher 與資料庫設定異動；Stale 表示上一輪更新失敗但
    /// 仍有舊圖可用。兩者都應在回答前重試；Indexed 且 watcher 正常時則直接使用現有 Graph。
    /// </summary>
    /// <param name="project">本次專案對話綁定的持久化專案狀態。</param>
    /// <returns>需要先呼叫 IndexProjectAsync 時為 true。</returns>
    internal static bool RequiresFullIndexRefreshForProjectQuestion(
        ProjectEntity project) =>
        project.IndexStatus is
            ProjectIndexStatus.PendingChanges or
            ProjectIndexStatus.Stale;

    private static string ToWireValue(ConversationScope scope) =>
        scope == ConversationScope.Project ? "project" : "general";

    private static string ShortTitle(string value) =>
        value.Length > 50 ? value[..50] + "…" : value;

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";

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

    private sealed record CreateConversationPayload(
        string? ProviderProfileId = null,
        string? Scope = null,
        string? ProjectId = null);

    private sealed record SetTitlePayload(string Title);
}
