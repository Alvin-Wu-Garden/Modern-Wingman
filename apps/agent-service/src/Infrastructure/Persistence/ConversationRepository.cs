using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// IConversationRepository 實作。
/// 使用 IDbContextFactory 建立 DbContext，與 Singleton IProviderSettingStore 共用同一個 factory，
/// 避免 Scoped DbContext 與 Singleton factory 的 DI 生命週期衝突。
/// </summary>
public sealed class ConversationRepository(IDbContextFactory<AppDbContext> dbFactory) : IConversationRepository
{
    public async Task<List<ConversationEntity>> ListGeneralAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Conversations
            .Where(c => c.ProjectId == null)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<ConversationEntity>> ListProjectAsync(
        string projectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Conversations
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<ConversationEntity?> GetGeneralAsync(
        string id,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == id && c.ProjectId == null, ct);
    }

    public async Task<ConversationEntity?> GetProjectAsync(
        string projectId,
        string id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == id && c.ProjectId == projectId, ct);
    }

    public async Task<ConversationEntity> CreateGeneralAsync(
        string? providerProfileId = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conv = new ConversationEntity
        {
            ProviderProfileId = providerProfileId,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync(ct);
        return conv;
    }

    public async Task<ConversationEntity> CreateProjectAsync(
        string projectId,
        string? providerProfileId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conv = new ConversationEntity
        {
            ProviderProfileId = providerProfileId,
            ProjectId = projectId,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync(ct);
        return conv;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<string> AddMessageAsync(
        string conversationId,
        MessageRole role,
        string content,
        CancellationToken ct = default) =>
        await AddMessageAsync(conversationId, role, content, turnId: null, ct);

    public async Task<string> AddMessageAsync(
        string conversationId,
        MessageRole role,
        string content,
        string? turnId = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            var existing = await db.Messages
                .Where(m => m.ConversationId == conversationId &&
                            m.TurnId == turnId && m.Role == role)
                .Select(m => m.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
                return existing;
        }
        var msg = new MessageEntity
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            TurnId = turnId,
        };
        db.Messages.Add(msg);

        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow), ct);

        await db.SaveChangesAsync(ct);
        return msg.Id;
    }

    public async Task<ConversationTurnEntity?> GetTurnAsync(
        string conversationId,
        string turnId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ConversationTurns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.Id == turnId, ct);
    }

    public async Task<ConversationTurnEntity> BeginTurnAsync(
        string conversationId,
        string turnId,
        string userMessage,
        string providerProfileId,
        string? modelId,
        CancellationToken ct = default)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userMessage)));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.ConversationTurns
            .FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.Id == turnId, ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.UserMessageHash, hash, StringComparison.Ordinal) ||
                !string.Equals(existing.ProviderProfileId, providerProfileId, StringComparison.Ordinal) ||
                !string.Equals(existing.ModelId, modelId, StringComparison.Ordinal))
                throw new InvalidOperationException("相同 TurnId 不可更換訊息內容、Provider 或模型。");

            if (existing.Status is ConversationTurnStatus.Failed or ConversationTurnStatus.Cancelled)
            {
                existing.Status = ConversationTurnStatus.Running;
                existing.ErrorCode = null;
                existing.AttemptCount++;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return existing;
        }

        var message = new MessageEntity
        {
            ConversationId = conversationId,
            Role = MessageRole.User,
            Content = userMessage,
            TurnId = turnId,
        };
        var turn = new ConversationTurnEntity
        {
            Id = turnId,
            ConversationId = conversationId,
            UserMessageId = message.Id,
            ProviderProfileId = providerProfileId,
            ModelId = modelId,
            UserMessageHash = hash,
            AttemptCount = 1,
        };
        db.Messages.Add(message);
        db.ConversationTurns.Add(turn);
        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow), ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // 多個相同請求同時抵達時，唯一鍵只允許一個建立；讀回勝出的資料。
            await using var retryDb = await dbFactory.CreateDbContextAsync(ct);
            var winner = await retryDb.ConversationTurns
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.Id == turnId, ct);
            if (winner is null)
                throw;
            return winner;
        }
        return turn;
    }

    public async Task<MessageEntity?> GetMessageAsync(
        string messageId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == messageId, ct);
    }

    public async Task CompleteTurnAsync(
        string conversationId,
        string turnId,
        string assistantMessageId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ConversationTurns
            .Where(x => x.ConversationId == conversationId && x.Id == turnId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AssistantMessageId, assistantMessageId)
                .SetProperty(x => x.Status, ConversationTurnStatus.Completed)
                .SetProperty(x => x.ErrorCode, (string?)null)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task FailTurnAsync(
        string conversationId,
        string turnId,
        ConversationTurnStatus status,
        string errorCode,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ConversationTurns
            .Where(x => x.ConversationId == conversationId && x.Id == turnId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.ErrorCode, errorCode)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task SetTitleAsync(string conversationId, string title, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, title), ct);
    }
}
