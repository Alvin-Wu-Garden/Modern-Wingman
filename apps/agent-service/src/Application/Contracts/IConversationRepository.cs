using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// 對話持久化介面。
/// Phase 1：SQLite 實作。Phase 2+：可替換為其他儲存後端。
/// </summary>
public interface IConversationRepository
{
    Task<List<ConversationEntity>> ListAsync(CancellationToken ct = default);
    Task<ConversationEntity?> GetAsync(string id, CancellationToken ct = default);
    Task<ConversationEntity> CreateAsync(string? providerProfileId = null, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);

    Task<string> AddMessageAsync(string conversationId, MessageRole role, string content, CancellationToken ct = default);

    /// <summary>設定對話標題（若仍為預設值則自動由第一條 User 訊息截取）。</summary>
    Task SetTitleAsync(string conversationId, string title, CancellationToken ct = default);
}
