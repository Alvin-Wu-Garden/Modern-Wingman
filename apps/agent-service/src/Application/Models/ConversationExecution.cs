using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Extensions.AI;

namespace AgentService.Application.Models;

/// <summary>對話執行前已準備好的專案上下文。</summary>
/// <param name="Prompt">送給模型的本輪使用者提示詞。</param>
/// <param name="Instructions">Agent 系統指示。</param>
/// <param name="SkillsPrompt">本輪載入的 Skill 指示。</param>
/// <param name="Tools">本輪可使用的工具。</param>
/// <param name="GraphStatus">專案圖譜可用狀態。</param>
/// <param name="GraphWarning">可回報前端的圖譜降級原因。</param>
/// <param name="GraphVersion">本輪固定使用的不可變 Graph 版本。</param>
/// <param name="MaxToolCalls">本輪允許的最大工具呼叫次數。</param>
/// <param name="GetToolCallUsage">本輪完成後取得工具呼叫診斷摘要的委派。</param>
public sealed record ConversationPreparation(
    string Prompt,
    string Instructions,
    string SkillsPrompt,
    IReadOnlyList<AIFunction> Tools,
    string? GraphStatus = null,
    string? GraphWarning = null,
    string? GraphVersion = null,
    int? MaxToolCalls = null,
    Func<ToolCallUsageSummary>? GetToolCallUsage = null);

/// <summary>
/// 單次對話回合結束後的工具呼叫次數統計，只給診斷 log 使用。
/// 一般對話沒有工具可呼叫時維持 null。
/// </summary>
public sealed record ToolCallUsageSummary(
    int TotalCalls,
    IReadOnlyDictionary<string, int> CallsByCategory);

/// <summary>
/// Agent Runtime 執行單次模型回合所需的完整上下文。
/// EmitRuntimeActivities 只控制 Runtime 自動建立的階段活動；真正工具自行回報的活動不受影響。
/// </summary>
/// <param name="UserMessage">本輪使用者訊息。</param>
/// <param name="History">送給模型的對話歷史。</param>
/// <param name="Profile">已解析的供應商設定。</param>
/// <param name="ModelOverride">本輪指定的模型；未提供時使用供應商預設值。</param>
/// <param name="Attachments">本輪附件。</param>
/// <param name="Instructions">Agent 系統指示。</param>
/// <param name="SkillsPrompt">本輪 Skill 指示。</param>
/// <param name="Tools">本輪可用工具。</param>
/// <param name="OnUsage">接收模型 Token 使用量的回呼。</param>
/// <param name="Activity">安全進度事件回報器。</param>
/// <param name="EmitRuntimeActivities">是否發出 Runtime 自動階段事件。</param>
/// <param name="MaxToolCalls">本輪模型可執行的硬性工具呼叫上限。</param>
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
    int MaxToolCalls = 8);

/// <summary>
/// 一次對話執行所需的模型、訊息與可用工具。
/// 一般對話不發出模型階段活動，專案解析路由才啟用 EmitRuntimeActivities。
/// </summary>
/// <param name="Conversation">目前對話。</param>
/// <param name="Message">本輪輸入訊息。</param>
/// <param name="Profile">已解析的供應商設定。</param>
/// <param name="ModelId">本輪模型識別碼。</param>
/// <param name="Prepare">需要時才執行的專案證據準備委派。</param>
/// <param name="EmitRuntimeActivities">是否發出 Runtime 自動階段事件。</param>
/// <param name="ExecutionTimeout">測試或內部流程使用的整輪逾時覆寫。</param>
public sealed record ConversationExecutionRequest(
    ConversationEntity Conversation,
    SendMessageRequest Message,
    ModelProviderProfile Profile,
    string? ModelId,
    Func<AgentActivityReporter, CancellationToken, Task<ConversationPreparation>>? Prepare = null,
    bool EmitRuntimeActivities = false,
    TimeSpan? ExecutionTimeout = null);

/// <summary>對話串流可由前端穩定判斷的錯誤代碼。</summary>
public static class ConversationErrorCodes
{
    /// <summary>Modern Wingman 設定的整輪執行期限已到。</summary>
    public const string TurnTimeout = "turn_timeout";

    /// <summary>模型、SDK 或工具在整輪期限前自行取消或逾時。</summary>
    public const string DependencyTimeout = "dependency_timeout";

    /// <summary>Agent 執行期間發生非逾時錯誤。</summary>
    public const string AgentExecutionFailed = "agent_execution_failed";

    /// <summary>同一個對話已有另一輪執行中的請求。</summary>
    public const string ConversationBusy = "conversation_busy";
}

/// <summary>對話串流事件的基底型別。</summary>
public abstract record ConversationStreamEvent;

/// <summary>告知前端本次執行的 Provider、模型與專案解析狀態。</summary>
/// <param name="ResolvedProviderId">實際使用的供應商設定識別碼。</param>
/// <param name="ResolvedModelId">實際使用的模型識別碼。</param>
/// <param name="RunId">本輪執行識別碼。</param>
/// <param name="GraphStatus">專案圖譜可用狀態。</param>
/// <param name="GraphWarning">專案圖譜降級原因。</param>
/// <param name="GraphVersion">本輪專案證據使用的不可變 Graph snapshot 版本。</param>
/// <param name="TurnId">用戶端回合冪等鍵。</param>
public sealed record ConversationStartedEvent(
    string ResolvedProviderId,
    string? ResolvedModelId,
    string RunId,
    string? GraphStatus,
    string? GraphWarning,
    string? GraphVersion = null,
    string? TurnId = null) : ConversationStreamEvent;

/// <summary>Agent 或專案工具的安全進度事件。</summary>
public sealed record ConversationActivityStreamEvent(AgentActivityEvent Activity, string? TurnId = null) : ConversationStreamEvent;

/// <summary>模型輸出的單一文字片段。</summary>
public sealed record ConversationTokenEvent(string Token, string? TurnId = null) : ConversationStreamEvent;

/// <summary>模型輸出的 Token 使用量。</summary>
public sealed record ConversationUsageEvent(TokenUsage Usage, string? TurnId = null) : ConversationStreamEvent;

/// <summary>對話串流正常完成。</summary>
public sealed record ConversationCompletedEvent(string? TurnId = null) : ConversationStreamEvent;

/// <summary>對話串流發生可回報給使用者的錯誤。</summary>
/// <param name="Error">可顯示給使用者的繁體中文錯誤訊息。</param>
/// <param name="Code">供前端區分取消、逾時與一般執行錯誤的穩定代碼。</param>
/// <param name="Retryable">相同輸入是否適合直接重試。</param>
/// <param name="Stage">最後觀察到的安全執行階段，不包含 Prompt 或工具參數。</param>
/// <param name="TurnId">用戶端回合冪等鍵。</param>
public sealed record ConversationErrorEvent(
    string Error,
    string? Code = null,
    bool Retryable = false,
    string? Stage = null,
    string? TurnId = null) : ConversationStreamEvent;
