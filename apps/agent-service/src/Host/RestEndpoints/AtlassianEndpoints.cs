using System.Text;
using System.Text.Json;
using AgentService.Application.Atlassian;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Atlassian;
using AgentService.Infrastructure.AgentRuntime;
using AgentService.Modules.GraphRAG;
using AgentRuntimeService = AgentService.Infrastructure.AgentRuntime.AgentRuntime;

namespace AgentService.Host.RestEndpoints;

/// <summary>
/// Atlassian 連線設定與 JIRA 分析 REST 端點。
///
/// GET    /api/atlassian/settings              → 取得已設定的連線摘要（不含 Token）
/// POST   /api/atlassian/connections/{type}/validate  → 驗證並儲存連線
/// DELETE /api/atlassian/connections/{type}           → 刪除連線
/// POST   /api/atlassian/jira/preview          → 取得議題預覽（使用者確認用）
/// POST   /api/projects/{projectId}/analysis/jira → 完整專案分析（SSE 串流）
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

    /// <summary>啟動專案 JIRA 議題分析時使用的輸入資料。</summary>
    /// <param name="JiraKey">待分析的 JIRA 議題鍵值。</param>
    /// <param name="ProviderProfileId">指定的模型供應商設定識別碼。</param>
    /// <param name="LocalFileKey">本機測試模式使用的檔案鍵值。</param>
    /// <param name="ModelId">使用者選擇的模型；未提供時使用供應商預設值。</param>
    public sealed record AnalyzeJiraIssueRequest(
        string JiraKey,
        string? ProviderProfileId,
        string? LocalFileKey = null,
        string? ModelId = null);

    public static IEndpointRouteBuilder MapAtlassianEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/atlassian");
        group.MapGet("/settings", GetSettings);
        group.MapPost("/connections/{serviceType}/validate", ValidateAndSave);
        group.MapDelete("/connections/{serviceType}", DeleteConnection);
        group.MapPost("/jira/preview", PreviewIssue);
        group.MapGet("/jira/local-files", ListLocalFiles);
        app.MapPost("/api/projects/{projectId}/analysis/jira", AnalyzeIssue);
        return app;
    }

    // ── 讀取 Atlassian 設定 ─────────────────────────────────────────────────

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

    // ── 驗證指定類型的 Atlassian 連線 ───────────────────────────────────────

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
        var existing = await repo.GetForUseAsync(svcType, ct);

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

    // ── 刪除指定類型的 Atlassian 連線 ───────────────────────────────────────

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

    // ── 列出 JIRA 本機測試檔案 ──────────────────────────────────────────────

    private static IResult ListLocalFiles(LocalJiraFileRepository localFiles) =>
        Results.Ok(localFiles.ListFiles());

    // ── 預覽 JIRA 議題 ──────────────────────────────────────────────────────

    private static async Task<IResult> PreviewIssue(
        PreviewJiraIssueRequest request,
        IAtlassianConnectionRepository repo,
        IJiraHttpClient jiraClient,
        LocalJiraFileRepository localFiles,
        CancellationToken ct)
    {
        var keyValidation = ValidateFullKey(request.JiraKey);
        if (keyValidation is not null)
            return Results.BadRequest(new { error = keyValidation });

        var fullKey = request.JiraKey.Trim().ToUpperInvariant();

        // 本機檔案模式：若找到對應 JSON，直接回傳 Preview（跳過 JIRA 連線）
        var localIssue = localFiles.Load(fullKey);
        if (localIssue is not null)
            return Results.Ok(localIssue.Preview);

        var conn = await repo.GetForUseAsync(AtlassianServiceType.Jira, ct);
        if (conn is null || !conn.HasSecret)
            return Results.UnprocessableEntity(new { error = AtlassianErrorCodes.NotConfigured });
        if (conn.SecretValue is null)
            return Results.UnprocessableEntity(new { error = AtlassianErrorCodes.SecretNotFound });

        var result = await jiraClient.GetIssuePreviewAsync(conn, fullKey, ct);
        if (!result.IsSuccess)
            return Results.UnprocessableEntity(new { error = result.ErrorCode, detail = result.ErrorDetail });

        return Results.Ok(result.Value);
    }

    // ── 以 SSE 串流分析專案的 JIRA 議題 ─────────────────────────────────────

    private static async Task AnalyzeIssue(
        string projectId,
        AnalyzeJiraIssueRequest request,
        IAtlassianConnectionRepository atlassianRepo,
        IConversationRepository conversations,
        IProjectRepository projects,
        IModelProviderService providers,
        IJiraHttpClient jiraClient,
        JiraAnalysisRunRepository analysisRuns,
        JiraFeatureIdentifierExtractor featureExtractor,
        JiraGraphRagRetrievalService graphRagRetrieval,
        LocalJiraFileRepository localFiles,
        INeo4jRuntime neo4j,
        AgentRuntimeService agent,
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

        if (await projects.GetAsync(projectId, ct) is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { error = "找不到指定的專案。" }, ct);
            return;
        }

        // ── 取得完整 JIRA 內容（本機檔案或遠端連線） ─────────────────────────
        NormalizedJiraIssue issue;

        // 明確指定 localFileKey，或 LocalJiraFiles.Enabled = true 且存在對應本機檔案時，
        // 直接從本機讀取，略過 JIRA 連線。
        var localFileKey = !string.IsNullOrWhiteSpace(request.LocalFileKey)
            ? request.LocalFileKey!.Trim()
            : null;
        var localIssue = localFileKey is not null
            ? localFiles.Load(localFileKey)
            : localFiles.Load(fullKey);   // Enabled=false 時 Load() 直接回傳 null，無副作用

        if (localIssue is not null)
        {
            issue = localIssue;
            logger.LogInformation(
                "以本機檔案模式載入 JIRA 議題。Key={Key}, ProjectId={ProjectId}",
                fullKey, projectId);
        }
        else
        {
            if (localFileKey is not null)
            {
                // 明確要求本機檔案但找不到 → 回傳錯誤
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(
                    new { error = $"找不到本機 JIRA 檔案：{localFileKey}.json" }, ct);
                return;
            }

            var conn = await atlassianRepo.GetForUseAsync(AtlassianServiceType.Jira, ct);
            if (conn is null || !conn.HasSecret || conn.SecretValue is null)
            {
                http.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await http.Response.WriteAsJsonAsync(
                    new { error = AtlassianErrorCodes.NotConfigured }, ct);
                return;
            }

            var issueResult = await jiraClient.GetFullIssueAsync(conn, fullKey, ct);
            if (!issueResult.IsSuccess)
            {
                http.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await http.Response.WriteAsJsonAsync(
                    new { error = issueResult.ErrorCode, detail = issueResult.ErrorDetail }, ct);
                return;
            }

            issue = issueResult.Value!;
        }

        // ── 建立追蹤紀錄 ─────────────────────────────────────────────────────
        var runId = await analysisRuns.CreateAsync(
            projectId, fullKey, issue.Preview.Summary, issue.Preview.Updated, ct);

        // ── 建立新對話 ───────────────────────────────────────────────────────
        var profile = await providers.GetProfileAsync(request.ProviderProfileId, ct);
        var resolvedModelId = !string.IsNullOrWhiteSpace(request.ModelId)
            ? request.ModelId : profile.ModelId;
        var conversation = await conversations.CreateProjectAsync(
            projectId,
            profile.Id,
            ct);
        await conversations.SetTitleAsync(
            conversation.Id,
            JiraPromptBuilder.BuildConversationTitle(fullKey, issue.Preview.Summary),
            ct);
        await analysisRuns.SetConversationAsync(runId, conversation.Id, ct);

        // ── 開始 SSE 串流 ─────────────────────────────────────────────────────
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Response.Headers.AccessControlAllowOrigin = "*";

        await WriteSseAsync(http, new
        {
            conversationId = conversation.Id,
            jiraKey = fullKey,
            summary = issue.Preview.Summary,
        }, ct);

        var fullResponse = new StringBuilder();

        try
        {
            // ── 辨識功能代號與功能名稱 ──────────────────────────────────────────
            await WriteSseProgressAsync(http, "辨識功能代號與功能名稱", ct);
            var identifiers = featureExtractor.Extract(issue);

            logger.LogInformation(
                "JIRA 功能識別完成。RunId={RunId}, ProjectId={ProjectId}, JiraKey={JiraKey}, FeatureCount={FeatureCount}",
                runId, projectId, fullKey, identifiers.Count);

            // ── GraphRAG 三階段檢索 ─────────────────────────────────────────────
            JiraGraphRagContext graphRagContext;
            bool neo4jAvailable = await neo4j.EnsureAvailableAsync(null, ct);

            if (!neo4jAvailable)
            {
                graphRagContext = JiraGraphRagContext.Degraded("Neo4j 圖譜資料庫目前無法連線，改以純 JIRA 內容繼續分析。");
                await WriteSseProgressAsync(http, "GraphRAG 資料庫無法連線，改以純 JIRA 內容繼續分析", ct);
            }
            else
            {
                await WriteSseProgressAsync(http, "搜尋功能 Controller 與程式入口", ct);
                graphRagContext = await graphRagRetrieval.RetrieveAsync(
                    projectId, issue, identifiers, ct);

                if (graphRagContext.HasResults)
                {
                    var entryCount = graphRagContext.ConfirmedEntryPoints.Count
                        + graphRagContext.CandidateEntryPoints.Count;
                    await WriteSseProgressAsync(
                        http,
                        $"已找到 {entryCount} 個程式入口，展開 {graphRagContext.IncludedHitCount} 個相關節點",
                        ct);
                }
                else
                {
                    await WriteSseProgressAsync(http, "未找到相關程式碼，本次以 JIRA 內容繼續分析", ct);
                }

                logger.LogInformation(
                    "JIRA GraphRAG 完成。RunId={RunId}, ProjectId={ProjectId}, JiraKey={JiraKey}, ConfirmedEntries={Confirmed}, CandidateEntries={Candidates}, Hits={Hits}, Degraded={Degraded}",
                    runId, projectId, fullKey,
                    graphRagContext.ConfirmedEntryPoints.Count,
                    graphRagContext.CandidateEntryPoints.Count,
                    graphRagContext.IncludedHitCount,
                    graphRagContext.WasDegraded);
            }

            // ── 組裝 Prompt 並新增 User 訊息 ─────────────────────────────────────
            await WriteSseProgressAsync(http, "整理 GraphRAG 分析內容，建立專案對話", ct);

            var systemPrompt = graphRagContext.HasResults
                ? JiraPromptBuilder.BuildSystemPromptWithGraphRAG()
                : JiraPromptBuilder.BuildSystemPrompt();

            var taskPrompt = graphRagContext.HasResults
                ? JiraPromptBuilder.BuildUserPromptWithGraphRAG(issue, identifiers, graphRagContext)
                : JiraPromptBuilder.BuildUserPrompt(issue);

            var userPrompt = systemPrompt + "\n\n" + taskPrompt;
            await conversations.AddMessageAsync(conversation.Id, MessageRole.User, userPrompt, ct);

            // ── AI 生成三項分析 ───────────────────────────────────────────────────
            await WriteSseProgressAsync(http, "AI 生成三項分析", ct);

            await foreach (var token in agent.RunStreamingAsync(
                new AgentExecutionRequest(
                    userPrompt,
                    conversation.Messages,
                    profile,
                    resolvedModelId,
                    Attachments: [],
                    Instructions: systemPrompt,
                    SkillsPrompt: string.Empty,
                    Tools: []),
                ct))
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
                projectId,
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
            if (ex.Message.Contains("quota") && ex.Message.Contains("exceeded"))
            {
                logger.LogInformation(
                    "JIRA 分析已取消（例外訊息包含 'quota_exceeded'）。RunId={RunId}, ProjectId={ProjectId}, JiraKey={JiraKey}, ConversationId={ConversationId}",
                    runId,
                    projectId,
                    fullKey,
                    conversation.Id);

                await analysisRuns.FailAsync(
                    runId,
                    AtlassianErrorCodes.AnalysisQuotaExceeded,
                    CancellationToken.None);

                await TryDeleteConversationAsync(
                    conversations,
                    conversation.Id,
                    logger);

                await TryWriteSseErrorAsync(
                    http,
                    AtlassianErrorCodes.AnalysisQuotaExceeded,
                    logger);

                return;
            }
            else {
                logger.LogError(
                    ex,
                    "JIRA 分析失敗。RunId={RunId}, ProjectId={ProjectId}, JiraKey={JiraKey}, ConversationId={ConversationId}",
                    runId,
                    projectId,
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

    private static Task WriteSseProgressAsync(HttpContext ctx, string message, CancellationToken ct) =>
        WriteSseAsync(ctx, new { progress = message }, ct);
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
