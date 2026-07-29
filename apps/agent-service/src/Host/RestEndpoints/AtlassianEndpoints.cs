using System.Text;
using System.Text.Json;
using AgentService.Application.Atlassian;
using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Atlassian;
using AgentService.Infrastructure.AgentFramework;

namespace AgentService.Host.RestEndpoints;

/// <summary>
/// Atlassian 連線設定與 JIRA 分析 REST 端點。
///
/// GET    /api/atlassian/settings              → 取得已設定的連線摘要（不含 Token）
/// POST   /api/atlassian/connections/{type}/validate  → 驗證並儲存連線
/// DELETE /api/atlassian/connections/{type}           → 刪除連線
/// POST   /api/atlassian/jira/preview          → 取得議題預覽（使用者確認用）
/// POST   /api/atlassian/jira/analyze          → 完整分析（SSE 串流）
/// </summary>
public static class AtlassianEndpoints
{
    public sealed record ValidateConnectionRequest(
        string BaseUrl,
        string AuthType,
        string? Username,
        string? Token,   // 新 Token；留空表示沿用已儲存值
        string? ApiVersion);

    public sealed record PreviewJiraIssueRequest(string JiraKey);

    public sealed record AnalyzeJiraIssueRequest(
        string ProjectId,
        string JiraKey,
        string? ProviderProfileId);

