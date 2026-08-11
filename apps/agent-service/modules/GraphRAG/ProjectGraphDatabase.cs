using System.Security.Cryptography;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Modules.GraphRAG.FblAuthority;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 索引執行期使用的資料庫來源。完整連線字串只存在記憶體，不得記錄或序列化。
/// </summary>
public sealed record GraphDatabaseSource(
    ProjectDatabaseProvider Provider,
    string ConnectionString,
    string DatabaseName,
    string ConfigurationFingerprint = "legacy");

/// <summary>由專案的 DPAPI 設定建立唯讀資料庫來源。</summary>
public interface IGraphDatabaseSourceProvider
{
    /// <summary>取得本次索引可用的唯讀資料來源；尚未設定時回傳 null。</summary>
    Task<GraphDatabaseSource?> GetAsync(
        ProjectEntity project,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取得專案全部已設定且格式完整的外部資料來源。
    /// 這個方法只組合唯讀來源，不會測試或掃描資料庫；連線閘門由索引協調器統一執行。
    /// </summary>
    Task<IReadOnlyList<GraphDatabaseSource>> GetAllAsync(
        ProjectEntity project,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 將安全的專案資料庫設定轉為唯讀連線字串。
/// SQL Server 與 SQLite 都以使用者設定為準；外部資料庫只建立唯讀連線來源。
/// </summary>
public sealed class ProjectGraphDatabaseSourceProvider(
    IProjectDatabaseConfigurationStore configurations) : IGraphDatabaseSourceProvider
{
    /// <inheritdoc />
    public async Task<GraphDatabaseSource?> GetAsync(
        ProjectEntity project,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurations.GetAsync(
            project.Id,
            includePassword: true,
            cancellationToken);
        return configuration is null ? null : Build(configuration);
    }

    /// <summary>將專案全部 Provider 設定轉成穩定排序的唯讀來源。</summary>
    public async Task<IReadOnlyList<GraphDatabaseSource>> GetAllAsync(
        ProjectEntity project,
        CancellationToken cancellationToken = default)
    {
        var configuredSources = await configurations.GetAllAsync(
            project.Id,
            includePassword: true,
            cancellationToken);
        var sources = new List<GraphDatabaseSource>(configuredSources.Count);
        var errors = new List<string>();
        foreach (var configuration in configuredSources)
        {
            try
            {
                sources.Add(Build(configuration));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 只保留 Provider 與安全識別，絕不把完整連線字串或密碼帶入錯誤。
                var identity = configuration.Provider == ProjectDatabaseProvider.Sqlite
                    ? configuration.SqlitePath
                    : $"{configuration.Server}:{configuration.Port}/{configuration.DatabaseName}";
                errors.Add($"{configuration.Provider} ({identity})：{exception.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"資料庫設定檢查失敗；索引尚未開始。{string.Join("；", errors)}");
        }

        return sources
            .OrderBy(source => source.Provider)
            .ThenBy(source => source.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>由尚未儲存的設定建立測試用唯讀連線來源。</summary>
    public static GraphDatabaseSource Build(ProjectDatabaseConfiguration configuration) =>
        configuration.Provider switch
        {
            ProjectDatabaseProvider.SqlServer => BuildSqlServer(configuration),
            ProjectDatabaseProvider.Sqlite => BuildSqlite(configuration),
            _ => throw new InvalidOperationException("不支援的專案資料庫種類。"),
        };

    /// <summary>只使用非機密設定與 UpdatedAt 產生版本指紋。</summary>
    internal static string ComputeConfigurationFingerprint(ProjectDatabaseConfiguration configuration)
    {
        var material = string.Join(
            '\0',
            configuration.Provider.ToString(),
            configuration.Server?.Trim().ToUpperInvariant() ?? string.Empty,
            configuration.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            configuration.DatabaseName?.Trim().ToUpperInvariant() ?? string.Empty,
            configuration.Authentication?.ToString() ?? string.Empty,
            configuration.TrustServerCertificate ? "trust" : "verify",
            NormalizeSqlitePath(configuration.SqlitePath),
            configuration.UpdatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>建立強制 ApplicationIntent=ReadOnly 的 SQL Server 連線字串。</summary>
    private static GraphDatabaseSource BuildSqlServer(ProjectDatabaseConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Server) ||
            string.IsNullOrWhiteSpace(configuration.DatabaseName) ||
            configuration.Authentication is null)
        {
            throw new InvalidOperationException("SQL Server 設定缺少伺服器、資料庫或驗證方式。");
        }
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = configuration.Port is > 0
                ? $"{configuration.Server},{configuration.Port}"
                : configuration.Server,
            InitialCatalog = configuration.DatabaseName,
            IntegratedSecurity = configuration.Authentication == SqlServerAuthentication.IntegratedSecurity,
            Encrypt = true,
            TrustServerCertificate = configuration.TrustServerCertificate,
            PersistSecurityInfo = false,
            ConnectTimeout = 15,
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };
        if (!builder.IntegratedSecurity)
        {
            if (string.IsNullOrWhiteSpace(configuration.Username) || string.IsNullOrEmpty(configuration.Password))
            {
                throw new InvalidOperationException("SQL Server 帳號驗證缺少使用者名稱或密碼。");
            }
            builder.UserID = configuration.Username;
            builder.Password = configuration.Password;
        }
        return new GraphDatabaseSource(
            ProjectDatabaseProvider.SqlServer,
            builder.ConnectionString,
            configuration.DatabaseName,
            ComputeConfigurationFingerprint(configuration));
    }

    /// <summary>建立 Mode=ReadOnly 且不使用 pool 的 SQLite 連線字串。</summary>
    private static GraphDatabaseSource BuildSqlite(ProjectDatabaseConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.SqlitePath))
        {
            throw new InvalidOperationException("SQLite 設定缺少資料庫檔案路徑。");
        }
        var path = Path.GetFullPath(configuration.SqlitePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("設定的 SQLite 資料庫檔案不存在。", path);
        }
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
        };
        return new GraphDatabaseSource(
            ProjectDatabaseProvider.Sqlite,
            builder.ConnectionString,
            Path.GetFileNameWithoutExtension(path),
            ComputeConfigurationFingerprint(configuration));
    }

    /// <summary>正規化 SQLite 路徑，只作雜湊材料。</summary>
    private static string NormalizeSqlitePath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path).Replace('\\', '/').ToUpperInvariant();
}

/// <summary>
/// 資料庫設定頁使用的唯讀連線工具。
/// 類名為了維持既有 REST 端點注入契約而保留，但已不負責建立 Graph 節點或關係。
/// </summary>
public sealed class ProjectGraphDatabaseExtractor
{
    /// <summary>只開啟連線並執行 SELECT 1，確認使用者設定可用。</summary>
    public async Task TestConnectionAsync(
        GraphDatabaseSource source,
        CancellationToken cancellationToken = default)
    {
        if (source.Provider == ProjectDatabaseProvider.SqlServer)
        {
            await using var connection = new SqlConnection(source.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_NAME()";
            command.CommandTimeout = 15;
            var actualDatabase = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(actualDatabase, source.DatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQL Server 連線實際資料庫為 '{actualDatabase}'，與設定的 '{source.DatabaseName}' 不一致。");
            }
            return;
        }
        await using var sqlite = new SqliteConnection(source.ConnectionString);
        await sqlite.OpenAsync(cancellationToken);
        await using var commandSqlite = sqlite.CreateCommand();
        commandSqlite.CommandText = "SELECT 1";
        await commandSqlite.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>只查詢 SQL Server 系統目錄中的可連線資料庫名稱。</summary>
    public async Task<IReadOnlyList<string>> ListSqlServerDatabasesAsync(
        GraphDatabaseSource source,
        CancellationToken cancellationToken = default)
    {
        if (source.Provider != ProjectDatabaseProvider.SqlServer)
        {
            throw new InvalidOperationException("只有 SQL Server 支援資料庫清單。");
        }
        await using var connection = new SqlConnection(source.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [name]
            FROM sys.databases
            WHERE [state] = 0
              AND HAS_DBACCESS([name]) = 1
            ORDER BY [name];
            """;
        command.CommandTimeout = 15;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <summary>
    /// 以 SQLite 系統目錄建立唯讀 DB Object 清單。
    /// SQLite 沒有 FBL 菜單與 CustomReport authority tables，因此只回傳實際存在的使用者物件。
    /// </summary>
    public async Task<IReadOnlyList<DatabaseObjectCatalogItem>> LoadSqliteDatabaseObjectsAsync(
        GraphDatabaseSource source,
        CancellationToken cancellationToken = default)
    {
        if (source.Provider != ProjectDatabaseProvider.Sqlite)
        {
            throw new InvalidOperationException("只有 SQLite 支援 SQLite DB Object 清單。");
        }

        const string sql = """
            SELECT type, name
            FROM sqlite_schema
            WHERE type IN ('table', 'view', 'trigger', 'index')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;
        await using var connection = new SqliteConnection(source.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DatabaseObjectCatalogItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectType = reader.GetString(0);
            var kind = objectType switch
            {
                "table" => DatabaseObjectKind.Table,
                "view" => DatabaseObjectKind.View,
                "trigger" => DatabaseObjectKind.Trigger,
                "index" => DatabaseObjectKind.Index,
                _ => throw new InvalidOperationException($"不支援的 SQLite object type：{objectType}"),
            };
            result.Add(new DatabaseObjectCatalogItem(
                "main",
                reader.GetString(1),
                kind,
                "Sqlite",
                source.DatabaseName));
        }

        return result;
    }
}

/// <summary>
/// 建立 SQLite-only 的最小可用 GraphDocument。
/// 它保存原始碼中的 CodeClass 與 SQLite DB Object，刻意不套用 SQL Server 菜單及 FBL authority validator。
/// </summary>
public sealed class SqliteGraphDocumentBuilder
{
    /// <summary>
    /// 建立原始碼＋SQLite DB Object 圖；所有輸入均來自唯讀掃描結果。
    /// </summary>
    public async Task<GraphDocument> BuildAsync(
        string rootPath,
        GraphDatabaseSource source,
        IReadOnlyList<DatabaseObjectCatalogItem> databaseObjects,
        CancellationToken cancellationToken = default,
        bool sourceOnly = false)
    {
        var index = await CSharpSourceIndex.CreateAsync(rootPath, cancellationToken);
        var metadata = new GraphRunMetadata(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            rootPath,
            sourceOnly ? string.Empty : source.DatabaseName,
            GraphBuildStage.CompleteExtraction,
            null,
            source.ConfigurationFingerprint,
            sourceOnly ? "SourceOnly" : "Sqlite");
        var builder = new GraphDocumentBuilder(metadata);

        foreach (var type in index.Types)
        {
            CodeClassNodeFactory.Add(builder, type, CodeClassRole.Other);
        }

        foreach (var databaseObject in databaseObjects)
        {
            builder.AddNode(
                GraphNodeKind.DatabaseObject,
                databaseObject.CreateNodeKey(),
                new Dictionary<string, object?>
                {
                    ["provider"] = databaseObject.Provider,
                    ["database"] = databaseObject.DatabaseName,
                    ["schema"] = databaseObject.SchemaName,
                    ["name"] = databaseObject.ObjectName,
                    ["object_kind"] = databaseObject.Kind.ToString(),
                });
        }

        // SQLite-only 與沒有 DB 的專案也必須具有與 SQL Server 相同的 Solution／Project／
        // Type／Method semantic graph，不能再退化成只有 CodeClass 名稱的最小圖。
        var semantic = await new RoslynSemanticGraphExtractor()
            .ExtendAsync(builder.Build(), rootPath, cancellationToken);
        return semantic.Document;
    }
}
