using System.Diagnostics;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.AgentFramework;
using AgentService.Infrastructure.Providers;
using AgentService.Infrastructure.Telemetry;
using Microsoft.Extensions.Options;

namespace AgentService.Host.RestEndpoints;

/// <summary>
/// Conversations REST 端點（前端直接呼叫）。
///
/// GET  /api/conversations          → 列出所有對話（不含訊息）
/// POST /api/conversations          → 建立新對話
/// GET  /api/conversations/{id}     → 取得對話（含訊息歷史）
/// DELETE /api/conversations/{id}   → 刪除對話
/// POST /api/conversations/{id}/messages → 送出訊息（SSE 串流回應）
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
        group.MapPost("/{id}/messages", SendMessage);
        group.MapPatch("/{id}/title", SetTitle);

        return app;
    }

    // ─── Handlers ─────────────────────────────────────────────────────────────

    private static async Task<IResult> ListConversations(
        IConversationRepository repo,
        CancellationToken ct)
    {
        var items = await repo.ListAsync(ct);
        var dtos = items.Select(c => new ConversationDto(
            c.Id, c.Title, c.ProviderProfileId, c.CreatedAt, c.UpdatedAt)).ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> CreateConversation(
        CreateConversationPayload? payload,
        IConversationRepository repo,
        CancellationToken ct)
    {
        var conv = await repo.CreateAsync(payload?.ProviderProfileId, ct);
        return Results.Created(
            $"/api/conversations/{conv.Id}",
            new ConversationDto(conv.Id, conv.Title, conv.ProviderProfileId, conv.CreatedAt, conv.UpdatedAt));
    }

    private static async Task<IResult> GetConversation(
        string id,
        IConversationRepository repo,
        CancellationToken ct)
    {
        var conv = await repo.GetAsync(id, ct);
        if (conv is null) return Results.NotFound();

        var messages = conv.Messages.Select(m => new MessageDto(
            m.Id,
            m.Role == MessageRole.User ? "user" : "assistant",
            m.Content,
            m.CreatedAt)).ToList();

        return Results.Ok(new ConversationDto(
            conv.Id, conv.Title, conv.ProviderProfileId,
            conv.CreatedAt, conv.UpdatedAt, messages));
    }

    private static async Task<IResult> DeleteConversation(
        string id,
        IConversationRepository repo,
        CancellationToken ct)
    {
        await repo.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    // ─── Manual title update ──────────────────────────────────────────────────

    private static async Task<IResult> SetTitle(
        string id,
        SetTitlePayload payload,
        IConversationRepository repo,
        CancellationToken ct)
    {
        var conv = await repo.GetAsync(id, ct);
        if (conv is null) return Results.NotFound();
        await repo.SetTitleAsync(id, payload.Title, ct);
        return Results.NoContent();
    }

    // ─── SSE Streaming ────────────────────────────────────────────────────────

    private static async Task SendMessage(
        string id,
        SendMessageRequest request,
        IConversationRepository repo,
        IModelProviderService providerService,
        WingmanChatAgent agent,
        IRunRepository runRepository,
        IProjectRepository projectRepository,
        IRunWorkspaceManager workspaceManager,
        IChangeSetService changeSets,
        ILlmCompletionService llm,
        ILlmTelemetryRecorder telemetry,
        IAuditEventRecorder audit,
        IOptions<LlmTelemetryOptions> telemetryOptions,
        HttpContext http,
        CancellationToken ct)
    {
        var conv = await repo.GetAsync(id, ct);
        if (conv is null)
        {
            http.Response.StatusCode = 404;
            return;
        }

        // 記錄是否為第一次對話（用於決定是否生成 AI 標題）
        var isFirstExchange = conv.Messages.Count == 0;

        // 取得 provider profile
        var profile = await providerService.GetProfileAsync(
            request.ProviderProfileId ?? conv.ProviderProfileId, ct);
        var agentMode = ParseAgentMode(request.AgentMode);
        var project = string.IsNullOrWhiteSpace(request.ProjectId)
            ? null
            : await projectRepository.GetAsync(request.ProjectId, ct);
        if (!string.IsNullOrWhiteSpace(request.ProjectId) && project is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var workspacePath = project?.RootPath;
        var workspaceStrategy = workspacePath is null
            ? WorkspaceStrategy.Direct
            : workspaceManager.ResolveStrategy(workspacePath, WorkspaceStrategy.Direct);
        var run = new RunEntity
        {
            SessionId = id,
            UserMessage = request.UserMessage,
            ProviderProfileId = profile.Id,
            WorkspacePath = workspacePath,
            ProjectId = project?.Id,
            Mode = agentMode,
            WorkspaceStrategy = workspaceStrategy,
            IncludeUncommittedChanges = request.IncludeUncommittedChanges,
        };
        run.Status = RunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        await runRepository.SaveAsync(run, ct);

        var effectiveWorkspacePath = workspacePath;
        if (workspacePath is not null && agentMode is AgentMode.Auto or AgentMode.FullAuto)
        {
            var prepared = await workspaceManager.PrepareAsync(run, ct);
            run.ExecutionWorkspacePath = prepared.Path;
            run.Branch = prepared.Branch;
            run.BaseRevision = prepared.BaseRevision;
            effectiveWorkspacePath = prepared.Path ?? workspacePath;
            run.CheckpointId = await changeSets.CreateCheckpointAsync(run.Id, effectiveWorkspacePath, ct);
            await runRepository.SaveAsync(run, ct);
        }

        // 儲存使用者訊息
        var userMessageId = await repo.AddMessageAsync(id, MessageRole.User, request.UserMessage, ct);

        // 若仍為預設標題，自動設定
        if (conv.Title == "新對話")
        {
            var title = request.UserMessage.Length > 50
                ? request.UserMessage[..50] + "…"
                : request.UserMessage;
            await repo.SetTitleAsync(id, title, ct);
        }

        // ── SSE 標頭 ──────────────────────────────────────────────────────────
        http.Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
        http.Response.Headers["Cache-Control"] = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Response.Headers["Access-Control-Allow-Origin"] = "*";

        TokenUsage? tokenUsage = null;
        var fullResponse = new System.Text.StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        var firstTokenSeen = false;
        var tokenEventCount = 0;
        var interTokenTotalMs = 0L;
        DateTimeOffset? lastTokenAt = null;
        var requestedModelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? profile.ModelId
            : request.ModelId;
        run.ResolvedModelId = requestedModelId;
        await runRepository.SaveAsync(run, ct);
        var fallbackTargets = await BuildFallbackTargetsAsync(
            providerService,
            profile,
            requestedModelId,
            telemetryOptions.Value,
            ct);
        var targetIndex = 0;
        var attemptsForTarget = 0;

        var telemetryHandle = await telemetry.StartRequestAsync(
            new LlmTelemetryRequestStart(
                new LlmTelemetryContext(
                    FeatureArea: "chat",
                    ConversationId: id,
                    MessageId: userMessageId,
                    RunId: run.Id,
                    TraceId: run.TraceId),
                profile,
                requestedModelId,
                IsStreaming: true,
                Prompt: request.UserMessage),
            CancellationToken.None);

        var timeoutOptions = telemetryOptions.Value;
        var firstTokenTimeout = ToTimeout(timeoutOptions.FirstTokenTimeoutSeconds, 60);
        var idleStreamTimeout = ToTimeout(timeoutOptions.IdleStreamTimeoutSeconds, 120);
        var totalRequestTimeout = ToTimeout(timeoutOptions.TotalRequestTimeoutSeconds, 600);
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalCts.CancelAfter(totalRequestTimeout);

        try
        {
            await WriteSseAsync(
                http,
                new { runId = run.Id, agentMode = ToWireValue(agentMode),resolvedProviderId=profile.Id,resolvedModelId=requestedModelId },
                ct);
            for (;;)
            {
                attemptsForTarget++;
                using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
                streamCts.CancelAfter(firstTokenTimeout);
                try
                {
                    await foreach (var token in agent.RunStreamingAsync(
                        request.UserMessage, conv.Messages, profile, requestedModelId,
                        mode: agentMode, workspacePath: effectiveWorkspacePath, runId: run.Id,
                        onUsage: u => tokenUsage = u,
                        onTimeline: (timeline,token) => WriteSseAsync(http,new{timeline},token),
                        attachments: request.Attachments,
                        ct: streamCts.Token))
                    {
                        var tokenAt = DateTimeOffset.UtcNow;
                        if (!firstTokenSeen){firstTokenSeen=true;await telemetry.MarkFirstTokenAsync(telemetryHandle,tokenAt,CancellationToken.None);}
                        else if(lastTokenAt is not null)interTokenTotalMs+=Math.Max(0,(long)(tokenAt-lastTokenAt.Value).TotalMilliseconds);
                        lastTokenAt=tokenAt;tokenEventCount++;streamCts.CancelAfter(idleStreamTimeout);fullResponse.Append(token);await WriteSseAsync(http,new{token},ct);
                    }
                    break;
                }
                catch(OperationCanceledException)when(!ct.IsCancellationRequested&&!totalCts.IsCancellationRequested&&!firstTokenSeen)
                {
                    var maxRetries=Math.Max(0,timeoutOptions.MaxFirstTokenRetriesPerTarget);
                    if(attemptsForTarget<=maxRetries)
                    {
                        telemetryHandle=await telemetry.RetryAsync(telemetryHandle,profile,requestedModelId,LlmTimeoutKind.FirstToken,CancellationToken.None);
                        await WriteSseAsync(http,new{retrying=true,attempt=attemptsForTarget+1,reason=LlmTimeoutKind.FirstToken,resolvedProviderId=profile.Id,resolvedModelId=requestedModelId},ct);
                        continue;
                    }
                    if(targetIndex+1>=fallbackTargets.Count)throw;
                    var previousProvider=profile.Id;var previousModel=requestedModelId;
                    targetIndex++;(profile,requestedModelId)=fallbackTargets[targetIndex];attemptsForTarget=0;
                    telemetryHandle=await telemetry.RetryAsync(telemetryHandle,profile,requestedModelId,"fallback_after_first_token_timeout",CancellationToken.None);
                    await audit.RecordAsync(new AuditEventWrite("provider_fallback","agent_run",run.Id,"fallback","success","system",TraceId:run.TraceId,DetailsJson:JsonSerializer.Serialize(new{fromProvider=previousProvider,fromModel=previousModel,toProvider=profile.Id,toModel=requestedModelId,reason=LlmTimeoutKind.FirstToken,additionalCostPossible=true})),CancellationToken.None);
                    await WriteSseAsync(http,new{runId=run.Id,fallback=true,reason=LlmTimeoutKind.FirstToken,resolvedProviderId=profile.Id,resolvedModelId=requestedModelId},ct);
                }
            }

            stopwatch.Stop();
            var completedAt = DateTimeOffset.UtcNow;
            await telemetry.CompleteRequestAsync(
                telemetryHandle,
                new LlmTelemetryCompletion(
                    Response: fullResponse.ToString(),
                    Usage: tokenUsage,
                    CompletedAt: completedAt,
                    DurationMs: stopwatch.ElapsedMilliseconds,
                    TimeToLastByteMs: stopwatch.ElapsedMilliseconds,
                    AvgInterTokenMs: tokenEventCount > 1
                        ? interTokenTotalMs / Math.Max(1, tokenEventCount - 1)
                        : null,
                    TokensPerSecond: CalculateTokensPerSecond(tokenUsage, stopwatch.Elapsed),
                    ResolvedModelId: requestedModelId),
                CancellationToken.None);
            run.Status = RunStatus.Completed;
            run.EndedAt = completedAt;
            await runRepository.SaveAsync(run, CancellationToken.None);

            // usage 事件（若 LLM 有回傳耗用 token 資訊）
            if (tokenUsage is not null)
            {
                await WriteSseAsync(http, new
                {
                    usage = new
                    {
                        inputTokens = tokenUsage.InputTokens,
                        outputTokens = tokenUsage.OutputTokens,
                        totalTokens = tokenUsage.TotalTokens,
                    },
                }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            await telemetry.FailRequestAsync(
                telemetryHandle,
                new LlmTelemetryFailure(
                    LlmTelemetryStatus.Cancelled,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    TimeoutKind: null,
                    ErrorType: "client_cancelled",
                    ErrorCode: null,
                    HttpStatus: null,
                    ErrorMessage: "Client cancelled the streaming request"),
                CancellationToken.None);
            run.Status = RunStatus.Cancelled;
            run.EndedAt = DateTimeOffset.UtcNow;
            await runRepository.SaveAsync(run, CancellationToken.None);
            await WriteSseAsync(http, new { cancelled = true }, CancellationToken.None);
            return;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            var timeoutKind = ResolveTimeoutKind(firstTokenSeen, totalRequestTimeout, stopwatch.Elapsed);
            var message = BuildTimeoutMessage(timeoutKind, profile.DisplayName, requestedModelId);
            await telemetry.FailRequestAsync(
                telemetryHandle,
                new LlmTelemetryFailure(
                    LlmTelemetryStatus.TimedOut,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    timeoutKind,
                    ErrorType: "timeout",
                    ErrorCode: timeoutKind,
                    HttpStatus: null,
                    ErrorMessage: message,
                    RetryReason: timeoutKind),
                CancellationToken.None);
            run.Status = RunStatus.Failed;
            run.Error = message;
            run.EndedAt = DateTimeOffset.UtcNow;
            await runRepository.SaveAsync(run, CancellationToken.None);
            await WriteSseAsync(http, new { error = message }, CancellationToken.None);
            return;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await telemetry.FailRequestAsync(
                telemetryHandle,
                new LlmTelemetryFailure(
                    LlmTelemetryStatus.Failed,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    TimeoutKind: null,
                    ErrorType: "provider_error",
                    ErrorCode: ex.GetType().Name,
                    HttpStatus: null,
                    ErrorMessage: ex.Message),
                CancellationToken.None);
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
            run.EndedAt = DateTimeOffset.UtcNow;
            await runRepository.SaveAsync(run, CancellationToken.None);
            await WriteSseAsync(http, new { error = ex.Message }, CancellationToken.None);
            return;
        }

        // ── 儲存完整助理回應 ──────────────────────────────────────────────────
        if (fullResponse.Length > 0)
        {
            await repo.AddMessageAsync(id, MessageRole.Assistant, fullResponse.ToString(),
                CancellationToken.None);
        }

        // ── 第一次對話：以 AI 生成摘要標題取代截斷的使用者文字 ─────────────────
        if (isFirstExchange && fullResponse.Length > 0)
        {
            try
            {
                using var titleCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var userSnippet = Truncate(request.UserMessage, 300);
                var aiSnippet = Truncate(fullResponse.ToString(), 400);
                var titlePrompt = $"""
                    以下是一段對話的內容，請用 10 個字以內的**繁體中文**為這段對話取一個摘要標題。
                    只輸出標題本身，不加引號、句號、問號、驚嘆號或任何其他標點符號。

                    使用者：{userSnippet}
                    AI：{aiSnippet}
                    """;
                var aiTitle = (await llm.CompleteAsync(
                        titlePrompt,
                        new LlmTelemetryContext(
                            FeatureArea: "title_generation",
                            ConversationId: id,
                            ParentRequestId: telemetryHandle?.RequestLogId,
                            TraceId: telemetryHandle?.TraceId),
                        titleCts.Token))
                    .Trim()
                    .TrimEnd('。', '，', '、', '！', '？', '"', '"', '\'', '"')
                    .Split('\n')[0]
                    .Trim();
                if (aiTitle.Length > 30) aiTitle = aiTitle[..30] + "…";
                if (!string.IsNullOrWhiteSpace(aiTitle))
                    await repo.SetTitleAsync(id, aiTitle, CancellationToken.None);
            }
            catch
            {
                // 非致命：標題保持為截斷的使用者文字
            }
        }

        // ── 串流結束事件（在標題更新後才送，確保客戶端 loadConversations 拿到新標題）─
        await WriteSseAsync(http, new { done = true }, CancellationToken.None);
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";

    private static TimeSpan ToTimeout(int seconds, int fallbackSeconds) =>
        TimeSpan.FromSeconds(Math.Max(1, seconds > 0 ? seconds : fallbackSeconds));

    internal static string ResolveTimeoutKind(
        bool firstTokenSeen,
        TimeSpan totalTimeout,
        TimeSpan elapsed)
    {
        if (elapsed >= totalTimeout - TimeSpan.FromSeconds(1))
            return LlmTimeoutKind.TotalRequest;

        return firstTokenSeen ? LlmTimeoutKind.IdleStream : LlmTimeoutKind.FirstToken;
    }

    internal static bool CanAutomaticallyRetryTimeout(string timeoutKind,bool firstTokenSeen) =>
        !firstTokenSeen && string.Equals(timeoutKind,LlmTimeoutKind.FirstToken,StringComparison.Ordinal);

    private static string BuildTimeoutMessage(
        string timeoutKind,
        string providerName,
        string? modelId) =>
        timeoutKind switch
        {
            LlmTimeoutKind.FirstToken =>
                $"模型回應逾時：{providerName} / {modelId ?? "(default)"} 超過等待上限仍未回傳第一個 token。",
            LlmTimeoutKind.IdleStream =>
                $"模型串流逾時：{providerName} / {modelId ?? "(default)"} 已開始回應，但中途太久沒有新內容。",
            _ =>
                $"模型請求逾時：{providerName} / {modelId ?? "(default)"} 超過總等待上限。",
        };

    private static double? CalculateTokensPerSecond(TokenUsage? usage, TimeSpan elapsed)
    {
        if (usage?.OutputTokens is not > 0 || elapsed.TotalSeconds <= 0)
            return null;
        return Math.Round(usage.OutputTokens / elapsed.TotalSeconds, 2);
    }

    private static async Task<IReadOnlyList<(ModelProviderProfile Profile, string? ModelId)>>
        BuildFallbackTargetsAsync(
            IModelProviderService providerService,
            ModelProviderProfile initialProfile,
            string? initialModelId,
            LlmTelemetryOptions options,
            CancellationToken ct)
    {
        var targets = new List<(ModelProviderProfile Profile, string? ModelId)>
        {
            (initialProfile, initialModelId),
        };
        if (!options.FallbackEnabled)
            return targets;

        foreach (var configured in options.FallbackTargets)
        {
            if (string.IsNullOrWhiteSpace(configured.ProviderProfileId))
                continue;
            if (!options.AllowCrossProviderFallback &&
                !string.Equals(
                    configured.ProviderProfileId,
                    initialProfile.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = await providerService.GetProfileAsync(configured.ProviderProfileId, ct);
            var modelId = string.IsNullOrWhiteSpace(configured.ModelId)
                ? candidate.ModelId
                : configured.ModelId;
            if (targets.Any(target =>
                    string.Equals(target.Profile.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(target.ModelId, modelId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            targets.Add((candidate, modelId));
        }
        return targets;
    }

    private static AgentMode ParseAgentMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "ask" => AgentMode.Ask,
        "auto" => AgentMode.Auto,
        "full_auto" or "fullauto" => AgentMode.FullAuto,
        _ => AgentMode.Plan,
    };

    private static string ToWireValue(AgentMode mode) => mode switch
    {
        AgentMode.Ask => "ask",
        AgentMode.Plan => "plan",
        AgentMode.Auto => "auto",
        AgentMode.FullAuto => "full_auto",
        _ => "plan",
    };

    private static async Task WriteSseAsync(
        HttpContext http,
        object payload,
        CancellationToken ct)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    private sealed record CreateConversationPayload(string? ProviderProfileId = null);
    private sealed record SetTitlePayload(string Title);
}
