using AgentService.Application.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

/// <summary>EF Core 專用的專案資料庫設定資料列。</summary>
public sealed class ProjectDatabaseConfigurationRecord
{
    public string ProjectId { get; set; } = "";
    public string Provider { get; set; } = nameof(ProjectDatabaseProvider.SqlServer);
    public string? Server { get; set; }
    public int? Port { get; set; }
    public string? DatabaseName { get; set; }
    public string? Authentication { get; set; }
    public string? Username { get; set; }
    public string? ProtectedPassword { get; set; }
    public string? EncryptionScheme { get; set; }
    public bool TrustServerCertificate { get; set; } = true;
    public string? SqlitePath { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 專案資料庫設定儲存庫。SQL 密碼使用目前 Windows 使用者的 DPAPI 加密；
/// SQLite 只保存檔案路徑，索引時一律以唯讀模式開啟。
/// </summary>
public sealed class ProjectDatabaseConfigurationStore(
    IDbContextFactory<AppDbContext> factory,
    ISecretProtector secretProtector) : IProjectDatabaseConfigurationStore
{
    /// <summary>
    /// 讀取專案設定。預設不解密密碼，避免設定頁與一般呼叫端意外取得機密。
    /// </summary>
    public async Task<ProjectDatabaseConfiguration?> GetAsync(
        string projectId,
        bool includePassword = false,
        CancellationToken ct = default)
    {
        var configurations = await GetAllAsync(projectId, includePassword, ct);
        // 舊版 API 仍只回傳一筆；優先選 SQL Server，確保既有 UI 行為不變。
        return configurations.FirstOrDefault(item => item.Provider == ProjectDatabaseProvider.SqlServer)
            ?? configurations.FirstOrDefault();
    }

    /// <summary>讀取指定 Provider，避免多來源設定互相誤用。</summary>
    public async Task<ProjectDatabaseConfiguration?> GetAsync(
        string projectId,
        ProjectDatabaseProvider provider,
        bool includePassword = false,
        CancellationToken ct = default)
    {
        var configurations = await GetAllAsync(projectId, includePassword, ct);
        return configurations.FirstOrDefault(item => item.Provider == provider);
    }

    /// <summary>讀取專案全部 Provider 設定，並依 Provider 穩定排序。</summary>
    public async Task<IReadOnlyList<ProjectDatabaseConfiguration>> GetAllAsync(
        string projectId,
        bool includePassword = false,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ProjectDatabaseConfigurations
            .AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.Provider)
            .ToListAsync(ct);
        return rows.Select(row => ToDomain(row, includePassword)).ToArray();
    }

    /// <summary>將 EF 資料列轉成不暴露密文的領域設定。</summary>
    private ProjectDatabaseConfiguration ToDomain(
        ProjectDatabaseConfigurationRecord row,
        bool includePassword)
    {
        var hasPassword = !string.IsNullOrWhiteSpace(row.ProtectedPassword);
        var password = includePassword && hasPassword
            ? secretProtector.Unprotect(row.ProtectedPassword!, row.EncryptionScheme!)
            : null;
        return new ProjectDatabaseConfiguration(
            row.ProjectId,
            Enum.Parse<ProjectDatabaseProvider>(row.Provider),
            row.Server,
            row.Port,
            row.DatabaseName,
            Enum.TryParse<SqlServerAuthentication>(row.Authentication, out var authentication)
                ? authentication
                : null,
            row.Username,
            password,
            hasPassword,
            row.TrustServerCertificate,
            row.SqlitePath,
            row.UpdatedAt);
    }

    /// <summary>
    /// 保存設定。空密碼代表保留既有 SQL Auth 密碼；
    /// 非 SQL Auth 設定則主動移除已保存的密文。
    /// </summary>
    public async Task SaveAsync(
        ProjectDatabaseConfiguration configuration,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var providerName = configuration.Provider.ToString();
        var row = await db.ProjectDatabaseConfigurations
            .FirstOrDefaultAsync(item => item.ProjectId == configuration.ProjectId &&
                                         item.Provider == providerName, ct);
        if (row is null)
        {
            row = new ProjectDatabaseConfigurationRecord
            {
                ProjectId = configuration.ProjectId,
                Provider = providerName,
            };
            db.ProjectDatabaseConfigurations.Add(row);
        }

        row.Provider = providerName;
        row.Server = configuration.Server;
        row.Port = configuration.Port;
        row.DatabaseName = configuration.DatabaseName;
        row.Authentication = configuration.Authentication?.ToString();
        row.Username = configuration.Username;
        row.TrustServerCertificate = configuration.TrustServerCertificate;
        row.SqlitePath = configuration.SqlitePath;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        if (configuration.Provider != ProjectDatabaseProvider.SqlServer ||
            configuration.Authentication != SqlServerAuthentication.SqlPassword)
        {
            row.ProtectedPassword = null;
            row.EncryptionScheme = null;
        }
        else if (configuration.Password is not null)
        {
            var protectedSecret = secretProtector.Protect(configuration.Password);
            row.ProtectedPassword = protectedSecret.Value;
            row.EncryptionScheme = protectedSecret.Scheme;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>刪除指定 Provider 或專案全部設定；不存在時視為成功。</summary>
    public async Task DeleteAsync(
        string projectId,
        ProjectDatabaseProvider? provider = null,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.ProjectDatabaseConfigurations.Where(item => item.ProjectId == projectId);
        if (provider is not null)
        {
            var providerName = provider.Value.ToString();
            query = query.Where(item => item.Provider == providerName);
        }
        await query.ExecuteDeleteAsync(ct);
    }
}
