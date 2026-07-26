using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// 版本控制連線設定的資料庫記錄。
/// 此記錄只保存非機密欄位；帳號與加密後密碼由一對一的 Credential 記錄隔離保存。
/// </summary>
public sealed class VcsConnectionProfileRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string VcsType { get; set; } = "Git";
    public string ServerType { get; set; } = "BitbucketServer";
    public string BaseUrl { get; set; } = "";
    public bool SslVerificationEnabled { get; set; } = true;
    public string? DefaultWorkspaceRoot { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public VcsCredentialRecord? Credential { get; set; }
}

/// <summary>
/// 版本控制認證的加密資料庫記錄。
/// SecretValue 永遠是 DPAPI 密文；repository 以外的 EF 查詢不得把它當成明文使用。
/// </summary>
public sealed class VcsCredentialRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConnectionProfileId { get; set; } = "";
    public string Username { get; set; } = "";
    public string SecretType { get; set; } = "AccessToken";
    public string SecretValue { get; set; } = "";
    public string EncryptionScheme { get; set; } = "dpapi-current-user-v1";
    public DateTimeOffset UpdatedAt { get; set; }
    public VcsConnectionProfileRecord? Profile { get; set; }
}

/// <summary>
/// 管理 Git／SVN clone、checkout 與 update 所需的連線設定。
/// 密碼僅在寫入時加密、實際執行版本控制命令前解密；REST response 會再移除 SecretValue。
/// </summary>
public sealed class VcsProfileRepository(
    IDbContextFactory<AppDbContext> factory,
    ISecretProtector secretProtector) : IVcsProfileRepository
{
    /// <summary>列出所有設定，供服務端執行 clone／update 時使用。</summary>
    public async Task<IReadOnlyList<VcsConnectionProfile>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.VcsConnectionProfiles
            .AsNoTracking()
            .Include(x => x.Credential)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    /// <summary>依識別碼取得單一設定；找不到時回傳 null。</summary>
    public async Task<VcsConnectionProfile?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.VcsConnectionProfiles
            .AsNoTracking()
            .Include(x => x.Credential)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : Map(row);
    }

    /// <summary>
    /// 新增或更新設定。更新時若未傳入 SecretValue，會保留既有密文，
    /// 避免使用者只修改名稱就意外清除認證。
    /// </summary>
    public async Task SaveAsync(VcsConnectionProfile profile, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.VcsConnectionProfiles
            .Include(x => x.Credential)
            .FirstOrDefaultAsync(x => x.Id == profile.Id, ct);

        if (row is null)
        {
            row = new VcsConnectionProfileRecord
            {
                Id = profile.Id,
                CreatedAt = profile.CreatedAt,
            };
            db.VcsConnectionProfiles.Add(row);
        }

        row.Name = profile.Name;
        row.VcsType = profile.VcsType.ToString();
        row.ServerType = profile.ServerType.ToString();
        row.BaseUrl = profile.BaseUrl;
        row.SslVerificationEnabled = profile.SslVerificationEnabled;
        row.DefaultWorkspaceRoot = profile.DefaultWorkspaceRoot;
        row.Enabled = profile.Enabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        if (profile.SecretValue is not null)
        {
            var protectedSecret = secretProtector.Protect(profile.SecretValue);
            row.Credential ??= new VcsCredentialRecord { ConnectionProfileId = profile.Id };
            row.Credential.Username = profile.Username ?? "";
            row.Credential.SecretType = (profile.SecretType ?? VcsSecretType.Password).ToString();
            row.Credential.SecretValue = protectedSecret.Value;
            row.Credential.EncryptionScheme = protectedSecret.Scheme;
            row.Credential.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>刪除設定；不存在時視為已完成，不回報錯誤。</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.VcsConnectionProfiles.FindAsync([id], ct);
        if (row is null) return;

        db.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>將資料庫記錄轉成服務端 domain model，並只在記憶體中解密認證。</summary>
    private VcsConnectionProfile Map(VcsConnectionProfileRecord row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        VcsType = Enum.Parse<VcsType>(row.VcsType),
        ServerType = Enum.Parse<VcsServerType>(row.ServerType),
        BaseUrl = row.BaseUrl,
        SslVerificationEnabled = row.SslVerificationEnabled,
        DefaultWorkspaceRoot = row.DefaultWorkspaceRoot,
        Enabled = row.Enabled,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        Username = row.Credential?.Username,
        SecretType = row.Credential is null
            ? null
            : Enum.Parse<VcsSecretType>(row.Credential.SecretType),
        SecretValue = row.Credential is null
            ? null
            : secretProtector.Unprotect(row.Credential.SecretValue, row.Credential.EncryptionScheme),
    };
}
