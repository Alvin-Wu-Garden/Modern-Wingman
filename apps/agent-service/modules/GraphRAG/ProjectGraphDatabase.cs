using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AgentService.Modules.GraphRAG;

/// <summary>索引執行期使用的資料庫來源；完整連線字串禁止記錄或序列化。</summary>
public sealed record GraphDatabaseSource(
    ProjectDatabaseProvider Provider,
    string ConnectionString,
    string DatabaseName);

/// <summary>由專案的 DPAPI 設定建立唯讀資料庫來源。</summary>
public interface IGraphDatabaseSourceProvider
{
    /// <summary>取得專案本次索引可使用的唯讀資料庫來源；未設定時回傳 null。</summary>
    Task<GraphDatabaseSource?> GetAsync(
        ProjectEntity project,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 將安全的專案資料庫設定轉成驅動程式連線字串。
/// 連線字串只存在於目前索引請求的記憶體中，不寫入 log、SQLite 或 Neo4j。
/// </summary>
public sealed class ProjectGraphDatabaseSourceProvider(
    IProjectDatabaseConfigurationStore configurations) : IGraphDatabaseSourceProvider
{
    /// <summary>解密專案設定並在記憶體內建立唯讀連線字串。</summary>
    public async Task<GraphDatabaseSource?> GetAsync(
        ProjectEntity project,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurations.GetAsync(
            project.Id,
            includePassword: true,
            cancellationToken);
        if (configuration is null)
            return null;

        return Build(configuration);
    }

    /// <summary>
    /// 將記憶體內的候選設定轉成連線來源。
    /// 設定頁的「測試連線」可直接使用此方法，不必先把密碼寫入設定資料庫。
    /// </summary>
    public static GraphDatabaseSource Build(
        ProjectDatabaseConfiguration configuration) =>
        configuration.Provider switch
        {
            ProjectDatabaseProvider.SqlServer => BuildSqlServer(configuration),
            ProjectDatabaseProvider.Sqlite => BuildSqlite(configuration),
            _ => throw new InvalidOperationException("不支援的專案資料庫種類。"),
        };

    private static GraphDatabaseSource BuildSqlServer(
        ProjectDatabaseConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Server) ||
            string.IsNullOrWhiteSpace(configuration.DatabaseName) ||
            configuration.Authentication is null)
        {
            throw new InvalidOperationException("SQL Server 設定缺少伺服器、資料庫或驗證方式。");
        }

        var dataSource = configuration.Port is > 0
            ? $"{configuration.Server},{configuration.Port}"
            : configuration.Server;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = configuration.DatabaseName,
            IntegratedSecurity =
                configuration.Authentication == SqlServerAuthentication.IntegratedSecurity,
            Encrypt = true,
            TrustServerCertificate = configuration.TrustServerCertificate,
            PersistSecurityInfo = false,
            ConnectTimeout = 15,
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };
        if (!builder.IntegratedSecurity)
        {
            if (string.IsNullOrWhiteSpace(configuration.Username) ||
                string.IsNullOrEmpty(configuration.Password))
            {
                throw new InvalidOperationException("SQL Server 帳號驗證缺少使用者名稱或密碼。");
            }
            builder.UserID = configuration.Username;
            builder.Password = configuration.Password;
        }

        return new GraphDatabaseSource(
            ProjectDatabaseProvider.SqlServer,
            builder.ConnectionString,
            configuration.DatabaseName);
    }

    private static GraphDatabaseSource BuildSqlite(
        ProjectDatabaseConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.SqlitePath))
            throw new InvalidOperationException("SQLite 設定缺少資料庫檔案路徑。");
        var path = Path.GetFullPath(configuration.SqlitePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("設定的 SQLite 資料庫檔案不存在。", path);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            // 專案 SQLite 是外部檔案；停用 pool 才能在索引或測試結束後立即釋放檔案鎖。
            Pooling = false,
        };
        return new GraphDatabaseSource(
            ProjectDatabaseProvider.Sqlite,
            builder.ConnectionString,
            Path.GetFileNameWithoutExtension(path));
    }
}

