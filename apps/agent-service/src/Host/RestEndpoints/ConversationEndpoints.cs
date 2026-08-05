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
        GraphRetrievalService graphRag,
        IGraphStore graphStore,
        WingmanChatAgent agent,
        IProjectJobQueue projectJobs,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        HttpContext http,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("ConversationEndpoints");
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
        var graphStatus = "not_applicable";
        string? graphWarning = null;

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

            // 問答只做短時間 Graph probe，不在 HTTP request 內啟動 Neo4j、完整索引或
            // 重新 SHA-256 專案。Graph 暫時不可用時，後續仍會建立唯讀原始碼工具。
            var graphContext = await ProbeGraphContextAsync(
                project,
                graphStore,
                logger,
                ct);
            graphStatus = graphContext.Status;
            graphWarning = graphContext.Warning;
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
                graphStatus = project is null ? null : graphStatus,
                graphWarning,
            }, ct);
            runActivityId = await activity.StartAsync(
                "run.started",
                "正在分析問題",
                detail: conversation.Scope == ConversationScope.Project
                    ? "準備 GraphRAG 與專案工具"
                    : "準備模型回應");

            if (project is not null)
            {
                if (graphStatus is "ready" or "stale")
                {
                    try
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
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Graph probe 可能在真正查詢時失效；降級到 source-only，讓 Agent
                        // 仍可使用 search_project_text、read_project_file_range 與 symbol tool。
                        graphStatus = "unavailable";
                        graphWarning = "知識圖譜檢索暫時失敗，本輪改用最新原始碼工具。";
                        logger.LogWarning(
                            exception,
                            "Project Graph retrieval failed; falling back to source tools. ProjectId={ProjectId}",
                            project.Id);
                        prompt = BuildSourceOnlyPrompt(
                            request.UserMessage,
                            project.RootPath,
                            graphWarning);
                    }
                }
                else
                {
                    prompt = BuildSourceOnlyPrompt(
                            request.UserMessage,
                            project.RootPath,
                            graphWarning ?? "目前沒有可用的知識圖譜版本。");
                }

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

            if (runActivityId is not null)
                await activity.CompleteAsync(
                    runActivityId,
                    "回答已完成");
            await WriteSseAsync(http, new { done = true }, CancellationToken.None);

            if (firstExchange && fullResponse.Length > 0)
            {
                var titleInput = fullResponse.ToString();
                await projectJobs.EnqueueAsync(async backgroundToken =>
                {
                    // 背景工作自行建立 scope，避免 HTTP request 結束後使用已釋放的
                    // scoped repository；標題失敗不得影響已送出的主要回答。
                    using var backgroundScope = scopeFactory.CreateScope();
                    var backgroundConversations =
                        backgroundScope.ServiceProvider
                            .GetRequiredService<IConversationRepository>();
                    var backgroundLlm =
                        backgroundScope.ServiceProvider
                            .GetRequiredService<ILlmCompletionService>();
                    await TryGenerateTitleAsync(
                        id,
                        request.UserMessage,
                        titleInput,
                        profile.Id,
                        modelId,
                        backgroundConversations,
                        backgroundLlm,
                        backgroundToken);
                }, CancellationToken.None);
            }
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
        ILlmCompletionService llm,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
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
                    cancellationToken);
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

    private static string ToWireValue(ConversationScope scope) =>
        scope == ConversationScope.Project ? "project" : "general";

    private static string ShortTitle(string value) =>
        value.Length > 50 ? value[..50] + "…" : value;

    /// <summary>
    /// 建立沒有 Graph 證據時使用的專案提示，明確要求 Agent 改用唯讀原始碼工具。
    /// </summary>
    private static string BuildSourceOnlyPrompt(
        string question,
        string rootPath,
        string warning) =>
        $"""
        你正在分析 FBL 投資系統專案，專案根目錄為：{rootPath}

        本輪知識圖譜狀態：{warning}
        請不要假設不存在的 Graph 節點或鏈路。請優先使用本輪提供的唯讀工具：
        - search_project_text：搜尋原始碼、ASPX、JavaScript、TypeScript、SQL 與設定。
        - find_csharp_symbol：確認 C# 類別、方法與行號。
        - read_project_file_range：讀取實際原始碼並附上檔案路徑與行號。

        回答時必須區分已確認事實、合理推論與尚未確認項目；資訊不足時說明缺口，不能自行補造 Graph 關係。

        使用者問題：
        {question}
        """;

    /// <summary>
    /// 以短逾時檢查目前已存在的 Graph，不負責啟動 Neo4j 或執行索引。
    /// 這是專案問答的可降級探測，避免 Graph cold start 阻塞原始碼問答。
    /// </summary>
    private static async Task<GraphContextSnapshot> ProbeGraphContextAsync(
        ProjectEntity project,
        IGraphStore graphStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        probeTimeout.CancelAfter(TimeSpan.FromMilliseconds(750));

        try
        {
            if (!await graphStore.PingAsync(probeTimeout.Token))
            {
                return new GraphContextSnapshot(
                    "unavailable",
                    "知識圖譜目前無法連線；本輪改用最新原始碼工具。");
            }

            var activeVersion = await graphStore.GetActiveManifestAsync(
                project.Id,
                probeTimeout.Token);
            if (string.IsNullOrWhiteSpace(activeVersion))
            {
                return new GraphContextSnapshot(
                    "unavailable",
                    "目前沒有可用的成功 Graph 版本；本輪改用原始碼工具。" );
            }

            var status = string.Equals(
                    project.IndexManifestVersion,
                    activeVersion,
                    StringComparison.Ordinal)
                ? "ready"
                : "stale";
            var warning = status == "stale"
                ? "知識圖譜版本可能落後目前專案檔案；重要結論需用原始碼工具確認。"
                : null;
            return new GraphContextSnapshot(status, warning);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GraphContextSnapshot(
                "unavailable",
                "知識圖譜探測逾時；本輪改用最新原始碼工具。" );
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Graph probe failed; source-only project analysis will continue. ProjectId={ProjectId}",
                project.Id);
            return new GraphContextSnapshot(
                "unavailable",
                "知識圖譜探測失敗；本輪改用最新原始碼工具。" );
        }
    }

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

    private sealed record GraphContextSnapshot(string Status, string? Warning);
}
