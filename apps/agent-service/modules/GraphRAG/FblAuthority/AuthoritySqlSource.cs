using Microsoft.Data.SqlClient;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 定義 FBL 權威圖抽取器所需的唯讀資料來源。
/// 抽取核心只依賴這四組已人工驗證的資料，不會自行探索或修改資料庫。
/// </summary>
public interface IFblAuthoritySqlSource
{
    /// <summary>載入目前資料庫中 Released 且具有入口網址的中心菜單。</summary>
    Task<IReadOnlyList<MenuCatalogItem>> LoadMenusAsync(CancellationToken cancellationToken);

    /// <summary>載入 SQL Server 已存在的 Table、View、Procedure、Function 與 Synonym。</summary>
    Task<IReadOnlyList<DatabaseObjectCatalogItem>> LoadDatabaseObjectsAsync(CancellationToken cancellationToken);

    /// <summary>載入自訂報表的 RT、DS 與 PD 定義。</summary>
    Task<CustomReportCatalog> LoadCustomReportsAsync(CancellationToken cancellationToken);

    /// <summary>載入人工維護的覆核來源與菜單對應。</summary>
    Task<IReadOnlyList<ConfirmMappingItem>> LoadConfirmMappingsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// SQL Server 通用系統目錄的唯讀抽取契約。
/// 這個能力與 FBL 專屬資料表分離，讓同一組使用者設定的連線可以在沒有 FBL tables 時
/// 仍建立資料庫物件、欄位、參數與相依關係圖。
/// </summary>
public interface IGenericDatabaseMetadataSource
{
    /// <summary>從目前連線所指向的單一資料庫載入通用 metadata。</summary>
    Task<DatabaseMetadataCatalog> LoadDatabaseMetadataAsync(
        CancellationToken cancellationToken);
}

/// <summary>一次 SQL Server 通用系統目錄快照；不包含 server、帳號、密碼或連線字串。</summary>
public sealed record DatabaseMetadataCatalog(
    string Provider,
    string DatabaseName,
    IReadOnlyList<DatabaseObjectCatalogItem> Objects,
    IReadOnlyList<DatabaseColumnCatalogItem> Columns,
    IReadOnlyList<DatabaseParameterCatalogItem> Parameters,
    IReadOnlyList<DatabaseForeignKeyCatalogItem> ForeignKeys,
    IReadOnlyList<DatabaseDependencyCatalogItem> Dependencies);

/// <summary>資料表或檢視表的一個實際欄位。</summary>
public sealed record DatabaseColumnCatalogItem(
    string SchemaName,
    string ObjectName,
    int Ordinal,
    string Name,
    string DataType,
    short MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    bool IsPrimaryKey);

/// <summary>Stored Procedure 或 Function 的一個實際參數。</summary>
public sealed record DatabaseParameterCatalogItem(
    string SchemaName,
    string ObjectName,
    int Ordinal,
    string Name,
    string DataType,
    short MaxLength,
    byte Precision,
    byte Scale,
    bool IsOutput,
    bool IsReadOnly);

/// <summary>由 SQL Server foreign key catalog 證實的一組欄位對應。</summary>
public sealed record DatabaseForeignKeyCatalogItem(
    string ConstraintName,
    string SourceSchemaName,
    string SourceObjectName,
    string SourceColumnName,
    string TargetSchemaName,
    string TargetObjectName,
    string TargetColumnName,
    int Ordinal);

/// <summary>由 sys.sql_expression_dependencies 證實的同資料庫物件相依。</summary>
public sealed record DatabaseDependencyCatalogItem(
    string SourceSchemaName,
    string SourceObjectName,
    string TargetSchemaName,
    string TargetObjectName);

/// <summary>
/// 以 SQL Server 唯讀查詢實作 FBL 權威資料來源。
/// 每個方法只執行固定 SELECT；連線字串會強制加入 ApplicationIntent=ReadOnly，程式內沒有任何寫入 SQL。
/// </summary>
public sealed class FblSqlServerAuthoritySource :
    IFblAuthoritySqlSource,
    IGenericDatabaseMetadataSource
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