/// <summary>
/// 封裝 SQL Server 與 SQLite 的資料庫抽取差異，讓索引主流程維持單一路徑。
/// 只抽取物件級 metadata 與可證明的 View 依賴，不讀取一般業務資料列。
/// </summary>
public sealed partial class ProjectGraphDatabaseExtractor(
    SqlServerGraphExtractor sqlServer,
    ILogger<ProjectGraphDatabaseExtractor> logger)
{
    /// <summary>計算資料庫 metadata 指紋，供索引 no-op 判定使用。</summary>
    public Task<string?> ComputeFingerprintAsync(
        GraphDatabaseSource source,
        CancellationToken cancellationToken = default) =>
        source.Provider == ProjectDatabaseProvider.SqlServer
            ? sqlServer.ComputeDatabaseFingerprintAsync(ToSqlServer(source), cancellationToken)
            : ComputeSqliteFingerprintAsync(source, cancellationToken);

    /// <summary>依資料庫種類抽取物件級節點與可證明的依賴關係。</summary>
    public Task<GraphFragment> ExtractAsync(
        GraphDatabaseSource source,
        CancellationToken cancellationToken = default) =>
        source.Provider == ProjectDatabaseProvider.SqlServer
            ? sqlServer.ExtractDatabaseAsync(ToSqlServer(source), cancellationToken)
            : ExtractSqliteAsync(source, cancellationToken);

    /// <summary>供設定畫面的「測試連線」使用；只開啟連線並查詢常數。</summary>
    public async Task TestConnectionAsync(
        GraphDatabaseSource source,
        CancellationToken cancellationToken = default)
    {
        if (source.Provider == ProjectDatabaseProvider.SqlServer)
        {
            await using var connection = new SqlConnection(source.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 15;
            await command.ExecuteScalarAsync(cancellationToken);
            return;
        }

        await using var sqlite = new SqliteConnection(source.ConnectionString);
        await sqlite.OpenAsync(cancellationToken);
        await using var sqliteCommand = sqlite.CreateCommand();
        sqliteCommand.CommandText = "SELECT 1";
        await sqliteCommand.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// 列出目前 SQL Server 帳號可連線的線上資料庫，供設定畫面選取。
    /// 僅讀取系統目錄中的名稱，不讀取任何業務資料，也不保存候選連線字串。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListSqlServerDatabasesAsync(
        GraphDatabaseSource source,
        CancellationToken cancellationToken = default)
    {
        if (source.Provider != ProjectDatabaseProvider.SqlServer)
            throw new InvalidOperationException("只有 SQL Server 支援資料庫清單。");

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

        var databases = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            databases.Add(reader.GetString(0));
        return databases;
    }

    private static SqlServerGraphSource ToSqlServer(GraphDatabaseSource source) =>
        new(source.ConnectionString, source.DatabaseName);

    private static async Task<string?> ComputeSqliteFingerprintAsync(
        GraphDatabaseSource source,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(source.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, COALESCE(sql, '')
            FROM sqlite_master
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
        return GraphIdentity.Sha256(string.Join('\n', rows));
    }

    private async Task<GraphFragment> ExtractSqliteAsync(
        GraphDatabaseSource source,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(source.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, COALESCE(sql, '')
            FROM sqlite_master
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            LIMIT 10000;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var objects = new List<(string Type, string Name, string Sql)>();
        while (await reader.ReadAsync(cancellationToken))
            objects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        var fragment = new GraphFragment();
        var knownNames = objects
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in objects)
        {
            var role = item.Type == "view" ? GraphRoles.View : GraphRoles.Table;
            var id = GraphIdentity.SqlData(
                source.DatabaseName,
                "main",
                item.Type,
                item.Name);
            var evidence = new GraphEvidence(
                GraphEvidenceSource.Sql,
                GraphConfidence.Exact,
                $"db:{GraphIdentity.NormalizeRequiredToken(source.DatabaseName, "databaseName")}/sqlite_master",
                "由 SQLite sqlite_master 的物件定義取得，未讀取任何業務資料列。");
            fragment.Nodes.Add(new GraphNode(
                id,
                GraphNodeKind.Data,
                role,
                item.Name,
                $"{item.Name} main.{item.Name}",
                "sql",
                "sqlite",
                "active",
                [item.Name, $"main.{item.Name}"],
                null,
                null,
                null,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["database"] = source.DatabaseName,
                    ["schema"] = "main",
                    ["objectType"] = item.Type,
                },
                [evidence]));

            if (item.Type != "view")
                continue;
            foreach (Match match in ViewDependency().Matches(item.Sql))
            {
                var targetName = match.Groups["name"].Value.Trim('"', '`', '[', ']');
                if (!knownNames.Contains(targetName) ||
                    string.Equals(targetName, item.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var target = objects.First(value =>
                    string.Equals(value.Name, targetName, StringComparison.OrdinalIgnoreCase));
                var targetId = GraphIdentity.SqlData(
                    source.DatabaseName,
                    "main",
                    target.Type,
                    target.Name);
                fragment.Edges.Add(new GraphEdge(
                    GraphIdentity.Edge(id, GraphEdgeKind.Reads, targetId),
                    id,
                    GraphEdgeKind.Reads,
                    targetId,
                    [evidence with
                    {
                        Confidence = GraphConfidence.Resolved,
                        Reason = "由 SQLite View 的 FROM／JOIN 定義解析資料依賴。",
                    }]));
            }
        }

        logger.LogInformation(
            "SQLite metadata 抽取完成：{ObjectCount} 個資料物件。",
            objects.Count);
        return fragment;
    }

    [GeneratedRegex(
        @"(?i)\b(?:FROM|JOIN)\s+(?:main\.)?(?<name>[""`\[]?[A-Za-z_][A-Za-z0-9_$]*[""`\]]?)")]
    private static partial Regex ViewDependency();
}
