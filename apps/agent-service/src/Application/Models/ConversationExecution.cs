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
    /// <summary>本輪探測到的不可變 Graph 版本識別碼；僅用於證據標記與記錄。</summary>
    string? GraphVersion = null,
    /// <summary>本輪允許模型執行的工具次數；null 使用 Runtime 預設值。</summary>
    int? MaxToolCalls = null,
    /// <summary>本輪結束後提供診斷日誌使用的工具呼叫次數，不送到前端或持久化。</summary>
    Func<ToolCallUsageSummary>? GetToolCallUsage = null);

/// <summary>
/// 單次對話回合結束後的工具呼叫次數統計，只給診斷 log 使用。
/// 一般對話沒有工具可呼叫時維持 null。
/// </summary>
public sealed record ToolCallUsageSummary(
    int TotalCalls,
    IReadOnlyDictionary<string, int> CallsByCategory);

/// <summary>
/// 專案證據的編排模式。工具只作精準補查時，不會先把整份 Graph 證據預取到 Prompt，
/// 可避免模型看過同一份證據後又重複呼叫 Graph 工具。
/// </summary>
public enum ProjectEvidenceMode
{
    /// <summary>由模型依需要呼叫綁定專案的唯讀工具。</summary>
    ToolOnly,

    /// <summary>先建立 Graph context，再提供少量唯讀工具補查。</summary>
    PreFetchedContext,
}

/// <summary>專案證據編排的最小設定。</summary>
public sealed record ProjectEvidenceOptions(
    ProjectEvidenceMode Mode = ProjectEvidenceMode.ToolOnly,
    int MaxToolCalls = 4);

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
    bool EmitRuntimeActivities = false,
    /// <summary>本次模型回合的硬性工具呼叫上限。</summary>
    int MaxToolCalls = 8);

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
    bool EmitRuntimeActivities = false,
    /// <summary>整個對話回合的逾時；未指定時由執行服務使用兩分鐘預設值。</summary>
    TimeSpan? ExecutionTimeout = null);

/// <summary>對話串流事件的基底型別。</summary>
public abstract record ConversationStreamEvent;

/// <summary>告知前端本次執行的 Provider、模型與專案解析狀態。</summary>
public sealed record ConversationStartedEvent(
    string ResolvedProviderId,
    string? ResolvedModelId,
    string RunId,
    string? GraphStatus,
    string? GraphWarning,
    /// <summary>本輪專案證據使用的 immutable Graph snapshot 版本。</summary>
    string? GraphVersion = null) : ConversationStreamEvent;

/// <summary>Agent 或專案工具的安全進度事件。</summary>
public sealed record ConversationActivityStreamEvent(AgentActivityEvent Activity) : ConversationStreamEvent;

/// <summary>模型輸出的單一文字片段。</summary>
public sealed record ConversationTokenEvent(string Token) : ConversationStreamEvent;

/// <summary>模型輸出的 Token 使用量。</summary>
public sealed record ConversationUsageEvent(TokenUsage Usage) : ConversationStreamEvent;

/// <summary>對話串流正常完成。</summary>
public sealed record ConversationCompletedEvent : ConversationStreamEvent;

/// <summary>對話串流發生可回報給使用者的錯誤。</summary>
public sealed record ConversationErrorEvent(
    string Error,
    /// <summary>供前端以程式區分取消、逾時與一般執行錯誤。</summary>
    string? Code = null) : ConversationStreamEvent;
