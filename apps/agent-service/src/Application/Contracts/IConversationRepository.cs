using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// 對話持久化介面。
/// SQLite 對話持久化介面。
/// </summary>
public interface IConversationRepository
{
    Task<List<ConversationEntity>> ListGeneralAsync(CancellationToken ct = default);
    Task<List<ConversationEntity>> ListProjectAsync(string projectId, CancellationToken ct = default);
    Task<ConversationEntity?> GetGeneralAsync(string id, CancellationToken ct = default);
    Task<ConversationEntity?> GetProjectAsync(string projectId, string id, CancellationToken ct = default);
    Task<ConversationEntity> CreateGeneralAsync(
        string? providerProfileId = null,
        CancellationToken ct = default);
    Task<ConversationEntity> CreateProjectAsync(
        string projectId,
        string? providerProfileId = null,
        CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);

    Task<string> AddMessageAsync(string conversationId, MessageRole role, string content, CancellationToken ct = default);

    /// <summary>設定對話標題（若仍為預設值則自動由第一條 User 訊息截取）。</summary>
    Task SetTitleAsync(string conversationId, string title, CancellationToken ct = default);
}
