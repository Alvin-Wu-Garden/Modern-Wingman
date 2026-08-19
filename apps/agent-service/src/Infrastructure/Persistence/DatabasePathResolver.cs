using Microsoft.Data.Sqlite;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// 集中決定 Modern Wingman 自身 SQLite 資料庫位置，並確保父目錄存在。
/// 這裡管理的是系統設定與 manifest，不是待解析專案的 SQLite。
/// </summary>
public static class DatabasePathResolver
{
    /// <summary>取得 EF Core 使用的 SQLite 連線字串。</summary>
    /// <param name="configuration">可選的 <c>ConnectionStrings:WingmanDb</c> 設定來源。</param>
    /// <param name="environment">用來區分開發與正式資料庫位置的執行環境。</param>
    /// <returns>已確保父目錄存在的 SQLite 連線字串。</returns>
    public static string ResolveConnectionString(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration.GetConnectionString("WingmanDb");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            EnsureConfiguredDirectory(configured);
            return configured;
        }

        var dbPath = environment.IsDevelopment()
            ? GetDevelopmentDatabasePath(environment.ContentRootPath)
            : GetProductionDatabasePath();

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    /// <summary>取得開發環境的系統 SQLite 檔案路徑。</summary>
    /// <param name="contentRootPath">應用程式 content root，可為 repository、apps 或 agent-service 目錄。</param>
    /// <returns>正規化後的 <c>apps/wingman_dev.db</c> 路徑。</returns>
    public static string GetDevelopmentDatabasePath(string contentRootPath)
    {
        var root = Path.GetFullPath(contentRootPath);
        var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (rootName.Equals("agent-service", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.Combine(root, "..", "wingman_dev.db"));

        if (rootName.Equals("apps", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(root, "wingman_dev.db");

        // Content root 為 repository root 時，開發資料庫固定放在 apps/。
        // 不以 Directory.Exists 判斷，讓首次啟動與測試環境套用相同規則。
        return Path.GetFullPath(Path.Combine(root, "apps", "wingman_dev.db"));
    }

    /// <summary>取得正式環境的系統 SQLite 檔案路徑。</summary>
    /// <returns>使用者家目錄下 <c>.Wingman/sqlite/wingman.db</c> 的絕對路徑。</returns>
    public static string GetProductionDatabasePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME")
                ?? AppContext.BaseDirectory;

        return Path.Combine(userProfile, ".Wingman", "sqlite", "wingman.db");
    }

    private static void EnsureConfiguredDirectory(string connectionString)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource) ||
                builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }
        catch
        {
            // 無效連線字串交由 UseSqlite 在正常啟動流程回報。
        }
    }
}
