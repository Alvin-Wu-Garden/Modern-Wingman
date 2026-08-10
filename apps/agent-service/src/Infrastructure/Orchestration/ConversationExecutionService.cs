using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.AgentRuntime;
using AgentService.Infrastructure.Skills;
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
    ILogger<ConversationExecutionService> logger)
{
    /// <summary>執行單次對話並逐一回傳可安全送到前端的事件。</summary>
    public async IAsyncEnumerable<ConversationStreamEvent> ExecuteAsync(
        ConversationExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<ConversationStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var execution = ExecuteCoreAsync(request, channel.Writer, ct);

        await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return item;

        await execution;
    }

    private async Task ExecuteCoreAsync(
        ConversationExecutionRequest request,
        ChannelWriter<ConversationStreamEvent> writer,
        CancellationToken ct)
    {
        var conversation = request.Conversation;
        var firstExchange = conversation.Messages.Count == 0;
        var runId = Guid.NewGuid().ToString("N");
        var fullResponse = new System.Text.StringBuilder();
        TokenUsage? usage = null;
        var activity = new AgentActivityReporter(
            runId,
            value => writer.WriteAsync(
                new ConversationActivityStreamEvent(value),
                CancellationToken.None).AsTask());
        string? runActivityId = null;

        try
        {
            if (request.EmitRuntimeActivities)
            {
                runActivityId = await activity.StartAsync(
                    "run.started",
                    "正在分析問題",
                    tool: "runtime",
                    detail: "準備 GraphRAG 與專案工具");
            }
            var preparation = request.Prepare is null
                ? new ConversationPreparation(
                    request.Message.UserMessage,
                    AgentRuntimeService.GeneralInstructions,
                    SkillPromptBuilder.BuildSkillsPrompt(skillProvider),
                    Tools: [])
                : await request.Prepare(activity, ct);

            await writer.WriteAsync(
                new ConversationStartedEvent(
                    request.Profile.Id,
                    request.ModelId,
                    runId,
                    preparation.GraphStatus,
                    preparation.GraphWarning),
                CancellationToken.None);

            await conversations.AddMessageAsync(
                conversation.Id,
                MessageRole.User,
                request.Message.UserMessage,
                ct);
            if (conversation.Title == "新對話")
                await conversations.SetTitleAsync(
                    conversation.Id,
                    ShortTitle(request.Message.UserMessage),
                    ct);

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
                    request.EmitRuntimeActivities),
                ct))
            {
                fullResponse.Append(token);
                await writer.WriteAsync(
                    new ConversationTokenEvent(token),
                    CancellationToken.None);
            }

            if (fullResponse.Length > 0)
                await conversations.AddMessageAsync(
                    conversation.Id,
                    MessageRole.Assistant,
                    fullResponse.ToString(),
                    CancellationToken.None);

            if (usage is not null)
                await writer.WriteAsync(
                    new ConversationUsageEvent(usage),
                    CancellationToken.None);

            if (runActivityId is not null)
                await activity.CompleteAsync(runActivityId, "回答已完成");

            // 所有活動的完成事件必須先於 done 送出，否則前端收到 done 後會停止
            // 更新串流訊息，最後的活動完成事件就會被忽略而一直顯示「處理中」。
            await writer.WriteAsync(
                new ConversationCompletedEvent(),
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
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "對話 Agent 執行失敗。ConversationId={ConversationId}, ProviderId={ProviderId}",
                conversation.Id,
                request.Profile.Id);
            if (runActivityId is not null)
                await activity.FailAsync(runActivityId, "回答失敗");
            await writer.WriteAsync(
                new ConversationErrorEvent(exception.Message),
                CancellationToken.None);
        }
        finally
        {
            writer.TryComplete();
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
