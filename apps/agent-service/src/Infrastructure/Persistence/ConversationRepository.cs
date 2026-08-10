using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

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
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var msg = new MessageEntity
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
        };
        db.Messages.Add(msg);

        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow), ct);

        await db.SaveChangesAsync(ct);
        return msg.Id;
    }

    public async Task SetTitleAsync(string conversationId, string title, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, title), ct);
    }
}
