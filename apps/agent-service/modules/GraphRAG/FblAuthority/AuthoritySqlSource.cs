using Microsoft.Data.SqlClient;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 定義 FBL 權威圖抽取器所需的唯讀資料來源。
/// 抽取核心只依賴這四組已人工驗證的資料，不會自行探索或修改資料庫。
/// </summary>
public interface IFblAuthoritySqlSource
{
    /// <summary>載入 Released 且具有入口網址的696個中心菜單。</summary>
    Task<IReadOnlyList<MenuCatalogItem>> LoadMenusAsync(CancellationToken cancellationToken);

    /// <summary>載入 SQL Server 已存在的 Table、View、Procedure、Function 與 Synonym。</summary>
    Task<IReadOnlyList<DatabaseObjectCatalogItem>> LoadDatabaseObjectsAsync(CancellationToken cancellationToken);

    /// <summary>載入自訂報表的 RT、DS 與 PD 定義。</summary>
    Task<CustomReportCatalog> LoadCustomReportsAsync(CancellationToken cancellationToken);

    /// <summary>載入人工維護的覆核來源與菜單對應。</summary>
    Task<IReadOnlyList<ConfirmMappingItem>> LoadConfirmMappingsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 以 SQL Server 唯讀查詢實作 FBL 權威資料來源。
/// 每個方法只執行固定 SELECT；連線字串會強制加入 ApplicationIntent=ReadOnly，程式內沒有任何寫入 SQL。
/// </summary>
public sealed class FblSqlServerAuthoritySource : IFblAuthoritySqlSource
{
    private const int QueryTimeoutSeconds = 120;
    private const string MenuSql = """
        SELECT ID, Name, LinkAddress, Description
        FROM dbo.tblMenuMap
        WHERE Released = 1
          AND ISNULL(LinkAddress, '') <> ''
          AND ID NOT LIKE '88%'
          AND ID NOT IN (90000445, 90000446)
        ORDER BY ID;
        """;

    private const string DatabaseObjectSql = """
        SELECT SCHEMA_NAME(o.schema_id), o.name, o.type
        FROM sys.objects AS o
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'SN')
        ORDER BY SCHEMA_NAME(o.schema_id), o.name;
        """;

    private const string ConfirmSql = """
        SELECT ConfirmSourceType, MenuID, MaintainMenuID, WaitForConfirmName
        FROM dbo.tblAsyncConfirmSourceTypeMapping
        ORDER BY ConfirmSourceType, MenuID, MaintainMenuID;
        """;

    private readonly string _connectionString;

    /// <summary>
    /// 建立固定 SELECT 資料來源。呼叫端仍負責從秘密儲存取得帳密；本類別不記錄或輸出連線字串。
    /// </summary>
    public FblSqlServerAuthoritySource(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };
        _connectionString = builder.ConnectionString;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MenuCatalogItem>> LoadMenusAsync(CancellationToken cancellationToken)
    {
        var result = new List<MenuCatalogItem>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, MenuSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MenuCatalogItem(
                Convert.ToInt64(reader.GetValue(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatabaseObjectCatalogItem>> LoadDatabaseObjectsAsync(CancellationToken cancellationToken)
    {
        var result = new List<DatabaseObjectCatalogItem>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, DatabaseObjectSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DatabaseObjectCatalogItem(
                reader.GetString(0),
                reader.GetString(1),
                MapDatabaseObjectKind(reader.GetString(2).Trim())));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<CustomReportCatalog> LoadCustomReportsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var templates = await LoadTemplatesAsync(connection, cancellationToken).ConfigureAwait(false);
        var dataSources = await LoadDataSourcesAsync(connection, cancellationToken).ConfigureAwait(false);
        var parameterSources = await LoadParameterSourcesAsync(connection, cancellationToken).ConfigureAwait(false);
        return new CustomReportCatalog(templates, dataSources, parameterSources);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfirmMappingItem>> LoadConfirmMappingsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ConfirmMappingItem>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, ConfirmSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ConfirmMappingItem(
                Convert.ToInt32(reader.GetValue(0)),
                Convert.ToInt64(reader.GetValue(1)),
                Convert.ToInt64(reader.GetValue(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return result;
    }

    /// <summary>開啟一次性唯讀連線，讓取消作業能立即釋放連線。</summary>
    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 建立固定 SELECT command。兩分鐘足以容納 FBL 目錄查詢，同時避免資料庫異常時讓索引永久卡住。
    /// </summary>
    private static SqlCommand CreateCommand(SqlConnection connection, string sql) => new(sql, connection)
    {
        CommandTimeout = QueryTimeoutSeconds,
    };

    /// <summary>載入自訂報表樣板 RT。</summary>
    private static async Task<IReadOnlyList<CustomReportTemplateItem>> LoadTemplatesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT TemplateID, TemplateName, CONVERT(nvarchar(max), TemplateXML) FROM dbo.tblCustomDesignRiskReportTemplate;";
        var result = new List<CustomReportTemplateItem>();
        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CustomReportTemplateItem(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
        }

        return result;
    }

    /// <summary>載入自訂報表資料來源 DS。</summary>
    private static async Task<IReadOnlyList<CustomReportDataSourceItem>> LoadDataSourcesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT SerialID, Description, CONVERT(nvarchar(max), XMLDefinition) FROM dbo.tblCustomDesignReportDataSource;";
        var result = new List<CustomReportDataSourceItem>();
        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CustomReportDataSourceItem(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
        }

        return result;
    }

    /// <summary>載入自訂報表參數資料來源 PD。</summary>
    private static async Task<IReadOnlyList<CustomParameterDataSourceItem>> LoadParameterSourcesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT SerialID, Description FROM dbo.tblCustomDesignReportCustomParameterDataSource;";
        var result = new List<CustomParameterDataSourceItem>();
        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CustomParameterDataSourceItem(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return result;
    }

    /// <summary>把 SQL Server sys.objects type code 轉成受控 enum。</summary>
    private static DatabaseObjectKind MapDatabaseObjectKind(string objectType) => objectType switch
    {
        "U" => DatabaseObjectKind.Table,
        "V" => DatabaseObjectKind.View,
        "P" or "PC" => DatabaseObjectKind.StoredProcedure,
        "FN" or "IF" or "TF" or "FS" or "FT" => DatabaseObjectKind.Function,
        "SN" => DatabaseObjectKind.Synonym,
        _ => throw new InvalidOperationException($"未支援的 SQL Server 物件種類：{objectType}"),
    };
}
