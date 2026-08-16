using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Extensions.AI;

namespace AgentService.Application.Models;

/// <summary>對話執行前已準備好的專案上下文。</summary>
public sealed record ConversationPreparation(
    string Prompt,
    string Instructions,
    string SkillsPrompt,
    IReadOnlyList<AIFunction> Tools,
    string? GraphStatus = null,
    string? GraphWarning = null,
    Func<ToolCallUsageSummary>? GetToolCallUsage = null);

/// <summary>
/// 單次對話回合結束後的工具呼叫次數統計，只給診斷 log 使用，不會送到前端或持久化。
/// 一般對話沒有工具可呼叫，<see cref="ConversationPreparation.GetToolCallUsage"/> 維持 null 即可。
/// </summary>
public sealed record ToolCallUsageSummary(
    int TotalCalls,
    IReadOnlyDictionary<string, int> CallsByCategory);


/// <summary>
/// Agent Runtime 執行單次模型回合所需的完整上下文。
/// EmitRuntimeActivities 只控制 Runtime 自動建立的階段活動；真正工具自行回報的活動不受影響。
/// </summary>
public sealed record AgentExecutionRequest(
    string UserMessage,
    List<MessageEntity> History,
    ModelProviderProfile Profile,
    string? ModelOverride,
    IReadOnlyList<AttachmentReference>? Attachments,
    string Instructions,
    string SkillsPrompt,
    IReadOnlyList<AIFunction> Tools,
    Action<TokenUsage>? OnUsage = null,
    AgentActivityReporter? Activity = null,
    bool EmitRuntimeActivities = false);

/// <summary>
/// 一次對話執行所需的模型、訊息與可用工具。
/// 一般對話不發出模型階段活動，專案解析路由才啟用 EmitRuntimeActivities。
/// </summary>
public sealed record ConversationExecutionRequest(
    ConversationEntity Conversation,
    SendMessageRequest Message,
    ModelProviderProfile Profile,
    string? ModelId,
    Func<AgentActivityReporter, CancellationToken, Task<ConversationPreparation>>? Prepare = null,
    bool EmitRuntimeActivities = false);

/// <summary>對話串流事件的基底型別。</summary>
public abstract record ConversationStreamEvent;

/// <summary>告知前端本次執行的 Provider、模型與專案解析狀態。</summary>
public sealed record ConversationStartedEvent(
    string ResolvedProviderId,
    string? ResolvedModelId,
    string RunId,
    string? GraphStatus,
    string? GraphWarning) : ConversationStreamEvent;

/// <summary>Agent 或專案工具的安全進度事件。</summary>
public sealed record ConversationActivityStreamEvent(AgentActivityEvent Activity) : ConversationStreamEvent;

/// <summary>模型輸出的單一文字片段。</summary>
public sealed record ConversationTokenEvent(string Token) : ConversationStreamEvent;

/// <summary>模型輸出的 Token 使用量。</summary>
public sealed record ConversationUsageEvent(TokenUsage Usage) : ConversationStreamEvent;

/// <summary>對話串流正常完成。</summary>
public sealed record ConversationCompletedEvent : ConversationStreamEvent;

/// <summary>對話串流發生可回報給使用者的錯誤。</summary>
public sealed record ConversationErrorEvent(string Error) : ConversationStreamEvent;
