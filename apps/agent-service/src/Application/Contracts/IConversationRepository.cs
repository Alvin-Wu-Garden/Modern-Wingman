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

    Task<string> AddMessageAsync(
        string conversationId,
        MessageRole role,
        string content,
        CancellationToken ct = default);

    Task<string> AddMessageAsync(
        string conversationId,
        MessageRole role,
        string content,
        string? turnId,
        CancellationToken ct = default);

    /// <summary>取得指定用戶端回合；不存在時回傳 null。</summary>
    Task<ConversationTurnEntity?> GetTurnAsync(
        string conversationId,
        string turnId,
        CancellationToken ct = default);

    /// <summary>
    /// 建立或恢復冪等回合。相同 TurnId 的 User Message 只會建立一次，
    /// Completed 回合則由執行層直接重播既有 Assistant Message。
    /// </summary>
    Task<ConversationTurnEntity> BeginTurnAsync(
        string conversationId,
        string turnId,
        string userMessage,
        string providerProfileId,
        string? modelId,
        CancellationToken ct = default);

    /// <summary>取得已完成回合的 Assistant Message 內容。</summary>
    Task<MessageEntity?> GetMessageAsync(string messageId, CancellationToken ct = default);

    /// <summary>成功保存 Assistant 後標記回合完成。</summary>
    Task CompleteTurnAsync(
        string conversationId,
        string turnId,
        string assistantMessageId,
        CancellationToken ct = default);

    /// <summary>標記回合失敗或取消，保留 User Message 供相同 TurnId 重試。</summary>
    Task FailTurnAsync(
        string conversationId,
        string turnId,
        ConversationTurnStatus status,
        string errorCode,
        CancellationToken ct = default);

    /// <summary>設定對話標題（若仍為預設值則自動由第一條 User 訊息截取）。</summary>
    Task SetTitleAsync(string conversationId, string title, CancellationToken ct = default);
}