    private const string DatabaseColumnSql = """
        SELECT source_schema.name,
               source_object.name,
               source_column.column_id,
               source_column.name,
               TYPE_NAME(source_column.user_type_id),
               source_column.max_length,
               source_column.precision,
               source_column.scale,
               source_column.is_nullable,
               source_column.is_identity,
               source_column.is_computed,
               CONVERT(bit, CASE WHEN EXISTS (
                   SELECT 1
                   FROM sys.indexes AS primary_index
                   INNER JOIN sys.index_columns AS primary_column
                       ON primary_column.object_id = primary_index.object_id
                      AND primary_column.index_id = primary_index.index_id
                   WHERE primary_index.object_id = source_column.object_id
                     AND primary_index.is_primary_key = 1
                     AND primary_column.column_id = source_column.column_id
               ) THEN 1 ELSE 0 END)
        FROM sys.objects AS source_object
        INNER JOIN sys.schemas AS source_schema
            ON source_schema.schema_id = source_object.schema_id
        INNER JOIN sys.columns AS source_column
            ON source_column.object_id = source_object.object_id
        WHERE source_object.is_ms_shipped = 0
          AND source_object.type IN ('U', 'V')
        ORDER BY source_schema.name, source_object.name, source_column.column_id;
        """;

    private const string DatabaseParameterSql = """
        SELECT source_schema.name,
               source_object.name,
               source_parameter.parameter_id,
               source_parameter.name,
               TYPE_NAME(source_parameter.user_type_id),
               source_parameter.max_length,
               source_parameter.precision,
               source_parameter.scale,
               source_parameter.is_output,
               source_parameter.is_readonly
        FROM sys.objects AS source_object
        INNER JOIN sys.schemas AS source_schema
            ON source_schema.schema_id = source_object.schema_id
        INNER JOIN sys.parameters AS source_parameter
            ON source_parameter.object_id = source_object.object_id
        WHERE source_object.is_ms_shipped = 0
          AND source_object.type IN ('P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT')
          AND source_parameter.parameter_id > 0
        ORDER BY source_schema.name, source_object.name, source_parameter.parameter_id;
        """;

    private const string DatabaseForeignKeySql = """
        SELECT foreign_key.name,
               source_schema.name,
               source_table.name,
               source_column.name,
               target_schema.name,
               target_table.name,
               target_column.name,
               foreign_key_column.constraint_column_id
        FROM sys.foreign_keys AS foreign_key
        INNER JOIN sys.foreign_key_columns AS foreign_key_column
            ON foreign_key_column.constraint_object_id = foreign_key.object_id
        INNER JOIN sys.tables AS source_table
            ON source_table.object_id = foreign_key_column.parent_object_id
        INNER JOIN sys.schemas AS source_schema
            ON source_schema.schema_id = source_table.schema_id
        INNER JOIN sys.columns AS source_column
            ON source_column.object_id = foreign_key_column.parent_object_id
           AND source_column.column_id = foreign_key_column.parent_column_id
        INNER JOIN sys.tables AS target_table
            ON target_table.object_id = foreign_key_column.referenced_object_id
        INNER JOIN sys.schemas AS target_schema
            ON target_schema.schema_id = target_table.schema_id
        INNER JOIN sys.columns AS target_column
            ON target_column.object_id = foreign_key_column.referenced_object_id
           AND target_column.column_id = foreign_key_column.referenced_column_id
        ORDER BY source_schema.name,
                 source_table.name,
                 foreign_key.name,
                 foreign_key_column.constraint_column_id;
        """;

    private const string DatabaseDependencySql = """
        SELECT DISTINCT
               source_schema.name,
               source_object.name,
               target_schema.name,
               target_object.name
        FROM sys.sql_expression_dependencies AS dependency
        INNER JOIN sys.objects AS source_object
            ON source_object.object_id = dependency.referencing_id
        INNER JOIN sys.schemas AS source_schema
            ON source_schema.schema_id = source_object.schema_id
        INNER JOIN sys.objects AS target_object
            ON target_object.object_id = dependency.referenced_id
        INNER JOIN sys.schemas AS target_schema
            ON target_schema.schema_id = target_object.schema_id
        WHERE source_object.is_ms_shipped = 0
          AND target_object.is_ms_shipped = 0
          AND dependency.referenced_server_name IS NULL
          AND dependency.referenced_database_name IS NULL
          AND source_object.type IN ('V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT')
          AND target_object.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'SN')
        ORDER BY source_schema.name,
                 source_object.name,
                 target_schema.name,
                 target_object.name;
        """;

    private readonly string _connectionString;
    private readonly string _databaseName;

