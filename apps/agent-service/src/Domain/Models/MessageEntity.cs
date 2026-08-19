namespace AgentService.Domain.Models;

public enum MessageRole { User, Assistant }

/// <summary>
/// 對話中的單一訊息。
/// 對應 SQLite 的 Messages 資料表。
/// </summary>
public sealed class MessageEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string ConversationId { get; set; }

    public MessageRole Role { get; set; }

    public required string Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>所屬的用戶端回合；相同回合重試時用來避免重複插入訊息。</summary>
    public string? TurnId { get; set; }

    /// <summary>導覽屬性。</summary>
    public ConversationEntity? Conversation { get; set; }
}
