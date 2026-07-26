namespace AgentService.Domain.Models;

/// <summary>對話所屬範圍；一般對話與專案對話共用相同資料模型及 UI。</summary>
public enum ConversationScope
{
    General,
    Project,
}

/// <summary>
/// 一個持久化的對話（Conversation）。
/// 對應 SQLite 的 Conversations 資料表。
/// </summary>
public sealed class ConversationEntity
{
    /// <summary>唯一 ID（GUID 字串，無連字號）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>對話標題（自動取自第一則使用者訊息的前 50 個字元）。</summary>
    public string Title { get; set; } = "新對話";

    /// <summary>使用的 Provider Profile ID；null = 使用 ActiveProfileId。</summary>
    public string? ProviderProfileId { get; set; }

    /// <summary>一般聊天或專案解析；決定訊息要交給通用 Agent 或 GraphRAG。</summary>
    public ConversationScope Scope { get; set; } = ConversationScope.General;

    /// <summary>專案對話所屬的專案 ID；一般對話固定為 null。</summary>
    public string? ProjectId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>導覽屬性。</summary>
    public List<MessageEntity> Messages { get; set; } = [];
}
