using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// IProviderSettingStore 的 EF Core 實作，以 wingman.db 為後端。
///
/// 執行緒安全：使用 IDbContextFactory 確保每個操作都在獨立的 DbContext scope 執行，
/// 避免 DbContext 非執行緒安全的問題。
/// </summary>
public sealed class ProviderSettingStore : IProviderSettingStore
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AgentServiceOptions _options;
    private readonly ISecretProtector _secretProtector;

    public ProviderSettingStore(
        IDbContextFactory<AppDbContext> dbFactory,
        IOptions<AgentServiceOptions> options,
        ISecretProtector secretProtector)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
        _secretProtector = secretProtector;
    }

    // ── 讀取 ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProviderSettingEntity>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ProviderSettings
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);
    }

    public async Task<ProviderSettingEntity?> GetAsync(string profileId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ProviderSettings.FindAsync([profileId], ct);
    }

    // ── API Key（環境變數優先）───────────────────────────────────────────────

    public bool HasEnvVar(string profileId)
    {
        var envVar = GetEnvVarName(profileId);
        if (envVar is null) return false;
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar));
    }

    public string? GetApiKey(string profileId)
    {
        // 1. 環境變數優先
        var envVar = GetEnvVarName(profileId);
        if (envVar is not null)
        {
            var envVal = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(envVal)) return envVal;
        }

        // 2. DB 儲存值（同步讀取，避免 GetAsync 引入 async 複雜度在 non-async call path）
        using var db = _dbFactory.CreateDbContext();
        var entity = db.ProviderSettings.Find(profileId);
        if (entity?.ProtectedApiKey is null || entity.EncryptionScheme is null)
            return null;

        try
        {
            return _secretProtector.Unprotect(entity.ProtectedApiKey, entity.EncryptionScheme);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // DPAPI 密文無法在目前的使用者/機器環境下解密（例如帳號或設定檔狀態異常）。
            // 清除無效的加密資料，讓服務以「未設定」狀態啟動，避免整個 host 崩潰。
            entity.ProtectedApiKey = null;
            entity.EncryptionScheme = null;
            db.SaveChanges();
            return null;
        }
    }

    public async Task SetValidatedCredentialAsync(
        string profileId,
        string apiKey,
        string? baseUrl,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ProviderSettings.FindAsync([profileId], ct);
        if (entity is null)
        {
            entity = new ProviderSettingEntity
            {
                ProfileId = profileId,
                SortOrder = await NextSortOrderAsync(db, ct),
            };
            db.ProviderSettings.Add(entity);
        }

        var protectedSecret = _secretProtector.Protect(apiKey);
        entity.ProtectedApiKey = protectedSecret.Value;
        entity.EncryptionScheme = protectedSecret.Scheme;
        entity.BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveApiKeyAsync(string profileId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ProviderSettings.FindAsync([profileId], ct);
        if (entity is null) return;
        entity.ProtectedApiKey = null;
        entity.EncryptionScheme = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ── 排序 ──────────────────────────────────────────────────────────────────

    public async Task ReorderAsync(
        IReadOnlyList<(string ProfileId, int SortOrder)> order,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var (profileId, sortOrder) in order)
        {
            var entity = await db.ProviderSettings.FindAsync([profileId], ct);
            if (entity is null)
            {
                entity = new ProviderSettingEntity { ProfileId = profileId };
                db.ProviderSettings.Add(entity);
            }
            entity.SortOrder = sortOrder;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    // ── 私有輔助 ──────────────────────────────────────────────────────────────

    private string? GetEnvVarName(string profileId)
    {
        var config = _options.ModelProviders.FirstOrDefault(p => p.Id == profileId);
        return config?.ApiKeyEnvVar;
    }

    private static async Task<int> NextSortOrderAsync(AppDbContext db, CancellationToken ct)
    {
        var max = await db.ProviderSettings.MaxAsync(x => (int?)x.SortOrder, ct);
        return (max ?? -1) + 1;
    }
}