    public static IEndpointRouteBuilder MapAtlassianEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/atlassian");
        group.MapGet("/settings", GetSettings);
        group.MapPost("/connections/{serviceType}/validate", ValidateAndSave);
        group.MapDelete("/connections/{serviceType}", DeleteConnection);
        group.MapPost("/jira/preview", PreviewIssue);
        group.MapPost("/jira/analyze", AnalyzeIssue);
        return app;
    }

    // ── GET /api/atlassian/settings ──────────────────────────────────────────

    private static async Task<IResult> GetSettings(
        IAtlassianConnectionRepository repo,
        CancellationToken ct)
    {
        var jira = await repo.GetAsync(AtlassianServiceType.Jira, ct);
        var wiki = await repo.GetAsync(AtlassianServiceType.Wiki, ct);
        return Results.Ok(new
        {
            jira = jira is null ? null : ToDto(jira),
            wiki = wiki is null ? null : ToDto(wiki),
        });
    }

    // ── POST /api/atlassian/connections/{type}/validate ──────────────────────

    private static async Task<IResult> ValidateAndSave(
        string serviceType,
        ValidateConnectionRequest request,
        IAtlassianConnectionRepository repo,
        IJiraHttpClient jiraClient,
        CancellationToken ct)
    {
        if (!Enum.TryParse<AtlassianServiceType>(serviceType, true, out var svcType))
            return Results.BadRequest(new { error = "serviceType 必須為 jira 或 wiki。" });

        if (!Enum.TryParse<AtlassianAuthType>(request.AuthType, true, out var authType))
            return Results.BadRequest(new { error = "authType 必須為 Bearer 或 Basic。" });

        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return Results.BadRequest(new { error = AtlassianErrorCodes.InvalidUrl });

        if (authType == AtlassianAuthType.Basic && string.IsNullOrWhiteSpace(request.Username))
            return Results.BadRequest(new { error = "Basic 驗證方式必須提供使用者名稱。" });

        // 讀取既有設定以備 Token 留空時沿用
        var existing = await repo.GetAsync(svcType, ct);

        // 若未提供新 Token，使用既有解密後的 Token；若兩者都無 → 回傳錯誤
        var tokenToTest = !string.IsNullOrWhiteSpace(request.Token)
            ? request.Token
            : existing?.SecretValue;

        if (string.IsNullOrWhiteSpace(tokenToTest))
            return Results.BadRequest(new { error = AtlassianErrorCodes.SecretNotFound });

        var testConn = new AtlassianConnection
        {
            ServiceType = svcType,
            BaseUrl = request.BaseUrl.TrimEnd('/'),
            AuthType = authType,
            Username = request.Username?.Trim(),
            SecretValue = tokenToTest,
        };

        // 執行連線驗證（不更動任何既有設定）
        var result = await jiraClient.ValidateConnectionAsync(testConn, ct);
        if (!result.IsSuccess)
            return Results.UnprocessableEntity(new { error = result.ErrorCode, detail = result.ErrorDetail });

        // 驗證成功才儲存
        var toSave = new AtlassianConnection
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            ServiceType = svcType,
            BaseUrl = request.BaseUrl.TrimEnd('/'),
            AuthType = authType,
            Username = request.Username?.Trim(),
            SecretValue = !string.IsNullOrWhiteSpace(request.Token) ? request.Token : null,
            ApiVersion = request.ApiVersion,
            IsVerified = true,
            VerifiedAt = DateTimeOffset.UtcNow,
            VerifiedDisplayName = result.Value,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
        };
        await repo.SaveAsync(toSave, ct);

        return Results.Ok(new
        {
            verified = true,
            displayName = result.Value,
        });
    }

    // ── DELETE /api/atlassian/connections/{type} ─────────────────────────────

    private static async Task<IResult> DeleteConnection(
        string serviceType,
        IAtlassianConnectionRepository repo,
        CancellationToken ct)
    {
        if (!Enum.TryParse<AtlassianServiceType>(serviceType, true, out var svcType))
            return Results.BadRequest(new { error = "serviceType 必須為 jira 或 wiki。" });
        await repo.DeleteAsync(svcType, ct);
        return Results.NoContent();
    }

    // ── POST /api/atlassian/jira/preview ─────────────────────────────────────

    private static async Task<IResult> PreviewIssue(
        PreviewJiraIssueRequest request,
        IAtlassianConnectionRepository repo,
        IJiraHttpClient jiraClient,
        CancellationToken ct)
    {
        var keyValidation = ValidateFullKey(request.JiraKey);
        if (keyValidation is not null)
            return Results.BadRequest(new { error = keyValidation });

        var conn = await repo.GetAsync(AtlassianServiceType.Jira, ct);
        if (conn is null || !conn.HasSecret)
            return Results.UnprocessableEntity(new { error = AtlassianErrorCodes.NotConfigured });
        if (conn.SecretValue is null)
            return Results.UnprocessableEntity(new { error = AtlassianErrorCodes.SecretNotFound });

        var result = await jiraClient.GetIssuePreviewAsync(conn, request.JiraKey.Trim().ToUpperInvariant(), ct);
        if (!result.IsSuccess)
            return Results.UnprocessableEntity(new { error = result.ErrorCode, detail = result.ErrorDetail });

        return Results.Ok(result.Value);
    }

    // ── POST /api/atlassian/jira/analyze （SSE） ─────────────────────────────

    private static async Task AnalyzeIssue(
        AnalyzeJiraIssueRequest request,
        IAtlassianConnectionRepository atlassianRepo,
        IConversationRepository conversations,
        IProjectRepository projects,
        IModelProviderService providers,
        IJiraHttpClient jiraClient,
        JiraAnalysisRunRepository analysisRuns,
        WingmanChatAgent agent,
        ILoggerFactory loggerFactory,
        HttpContext http,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("AtlassianEndpoints");
        var keyValidation = ValidateFullKey(request.JiraKey);
        if (keyValidation is not null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = keyValidation }, ct);
            return;
        }

        var fullKey = request.JiraKey.Trim().ToUpperInvariant();

        if (await projects.GetAsync(request.ProjectId, ct) is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { error = "找不到指定的專案。" }, ct);
            return;
        }

        var conn = await atlassianRepo.GetAsync(AtlassianServiceType.Jira, ct);
        if (conn is null || !conn.HasSecret || conn.SecretValue is null)
        {
            http.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await http.Response.WriteAsJsonAsync(
                new { error = AtlassianErrorCodes.NotConfigured }, ct);
            return;
        }

        // ── 取得完整 JIRA 內容 ────────────────────────────────────────────────
        var issueResult = await jiraClient.GetFullIssueAsync(conn, fullKey, ct);
        if (!issueResult.IsSuccess)
        {
            http.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await http.Response.WriteAsJsonAsync(
                new { error = issueResult.ErrorCode, detail = issueResult.ErrorDetail }, ct);
            return;
        }
        var issue = issueResult.Value!;

        // ── 建立追蹤紀錄 ─────────────────────────────────────────────────────
        var runId = await analysisRuns.CreateAsync(
            request.ProjectId, fullKey, issue.Preview.Summary, issue.Preview.Updated, ct);

        // ── 建立新對話 ───────────────────────────────────────────────────────
        var profile = await providers.GetProfileAsync(request.ProviderProfileId, ct);
        var conversation = await conversations.CreateAsync(
            profile.Id, ConversationScope.Project, request.ProjectId, ct);
        await conversations.SetTitleAsync(
            conversation.Id,
            JiraPromptBuilder.BuildConversationTitle(fullKey, issue.Preview.Summary),
            ct);
        await analysisRuns.SetConversationAsync(runId, conversation.Id, ct);

        // ── 組裝 Prompt 並新增 User 訊息 ─────────────────────────────────────
        // 系統指令以前置方式嵌入 userPrompt；MessageRole 無 System 角色。
        var userPrompt = JiraPromptBuilder.BuildSystemPrompt()
            + "\n\n"
            + JiraPromptBuilder.BuildUserPrompt(issue);
        await conversations.AddMessageAsync(conversation.Id, MessageRole.User, userPrompt, ct);

        // ── 開始 SSE 串流 ─────────────────────────────────────────────────────
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Response.Headers.AccessControlAllowOrigin = "*";

        // 傳送 metadata（conversationId 讓前端可跳轉）
        await WriteSseAsync(http, new
        {
            conversationId = conversation.Id,
            jiraKey = fullKey,
            summary = issue.Preview.Summary,
        }, ct);

        var fullResponse = new StringBuilder();

                

        try
        {
            await foreach (var token in agent.RunStreamingAsync(
                userPrompt,
                conversation.Messages,
                profile,
                profile.ModelId,
                onUsage: _ => { },
                attachments: [],
                includeSkills: false,
                ct: ct))
            {
                fullResponse.Append(token);

                await WriteSseAsync(
                    http,
                    new { token },
                    ct);
            }

            if (fullResponse.Length == 0)
            {
                throw new InvalidOperationException(
                    "AI 分析完成，但沒有產生任何回覆內容。");
            }

            await conversations.AddMessageAsync(
                conversation.Id,
                MessageRole.Assistant,
                fullResponse.ToString(),
                CancellationToken.None);

            await analysisRuns.CompleteAsync(
                runId,
                CancellationToken.None);

            await WriteSseAsync(
                http,
                new
                {
                    done = true,
                    conversationId = conversation.Id
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation(
                "JIRA 分析已取消。RunId={RunId}, ProjectId={ProjectId}, JiraKey={JiraKey}, ConversationId={ConversationId}",
                runId,
                request.ProjectId,
                fullKey,
                conversation.Id);

            await analysisRuns.FailAsync(
                runId,
                AtlassianErrorCodes.AnalysisCancelled,
                CancellationToken.None);

            await TryDeleteConversationAsync(
                conversations,
                conversation.Id,
                logger);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "JIRA 分析失敗。RunId={RunId}, ProjectId={ProjectId}, JiraKey={JiraKey}, ConversationId={ConversationId}",
                runId,
                request.ProjectId,
                fullKey,
                conversation.Id);

            await analysisRuns.FailAsync(
                runId,
                AtlassianErrorCodes.AiAnalysisFailed,
                CancellationToken.None);

            await TryDeleteConversationAsync(
                conversations,
                conversation.Id,
                logger);

            await TryWriteSseErrorAsync(
                http,
                AtlassianErrorCodes.AiAnalysisFailed,
                logger);
        }
    }

    // ── 輔助方法 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 驗證完整 JIRA Key（INNES1HD-1128 格式）；有效時回傳 null，否則回傳 error code。
    /// </summary>
    private static string? ValidateFullKey(string? key)
    {
        if (!Application.Atlassian.JiraKeyValidator.IsValidFullKey(key))
            return AtlassianErrorCodes.JiraKeyInvalid;
        return null;
    }

    private static object ToDto(AtlassianConnection conn) => new
    {
        serviceType = conn.ServiceType.ToString().ToLowerInvariant(),
        baseUrl = conn.BaseUrl,
        authType = conn.AuthType.ToString().ToLowerInvariant(),
        username = conn.Username,
        hasSecret = conn.HasSecret,
        verified = conn.IsVerified,
        verifiedAt = conn.VerifiedAt,
        verifiedDisplayName = conn.VerifiedDisplayName,
    };

    private static async Task WriteSseAsync(HttpContext ctx, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
    private static async Task TryDeleteConversationAsync(
        IConversationRepository conversations,
        string conversationId,
        ILogger logger)
    {
        try
        {
            await conversations.DeleteAsync(
                conversationId,
                CancellationToken.None);

            logger.LogInformation(
                "已刪除未完成的 JIRA 分析對話。ConversationId={ConversationId}",
                conversationId);
        }
        catch (Exception cleanupException)
        {
            logger.LogError(
                cleanupException,
                "刪除未完成的 JIRA 分析對話時發生錯誤。ConversationId={ConversationId}",
                conversationId);
        }
    }
    
    private static async Task TryWriteSseErrorAsync(
        HttpContext http,
        string errorCode,
        ILogger logger)
    {
        try
        {
            if (http.RequestAborted.IsCancellationRequested)
                return;

            await WriteSseAsync(
                http,
                new { error = errorCode },
                CancellationToken.None);
        }
        catch (Exception writeException)
        {
            logger.LogWarning(
                writeException,
                "傳送 JIRA 分析錯誤事件至前端時失敗。ErrorCode={ErrorCode}",
                errorCode);
        }
    }
}
