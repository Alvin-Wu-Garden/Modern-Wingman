using Microsoft.Data.Sqlite;

namespace AgentService.Infrastructure.Persistence;

public static class DatabasePathResolver
{
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
            // Let UseSqlite surface invalid connection strings in the normal startup path.
        }
    }
}
