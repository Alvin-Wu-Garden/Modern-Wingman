namespace AgentService.Domain.Models;

/// <summary>一個用戶端對話回合的持久化狀態。</summary>
public enum ConversationTurnStatus
{
    /// <summary>已建立 User Message，正在準備或呼叫模型。</summary>
    Running,

    /// <summary>Assistant Message 已完整保存。</summary>
    Completed,

    /// <summary>回合失敗，可依相同 TurnId 重試。</summary>
    Failed,

    /// <summary>回合被使用者取消，可依相同 TurnId 重試。</summary>
    Cancelled,
}

/// <summary>
/// 以用戶端 TurnId 作為冪等鍵，記錄一輪對話的狀態與模型選擇。
/// 這筆資料讓「SSE 斷線後重試」與「模型已完成但前端沒收到 done」都不會重複呼叫模型。
/// </summary>
public sealed class ConversationTurnEntity
{
    /// <summary>用戶端產生的回合識別碼；同一個問題重試必須沿用。</summary>
    public required string Id { get; set; }

    /// <summary>所屬對話識別碼。</summary>
    public required string ConversationId { get; set; }

    /// <summary>這輪 User Message 的資料庫識別碼。</summary>
    public required string UserMessageId { get; set; }

    /// <summary>成功時的 Assistant Message 識別碼。</summary>
    public string? AssistantMessageId { get; set; }

    /// <summary>本輪使用的 Provider Profile。</summary>
    public required string ProviderProfileId { get; set; }

    /// <summary>本輪實際指定的模型。</summary>
    public string? ModelId { get; set; }

    /// <summary>避免同一 TurnId 被不同 User Message 重用。</summary>
    public required string UserMessageHash { get; set; }

    /// <summary>目前回合狀態。</summary>
    public ConversationTurnStatus Status { get; set; } = ConversationTurnStatus.Running;

    /// <summary>目前已嘗試的執行次數。</summary>
    public int AttemptCount { get; set; }

    /// <summary>最後一次錯誤代碼。</summary>
    public string? ErrorCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>導覽屬性。</summary>
    public ConversationEntity? Conversation { get; set; }
}
