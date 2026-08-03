using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// Atlassian 連線設定的資料庫記錄。
/// ProtectedSecret 永遠是 DPAPI 密文；repository 以外的 EF 查詢不得把它當成明文使用。
/// </summary>
public sealed class AtlassianConnectionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>"Jira" 或 "Wiki"，對應 AtlassianServiceType 列舉字串。</summary>
    public string ServiceType { get; set; } = "Jira";

    public string BaseUrl { get; set; } = "";

    /// <summary>"Bearer" 或 "Basic"，對應 AtlassianAuthType 列舉字串。</summary>
    public string AuthType { get; set; } = "Bearer";

    public string? Username { get; set; }

    /// <summary>DPAPI 加密後的 PAT；null 代表尚未設定。絕不存放明文。</summary>
    public string? ProtectedSecret { get; set; }

    /// <summary>DPAPI 加密方案識別字，對應 ISecretProtector 的 scheme。</summary>
    public string? EncryptionScheme { get; set; }

    public string? ApiVersion { get; set; }
    public bool IsVerified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? VerifiedDisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 管理 JIRA 與 Wiki 連線設定的持久化邏輯。
/// PAT 僅在驗證流程中傳入，其餘讀取一律返回 HasSecret = true/false，不返回明文。
/// </summary>
public sealed class AtlassianConnectionRepository(
    IDbContextFactory<AppDbContext> factory,
    ISecretProtector secretProtector) : IAtlassianConnectionRepository
{
    public async Task<AtlassianConnection?> GetAsync(
        AtlassianServiceType serviceType,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AtlassianConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ServiceType == serviceType.ToString(), ct);
        return row is null ? null : MapToModel(row);
    }

    /// <summary>讀取並解密伺服器即將發送請求所需的連線設定。</summary>
    public async Task<AtlassianConnection?> GetForUseAsync(
        AtlassianServiceType serviceType,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AtlassianConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ServiceType == serviceType.ToString(), ct);
        return row is null ? null : MapToModel(row, includeSecret: true);
    }

    /// <summary>
    /// 新增或更新連線設定。
    /// 若 <paramref name="connection"/>.SecretValue 為 null，保留 DB 中既有的 DPAPI 加密 PAT，
    /// 不清除或覆寫。
    /// </summary>
    public async Task SaveAsync(AtlassianConnection connection, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AtlassianConnections
            .FirstOrDefaultAsync(x => x.ServiceType == connection.ServiceType.ToString(), ct);

        if (row is null)
        {
            row = new AtlassianConnectionRecord
            {
                Id = connection.Id,
                ServiceType = connection.ServiceType.ToString(),
                CreatedAt = connection.CreatedAt,
            };
            db.AtlassianConnections.Add(row);
        }

        row.BaseUrl = connection.BaseUrl;
        row.AuthType = connection.AuthType.ToString();
        row.Username = connection.Username;
        row.ApiVersion = connection.ApiVersion;
        row.IsVerified = connection.IsVerified;
        row.VerifiedAt = connection.VerifiedAt;
        row.VerifiedDisplayName = connection.VerifiedDisplayName;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        // 只有傳入新 SecretValue 時才更新加密金鑰；留白表示保留既有值
        if (!string.IsNullOrWhiteSpace(connection.SecretValue))
        {
            var protected_ = secretProtector.Protect(connection.SecretValue);
            row.ProtectedSecret = protected_.Value;
            row.EncryptionScheme = protected_.Scheme;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(AtlassianServiceType serviceType, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AtlassianConnections
            .FirstOrDefaultAsync(x => x.ServiceType == serviceType.ToString(), ct);
        if (row is not null)
        {
            db.AtlassianConnections.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    private AtlassianConnection MapToModel(
        AtlassianConnectionRecord row,
        bool includeSecret = false) => new()
    {
        Id = row.Id,
        ServiceType = Enum.Parse<AtlassianServiceType>(row.ServiceType),
        BaseUrl = row.BaseUrl,
        AuthType = Enum.Parse<AtlassianAuthType>(row.AuthType),
        Username = row.Username,
        SecretValue = includeSecret && row.ProtectedSecret is not null && row.EncryptionScheme is not null
            ? TryUnprotect(row.ProtectedSecret, row.EncryptionScheme) : null,
        HasSecret = !string.IsNullOrEmpty(row.ProtectedSecret),
        ApiVersion = row.ApiVersion,
        IsVerified = row.IsVerified,
        VerifiedAt = row.VerifiedAt,
        VerifiedDisplayName = row.VerifiedDisplayName,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    /// <summary>解密失敗時回傳 null，不拋例外（例如跨使用者讀取 DPAPI 密文）。</summary>
    private string? TryUnprotect(string value, string scheme)
    {
        try { return secretProtector.Unprotect(value, scheme); }
        catch { return null; }
    }
}