    /// <summary>取得目前連線實際指定的資料庫名稱，供 Graph metadata 與 stable key 使用。</summary>
    public string DatabaseName => _databaseName;

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
        _databaseName = builder.InitialCatalog;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MenuCatalogItem>> LoadMenusAsync(CancellationToken cancellationToken)
    {
        var result = new List<MenuCatalogItem>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        }
        catch (SqlException exception) when (IsMissingFblTable(exception))
        {
            // Generic SQL Server 專案不一定包含 FBL tables；缺少專屬表不影響通用 catalog。
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
                MapDatabaseObjectKind(reader.GetString(2).Trim()),
                Provider: "SqlServer",
                DatabaseName: _databaseName));
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
        try
        {
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
        }
        catch (SqlException exception) when (IsMissingFblTable(exception))
        {
            // Generic SQL Server 專案沒有人工覆核表時視為沒有 FBL overlay 資料。
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<DatabaseMetadataCatalog> LoadDatabaseMetadataAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var objects = await LoadDatabaseObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        var columns = await LoadDatabaseColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        var parameters = await LoadDatabaseParametersAsync(connection, cancellationToken).ConfigureAwait(false);
        var foreignKeys = await LoadDatabaseForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        var dependencies = await LoadDatabaseDependenciesAsync(connection, cancellationToken).ConfigureAwait(false);
        return new DatabaseMetadataCatalog(
            "SqlServer",
            _databaseName,
            objects,
            columns,
            parameters,
            foreignKeys,
            dependencies);
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
        try
        {
            await using var command = CreateCommand(connection, sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new CustomReportTemplateItem(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
            }
        }
        catch (SqlException exception) when (IsMissingFblTable(exception))
        {
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
        try
        {
            await using var command = CreateCommand(connection, sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new CustomReportDataSourceItem(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
            }
        }
        catch (SqlException exception) when (IsMissingFblTable(exception))
        {
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
        try
        {
            await using var command = CreateCommand(connection, sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new CustomParameterDataSourceItem(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }
        catch (SqlException exception) when (IsMissingFblTable(exception))
        {
        }

        return result;
    }

    /// <summary>使用現有唯讀連線載入通用資料庫物件。</summary>
    private async Task<IReadOnlyList<DatabaseObjectCatalogItem>> LoadDatabaseObjectsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseObjectCatalogItem>();
        await using var command = CreateCommand(connection, DatabaseObjectSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DatabaseObjectCatalogItem(
                reader.GetString(0),
                reader.GetString(1),
                MapDatabaseObjectKind(reader.GetString(2).Trim()),
                Provider: "SqlServer",
                DatabaseName: _databaseName));
        }

        return result;
    }

    /// <summary>載入 table/view 欄位與 PK 標記。</summary>
    private static async Task<IReadOnlyList<DatabaseColumnCatalogItem>> LoadDatabaseColumnsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseColumnCatalogItem>();
        await using var command = CreateCommand(connection, DatabaseColumnSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DatabaseColumnCatalogItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt16(5),
                reader.GetByte(6),
                reader.GetByte(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11)));
        }

        return result;
    }

    /// <summary>載入 Stored Procedure 與 Function 的參數。</summary>
    private static async Task<IReadOnlyList<DatabaseParameterCatalogItem>> LoadDatabaseParametersAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseParameterCatalogItem>();
        await using var command = CreateCommand(connection, DatabaseParameterSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DatabaseParameterCatalogItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt16(5),
                reader.GetByte(6),
                reader.GetByte(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9)));
        }

        return result;
    }

    /// <summary>載入 foreign key 的來源與目標欄位。</summary>
    private static async Task<IReadOnlyList<DatabaseForeignKeyCatalogItem>> LoadDatabaseForeignKeysAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseForeignKeyCatalogItem>();
        await using var command = CreateCommand(connection, DatabaseForeignKeySql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DatabaseForeignKeyCatalogItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7)));
        }

        return result;
    }

    /// <summary>載入同一個使用者設定資料庫內，可由 SQL Server 確認的物件相依。</summary>
    private static async Task<IReadOnlyList<DatabaseDependencyCatalogItem>> LoadDatabaseDependenciesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new List<DatabaseDependencyCatalogItem>();
        await using var command = CreateCommand(connection, DatabaseDependencySql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DatabaseDependencyCatalogItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return result;
    }

    /// <summary>SQL Server error 208 只代表 FBL 專屬資料表不存在。</summary>
    private static bool IsMissingFblTable(SqlException exception) => exception.Number == 208;

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
