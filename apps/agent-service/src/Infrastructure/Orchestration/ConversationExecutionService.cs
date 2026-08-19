using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.AgentRuntime;
using AgentService.Infrastructure.Skills;
using Microsoft.Extensions.Options;
using AgentRuntimeService = AgentService.Infrastructure.AgentRuntime.AgentRuntime;

namespace AgentService.Infrastructure.Orchestration;

/// <summary>
/// 執行已經由一般對話或專案解析服務準備好的 Agent 請求。
/// 此服務不判斷對話範圍，也不直接查詢 GraphRAG；它只負責訊息持久化、Agent 串流、
/// 使用量回報與背景標題更新。
/// </summary>
public sealed class ConversationExecutionService(
    IConversationRepository conversations,
    AgentRuntimeService agent,
    ISkillProvider skillProvider,
    IProjectJobQueue projectJobs,
    IServiceScopeFactory scopeFactory,
    IOptions<ConversationRuntimeOptions> runtimeOptions,
    ILogger<ConversationExecutionService> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ConversationGates =
        new(StringComparer.Ordinal);

    /// <summary>執行單次對話並逐一回傳可安全送到前端的事件。</summary>
    public async IAsyncEnumerable<ConversationStreamEvent> ExecuteAsync(
        ConversationExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<ConversationStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var execution = ExecuteCoreAsync(request, channel.Writer, ct);

        try
        {
            // 讀取端必須使用請求取消 Token；使用 CancellationToken.None 會在瀏覽器
            // 中斷連線後永遠等待 producer，造成「取消對話卡死」。
            while (true)
            {
                bool hasData;
                try
                {
                    hasData = await channel.Reader.WaitToReadAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 使用者中止 SSE 是正常控制流程，不應將 TaskCanceledException
                    // 再拋回 HTTP 端點或前端；producer 仍會在 finally 清理。
                    break;
                }

                if (!hasData)
                    break;
                while (channel.Reader.TryRead(out var item))
                    yield return item;
            }
        }
        finally
        {
            // 無論讀取端先取消或正常讀完，都等待 producer 清理 semaphore 與 channel。
            try { await execution; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private async Task ExecuteCoreAsync(
        ConversationExecutionRequest request,
        ChannelWriter<ConversationStreamEvent> writer,
        CancellationToken ct)
    {
        // TurnId 在取得 gate 前就固定，這樣「對話忙碌」事件也能讓前端保留
        // 同一個冪等識別碼；前端重試時不會意外產生第二輪。
        var turnId = string.IsNullOrWhiteSpace(request.Message.TurnId)
            ? Guid.NewGuid().ToString("N")
            : request.Message.TurnId!;
        var conversationGate = ConversationGates.GetOrAdd(
            request.Conversation.Id,
            _ => new SemaphoreSlim(1, 1));
        var gateAcquired = false;
        try
        {
            if (!await conversationGate.WaitAsync(0, ct))
            {
                await writer.WriteAsync(
                    new ConversationErrorEvent(
                        "此對話已有另一輪回答正在執行，請等待完成後再送出訊息。",
                        ConversationErrorCodes.ConversationBusy,
                        Retryable: true,
                        Stage: "concurrency_guard",
                        TurnId: turnId),
                    CancellationToken.None);
                writer.TryComplete();
                return;
            }
            gateAcquired = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            writer.TryComplete();
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var currentStage = "preparing";
        string? currentTool = null;
        var activeToolActivities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var executionTimeout = request.ExecutionTimeout ?? runtimeOptions.Value.ResolveTimeout(
            request.Conversation.ProjectId is not null);
        if (executionTimeout > TimeSpan.Zero && executionTimeout != Timeout.InfiniteTimeSpan)
            executionCts.CancelAfter(executionTimeout);
        var runCt = executionCts.Token;
        var conversation = request.Conversation;
        var firstExchange = conversation.Messages.Count == 0;
        var runId = Guid.NewGuid().ToString("N");
        var fullResponse = new System.Text.StringBuilder();
        TokenUsage? usage = null;
        var activity = new AgentActivityReporter(
            runId,
            value =>
            {
                var isProjectTool = !string.IsNullOrWhiteSpace(value.Tool) &&
                    !string.Equals(value.Tool, "runtime", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(value.Tool, "llm", StringComparison.OrdinalIgnoreCase);
                if (isProjectTool)
                {
                    currentTool = value.Tool;
                    if (string.Equals(value.Status, "started", StringComparison.OrdinalIgnoreCase))
                        activeToolActivities.TryAdd(value.ActivityId, 0);
                    else if (value.Status is "completed" or "failed")
                        activeToolActivities.TryRemove(value.ActivityId, out _);
                    currentStage = activeToolActivities.IsEmpty
                        ? "model_execution"
                        : "tool_execution";
                }
                else if (string.Equals(value.Tool, "llm", StringComparison.OrdinalIgnoreCase))
                {
                    currentStage = "model_execution";
                }
                else if (value.Type.StartsWith("run.", StringComparison.OrdinalIgnoreCase))
                {
                    currentStage = "preparing";
                }
                return writer.WriteAsync(
                    new ConversationActivityStreamEvent(value, turnId),
                    CancellationToken.None).AsTask();
            });
        string? runActivityId = null;
        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            // 已完成回合直接重播資料庫內容，不再次呼叫模型；這處理 SSE 斷線後
            // 前端重試以及使用者重複點擊送出造成的重複執行。
            var existingTurn = await conversations.GetTurnAsync(
                conversation.Id,
                turnId,
                ct);
            if (existingTurn?.Status == ConversationTurnStatus.Completed &&
                existingTurn.AssistantMessageId is not null)
            {
                var savedAssistant = await conversations.GetMessageAsync(
                    existingTurn.AssistantMessageId,
                    ct);
                if (savedAssistant is not null)
                {
                    await writer.WriteAsync(
                        new ConversationStartedEvent(
                            request.Profile.Id,
                            request.ModelId,
                            runId,
                            null,
                            null,
                            null,
                            turnId),
                        CancellationToken.None);
                    await writer.WriteAsync(
                        new ConversationTokenEvent(savedAssistant.Content, turnId),
                        CancellationToken.None);
                    await writer.WriteAsync(
                        new ConversationCompletedEvent(turnId),
                        CancellationToken.None);
                    return;
                }
            }

            if (request.EmitRuntimeActivities)
            {
                runActivityId = await activity.StartAsync(
                    "run.started",
                    "正在分析問題",
                    tool: "runtime",
                    detail: "準備本輪專案證據與唯讀工具");
            }
            var prepareStopwatch = Stopwatch.StartNew();
            var preparation = request.Prepare is null
                ? new ConversationPreparation(
                    request.Message.UserMessage,
                    AgentRuntimeService.GeneralInstructions,
                    SkillPromptBuilder.BuildSkillsPrompt(skillProvider),
                    Tools: [])
                : await request.Prepare(activity, runCt);
            prepareStopwatch.Stop();
            logger.LogInformation(
                "對話準備階段完成。耗時={ElapsedMs}ms，GraphStatus={GraphStatus}，工具數={ToolCount}，" +
                "ConversationId={ConversationId}",
                prepareStopwatch.ElapsedMilliseconds,
                preparation.GraphStatus,
                preparation.Tools.Count,
                conversation.Id);

            currentStage = "persisting_user_message";
            await writer.WriteAsync(
                new ConversationStartedEvent(
                    request.Profile.Id,
                    request.ModelId,
                    runId,
                    preparation.GraphStatus,
                    preparation.GraphWarning,
                    preparation.GraphVersion,
                    turnId),
                CancellationToken.None);

            await conversations.BeginTurnAsync(
                conversation.Id,
                turnId,
                request.Message.UserMessage,
                request.Profile.Id,
                request.ModelId,
                runCt);
            if (conversation.Title == "新對話")
                await conversations.SetTitleAsync(
                    conversation.Id,
                    ShortTitle(request.Message.UserMessage),
                    runCt);

            var streamStopwatch = Stopwatch.StartNew();
            currentStage = "model_execution";
            await foreach (var token in agent.RunStreamingAsync(
                new AgentExecutionRequest(
                    preparation.Prompt,
                    conversation.Messages,
                    request.Profile,
                    request.ModelId,
                    request.Message.Attachments,
                    preparation.Instructions,
                    preparation.SkillsPrompt,
                    preparation.Tools,
                    value => usage = value,
                    activity,
                    request.EmitRuntimeActivities,
                    preparation.MaxToolCalls ?? 8),
                runCt))
            {
                fullResponse.Append(token);
                await writer.WriteAsync(
                    new ConversationTokenEvent(token, turnId),
                    CancellationToken.None);
            }
            streamStopwatch.Stop();
            totalStopwatch.Stop();
            logger.LogInformation(
                "對話執行完成。準備={PrepareMs}ms，LLM 串流={StreamMs}ms，總計={TotalMs}ms，" +
                "ConversationId={ConversationId}",
                prepareStopwatch.ElapsedMilliseconds,
                streamStopwatch.ElapsedMilliseconds,
                totalStopwatch.ElapsedMilliseconds,
                conversation.Id);
            if (preparation.GetToolCallUsage is { } getToolCallUsage)
            {
                // 一次彙總 log，避免要逐行數 tool.completed 才能知道這輪到底呼叫了幾次工具。
                var toolUsage = getToolCallUsage();
                logger.LogInformation(
                    "本輪工具呼叫統計。總次數={TotalToolCalls}，明細={ToolCallBreakdown}，" +
                    "ConversationId={ConversationId}",
                    toolUsage.TotalCalls,
                    string.Join(
                        "、",
                        toolUsage.CallsByCategory
                            .Where(entry => entry.Value > 0)
                            .Select(entry => $"{entry.Key}={entry.Value}")),
                    conversation.Id);
            }

            if (fullResponse.Length == 0)
            {
                // 沒有任何模型文字時不能送出成功完成事件，也不能讓回合永久
                // 停在 Running；否則前端會把空內容誤判成成功，下一次又重複呼叫模型。
                await TryFailTurnAsync(
                    conversation.Id,
                    turnId,
                    ConversationTurnStatus.Failed,
                    "empty_response");
                if (runActivityId is not null)
                    await activity.FailAsync(runActivityId, "模型未回傳內容");
                await writer.WriteAsync(
                    new ConversationErrorEvent(
                        "模型未回傳可用內容，請稍後重試。",
                        ConversationErrorCodes.AgentExecutionFailed,
                        Retryable: true,
                        Stage: currentStage,
                        TurnId: turnId),
                    CancellationToken.None);
                return;
            }

            if (fullResponse.Length > 0)
            {
                currentStage = "persisting_assistant_message";
                var assistantMessageId = await conversations.AddMessageAsync(
                    conversation.Id,
                    MessageRole.Assistant,
                    fullResponse.ToString(),
                    turnId,
                    CancellationToken.None);
                await conversations.CompleteTurnAsync(
                    conversation.Id,
                    turnId,
                    assistantMessageId,
                    CancellationToken.None);
            }

            if (usage is not null)
                await writer.WriteAsync(
                    new ConversationUsageEvent(usage, turnId),
                    CancellationToken.None);

            if (runActivityId is not null)
                await activity.CompleteAsync(runActivityId, "回答已完成");

            // 所有活動的完成事件必須先於 done 送出，否則前端收到 done 後會停止
            // 更新串流訊息，最後的活動完成事件就會被忽略而一直顯示「處理中」。
            await writer.WriteAsync(
                new ConversationCompletedEvent(turnId),
                CancellationToken.None);

            if (firstExchange && fullResponse.Length > 0)
            {
                var titleInput = fullResponse.ToString();
                await projectJobs.EnqueueAsync(async backgroundToken =>
                {
                    using var backgroundScope = scopeFactory.CreateScope();
                    var backgroundConversations = backgroundScope.ServiceProvider
                        .GetRequiredService<IConversationRepository>();
                    var backgroundLlm = backgroundScope.ServiceProvider
                        .GetRequiredService<ILlmCompletionService>();
                    await TryGenerateTitleAsync(
                        conversation.Id,
                        request.Message.UserMessage,
                        titleInput,
                        request.Profile.Id,
                        request.ModelId,
                        backgroundConversations,
                        backgroundLlm,
                        backgroundToken);
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 使用者取消串流時不寫入未完成的 Assistant 回覆。
            await TryFailTurnAsync(
                conversation.Id,
                turnId,
                ConversationTurnStatus.Cancelled,
                "cancelled");
            logger.LogInformation(
                "對話已被取消。已耗時={ElapsedMs}ms，ConversationId={ConversationId}",
                totalStopwatch.ElapsedMilliseconds,
                conversation.Id);
        }
        catch (OperationCanceledException exception) when (executionCts.IsCancellationRequested)
        {
            await TryFailTurnAsync(
                conversation.Id,
                turnId,
                ConversationTurnStatus.Failed,
                ConversationErrorCodes.TurnTimeout);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var failedStage = currentStage;
            var failedTool = currentTool;
            logger.LogWarning(
                exception,
                "對話 Agent 已達整輪執行期限。ConversationId={ConversationId}, RunId={RunId}, " +
                "ProviderId={ProviderId}, ModelId={ModelId}, Stage={Stage}, Tool={Tool}, " +
                "ElapsedMs={ElapsedMs}, TimeoutMs={TimeoutMs}",
                conversation.Id,
                runId,
                request.Profile.Id,
                request.ModelId,
                failedStage,
                failedTool,
                (long)elapsed.TotalMilliseconds,
                (long)executionTimeout.TotalMilliseconds);
            if (runActivityId is not null)
                await activity.FailAsync(runActivityId, "回答逾時");
            await writer.WriteAsync(
                new ConversationErrorEvent(
                    request.Conversation.ProjectId is null
                        ? "一般對話超過本輪執行期限，請稍後重試。"
                        : "專案解析超過本輪執行期限，可重試或縮小查詢範圍。",
                    ConversationErrorCodes.TurnTimeout,
                    Retryable: true,
                    Stage: failedStage,
                    TurnId: turnId),
                CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or TimeoutException)
        {
            await TryFailTurnAsync(
                conversation.Id,
                turnId,
                ConversationTurnStatus.Failed,
                ConversationErrorCodes.DependencyTimeout);
            // 外部模型、SDK 或工具可能使用自己的 CancellationToken 或 TimeoutException。
            // 這不是 Modern Wingman 的整輪期限，必須保留成相依元件逾時，避免誤導使用者。
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var failedStage = currentStage;
            var failedTool = currentTool;
            logger.LogWarning(
                exception,
                "對話相依元件提前取消或逾時。ConversationId={ConversationId}, RunId={RunId}, " +
                "ProviderId={ProviderId}, ModelId={ModelId}, Stage={Stage}, Tool={Tool}, ElapsedMs={ElapsedMs}",
                conversation.Id,
                runId,
                request.Profile.Id,
                request.ModelId,
                failedStage,
                failedTool,
                (long)elapsed.TotalMilliseconds);
            if (runActivityId is not null)
                await activity.FailAsync(runActivityId, "模型或工具回應逾時");
            await writer.WriteAsync(
                new ConversationErrorEvent(
                    "模型或工具未在預期時間內完成，請稍後重試。",
                    ConversationErrorCodes.DependencyTimeout,
                    Retryable: true,
                    Stage: failedStage,
                    TurnId: turnId),
                CancellationToken.None);
        }
        catch (AgentRuntimeException exception)
        {
            await TryFailTurnAsync(
                conversation.Id,
                turnId,
                ConversationTurnStatus.Failed,
                exception.Code);
            logger.LogWarning(
                "Agent Runtime 回報結構化錯誤。ConversationId={ConversationId}, RunId={RunId}, Code={Code}",
                conversation.Id,
                runId,
                exception.Code);
            if (runActivityId is not null)
                await activity.FailAsync(runActivityId, "模型設定無法使用");
            await writer.WriteAsync(
                new ConversationErrorEvent(
                    exception.UserMessage,
                    exception.Code,
                    exception.Retryable,
                    currentStage,
                    turnId),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await TryFailTurnAsync(
                conversation.Id,
                turnId,
                ConversationTurnStatus.Failed,
                ConversationErrorCodes.AgentExecutionFailed);
            var failedStage = currentStage;
            logger.LogError(
                exception,
                "對話 Agent 執行失敗。已耗時={ElapsedMs}ms，ConversationId={ConversationId}, ProviderId={ProviderId}",
                totalStopwatch.ElapsedMilliseconds,
                conversation.Id,
                request.Profile.Id);
            if (runActivityId is not null)
                await activity.FailAsync(runActivityId, "回答失敗");
            await writer.WriteAsync(
                new ConversationErrorEvent(
                    "對話執行失敗，請稍後重試；若持續發生，請查看後端繁體中文 Log。",
                    ConversationErrorCodes.AgentExecutionFailed,
                    Retryable: false,
                    Stage: failedStage,
                    TurnId: turnId),
                CancellationToken.None);
        }
        finally
        {
            if (gateAcquired)
            {
                conversationGate.Release();
                // 不移除 gate。若先 Release 再 TryRemove，另一個請求可能取得舊
                // semaphore，而第三個請求取得新 semaphore，造成同一對話同時執行兩輪。
                // 以對話識別碼為鍵保留同一個 gate，才能保證整個生命週期的互斥。
            }
            writer.TryComplete();
        }
    }

    /// <summary>
    /// 失敗狀態寫回資料庫不能覆蓋原始錯誤；例如資料庫短暫不可用時，仍要
    /// 將結構化錯誤事件送到前端，而不是在 catch 區塊再次拋例外導致 SSE 無終止事件。
    /// </summary>
    private async Task TryFailTurnAsync(
        string conversationId,
        string turnId,
        ConversationTurnStatus status,
        string errorCode)
    {
        try
        {
            await conversations.FailTurnAsync(
                conversationId,
                turnId,
                status,
                errorCode,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "對話回合狀態寫回失敗；保留原始錯誤事件。ConversationId={ConversationId}, TurnId={TurnId}, Status={Status}",
                conversationId,
                turnId,
                status);
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
        CancellationToken cancellationToken)
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
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            // 標題失敗不影響主要回答；對話仍保留第一則訊息截取的標題。
        }
    }

    private static string ShortTitle(string value) =>
        value.Length > 50 ? value[..50] + "…" : value;

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";
}
