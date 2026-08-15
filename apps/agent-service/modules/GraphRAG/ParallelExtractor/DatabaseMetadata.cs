namespace AgentService.Modules.GraphRAG.ParallelExtractor;

using Microsoft.Data.SqlClient;

/// <summary>定義「DatabaseMetadata」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class DatabaseMetadata
{
    public string DatabaseName { get; init; } = string.Empty;
    public List<DbObjectInfo> Objects { get; } = new();
    public List<DbColumnInfo> Columns { get; } = new();
    public List<DbParameterInfo> Parameters { get; } = new();
    public List<DbForeignKeyInfo> ForeignKeys { get; } = new();
    public List<MenuRecord> Menus { get; } = new();
    public List<ReportTemplateRecord> ReportTemplates { get; } = new();
    public List<ReportDataSourceRecord> ReportDataSources { get; } = new();
    public List<CustomParameterDataSourceRecord> CustomParameterDataSources { get; } = new();
    public List<CsvFormatRecord> CsvFormats { get; } = new();
    public List<ScheduleRecord> Schedules { get; } = new();
    public List<ScheduleTaskRecord> ScheduleTasks { get; } = new();

    public Dictionary<string, DbObjectInfo> ByQualifiedName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DbObjectInfo>> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>執行「IndexObjects」所代表的圖譜抽取或匯入工作。</summary>
    public void IndexObjects()
    {
        ByQualifiedName.Clear();
        ByName.Clear();
        foreach (var item in Objects)
        {
            ByQualifiedName[$"{item.SchemaName}.{item.Name}"] = item;
            if (!ByName.TryGetValue(item.Name, out var list))
            {
                list = new List<DbObjectInfo>();
                ByName[item.Name] = list;
            }

            list.Add(item);
        }
    }

    public DbObjectInfo? Resolve(string? schemaName, string objectName, string? objectType = null)
    {
        var cleanName = objectName.Trim().Trim('[', ']');
        if (!string.IsNullOrWhiteSpace(schemaName) &&
            ByQualifiedName.TryGetValue($"{schemaName.Trim().Trim('[', ']')}.{cleanName}", out var qualified))
        {
            return objectType is null || qualified.ObjectType.Equals(objectType, StringComparison.OrdinalIgnoreCase)
                ? qualified
                : null;
        }

        if (!ByName.TryGetValue(cleanName, out var candidates))
        {
            return null;
        }

        var filtered = objectType is null
            ? candidates
            : candidates.Where(item => item.ObjectType.Equals(objectType, StringComparison.OrdinalIgnoreCase)).ToList();
        return filtered.Count == 1 ? filtered[0] : filtered.FirstOrDefault();
    }

    /// <summary>取得「LoadAsync」所代表的圖譜抽取或匯入工作。</summary>
    public static async Task<DatabaseMetadata> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var metadata = new DatabaseMetadata
        {
            DatabaseName = await ScalarStringAsync(connection, "SELECT DB_NAME();", cancellationToken)
        };

        await LoadObjectsAsync(connection, metadata, cancellationToken);
        metadata.IndexObjects();
        await LoadColumnsAsync(connection, metadata, cancellationToken);
        await LoadParametersAsync(connection, metadata, cancellationToken);
        await LoadForeignKeysAsync(connection, metadata, cancellationToken);
        // FBL 業務表屬於同一個 capability。非 FBL 資料庫沒有這些表時，
        // 仍保留通用 sys catalog；若表存在則完全沿用原始抽取器的資料列邏輯。
        if (await HasFblSchemaAsync(connection, cancellationToken))
        {
            await LoadMenusAsync(connection, metadata, cancellationToken);
            await LoadReportTemplatesAsync(connection, metadata, cancellationToken);
            await LoadReportDataSourcesAsync(connection, metadata, cancellationToken);
            await LoadCustomParameterDataSourcesAsync(connection, metadata, cancellationToken);
            await LoadCsvFormatsAsync(connection, metadata, cancellationToken);
            await LoadSchedulesAsync(connection, metadata, cancellationToken);
            await LoadScheduleTasksAsync(connection, metadata, cancellationToken);
        }
        return metadata;
    }

    private static async Task<bool> HasFblSchemaAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN
                OBJECT_ID(N'dbo.tblMenuMap', N'U') IS NOT NULL AND
                OBJECT_ID(N'dbo.tblCustomDesignRiskReportTemplate', N'U') IS NOT NULL AND
                OBJECT_ID(N'dbo.tblCustomDesignReportDataSource', N'U') IS NOT NULL AND
                OBJECT_ID(N'dbo.tblCustomDesignReportCustomParameterDataSource', N'U') IS NOT NULL AND
                OBJECT_ID(N'dbo.tblCSVFormat', N'U') IS NOT NULL AND
                OBJECT_ID(N'dbo.tblSchedule', N'U') IS NOT NULL AND
                OBJECT_ID(N'dbo.tblScheduleTask', N'U') IS NOT NULL
            THEN 1 ELSE 0 END;
            """,
            connection);
        command.CommandTimeout = 30;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>取得「LoadObjectsAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadObjectsAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name, o.name, o.object_id, o.type, o.type_desc,
                   o.create_date, o.modify_date, sm.definition
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.sql_modules sm ON sm.object_id = o.object_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U','V','P','FN','IF','TF','FS','FT')
            ORDER BY s.name, o.name;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sqlType = reader.GetString(3).Trim();
            metadata.Objects.Add(new DbObjectInfo
            {
                SchemaName = reader.GetString(0),
                Name = reader.GetString(1),
                ObjectId = reader.GetInt32(2),
                ObjectType = sqlType switch
                {
                    "U" => "Table",
                    "V" => "View",
                    "P" => "StoredProcedure",
                    _ => "UDF"
                },
                SqlType = sqlType,
                TypeDescription = reader.GetString(4).Trim(),
                CreateDate = reader.GetDateTime(5).ToString("O"),
                ModifyDate = reader.GetDateTime(6).ToString("O"),
                Definition = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }
    }

    /// <summary>取得「LoadColumnsAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadColumnsAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name, o.name, c.column_id, c.name,
                   TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.columns c ON c.object_id = o.object_id
            WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V')
            ORDER BY s.name, o.name, c.column_id;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.Columns.Add(new DbColumnInfo
            {
                SchemaName = reader.GetString(0),
                ObjectName = reader.GetString(1),
                Ordinal = reader.GetInt32(2),
                Name = reader.GetString(3),
                DataType = reader.GetString(4),
                MaxLength = reader.GetInt16(5),
                Precision = reader.GetByte(6),
                Scale = reader.GetByte(7),
                IsNullable = reader.GetBoolean(8),
                IsIdentity = reader.GetBoolean(9),
                IsComputed = reader.GetBoolean(10)
            });
        }

        await reader.CloseAsync();

        const string pkSql = """
            SELECT s.name, t.name, c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE i.is_primary_key = 1;
            """;
        await using var pkCommand = new SqlCommand(pkSql, connection);
        await using var pkReader = await pkCommand.ExecuteReaderAsync(cancellationToken);
        var primaryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await pkReader.ReadAsync(cancellationToken))
        {
            primaryKeys.Add($"{pkReader.GetString(0)}.{pkReader.GetString(1)}.{pkReader.GetString(2)}");
        }

        foreach (var column in metadata.Columns)
        {
            column.IsPrimaryKey = primaryKeys.Contains($"{column.SchemaName}.{column.ObjectName}.{column.Name}");
        }
    }

    /// <summary>取得「LoadParametersAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadParametersAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name, o.name, p.parameter_id, p.name,
                   TYPE_NAME(p.user_type_id), p.max_length, p.precision, p.scale,
                   p.is_output, p.is_readonly
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.parameters p ON p.object_id = o.object_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('P','FN','IF','TF','FS','FT')
              AND p.parameter_id > 0
            ORDER BY s.name, o.name, p.parameter_id;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.Parameters.Add(new DbParameterInfo
            {
                SchemaName = reader.GetString(0),
                ObjectName = reader.GetString(1),
                Ordinal = reader.GetInt32(2),
                Name = reader.GetString(3),
                DataType = reader.GetString(4),
                MaxLength = reader.GetInt16(5),
                Precision = reader.GetByte(6),
                Scale = reader.GetByte(7),
                IsOutput = reader.GetBoolean(8),
                IsReadonly = reader.GetBoolean(9)
            });
        }
    }

    /// <summary>取得「LoadForeignKeysAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadForeignKeysAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT fk.name,
                   ps.name, pt.name, pc.name,
                   rs.name, rt.name, rc.name,
                   fkc.constraint_column_id
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables pt ON pt.object_id = fkc.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            ORDER BY ps.name, pt.name, fk.name, fkc.constraint_column_id;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.ForeignKeys.Add(new DbForeignKeyInfo
            {
                ConstraintName = reader.GetString(0),
                ParentSchema = reader.GetString(1),
                ParentTable = reader.GetString(2),
                ParentColumn = reader.GetString(3),
                ReferencedSchema = reader.GetString(4),
                ReferencedTable = reader.GetString(5),
                ReferencedColumn = reader.GetString(6),
                Ordinal = reader.GetInt32(7)
            });
        }
    }

    /// <summary>取得「LoadMenusAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadMenusAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ID, Parent, Name, Released, Weight, LinkAddress, Description, IconCls, IsOpenNewTab
            FROM dbo.tblMenuMap
            WHERE Released = 1
              AND ISNULL(LinkAddress, '') <> ''
              AND CONVERT(varchar(30), ID) NOT LIKE '88%'
              AND ID NOT IN (90000445, 90000446);
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.Menus.Add(new MenuRecord
            {
                Id = reader.GetInt64(0),
                ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Name = reader.GetString(2),
                Released = reader.GetBoolean(3),
                Weight = reader.GetInt32(4),
                LinkAddress = reader.GetString(5),
                Description = reader.GetString(6),
                IconClass = reader.GetString(7),
                IsOpenNewTab = reader.IsDBNull(8) ? null : reader.GetBoolean(8)
            });
        }
    }

    /// <summary>取得「LoadReportTemplatesAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadReportTemplatesAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TemplateID, TemplateName, Description, [Type], ProductType, TickMode,
                   IsDefault, CreateDateTime, ModifyDateTime, Scope, IsGroup, IsPackageMode,
                   IsFax, IsSendMail, CONVERT(nvarchar(max), TemplateXML)
            FROM dbo.tblCustomDesignRiskReportTemplate;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.ReportTemplates.Add(new ReportTemplateRecord
            {
                TemplateId = reader.GetGuid(0),
                Name = reader.GetString(1),
                Description = reader.GetString(2),
                Type = reader.GetInt32(3),
                ProductType = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                TickMode = reader.GetInt32(5),
                IsDefault = reader.GetBoolean(6),
                CreateDateTime = reader.GetDateTime(7).ToString("O"),
                ModifyDateTime = reader.GetDateTime(8).ToString("O"),
                Scope = reader.GetBoolean(9),
                IsGroup = reader.IsDBNull(10) ? null : reader.GetBoolean(10),
                IsPackageMode = reader.IsDBNull(11) ? null : reader.GetBoolean(11),
                IsFax = reader.IsDBNull(12) ? null : reader.GetBoolean(12),
                IsSendMail = reader.GetBoolean(13),
                Xml = reader.IsDBNull(14) ? null : reader.GetString(14)
            });
        }
    }

    /// <summary>取得「LoadReportDataSourcesAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadReportDataSourcesAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Description, XMLDefinition, Category, Scope, UserID,
                   CreateDateTime, ModifyDateTime, SerialID, IsSingleData
            FROM dbo.tblCustomDesignReportDataSource;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.ReportDataSources.Add(new ReportDataSourceRecord
            {
                Description = reader.GetString(0),
                Xml = reader.IsDBNull(1) ? null : reader.GetString(1),
                Category = reader.GetInt32(2),
                Scope = reader.GetBoolean(3),
                UserId = reader.GetInt64(4),
                CreateDateTime = reader.GetDateTime(5).ToString("O"),
                ModifyDateTime = reader.GetDateTime(6).ToString("O"),
                SerialId = reader.GetGuid(7),
                IsSingleData = reader.GetBoolean(8)
            });
        }
    }

    /// <summary>取得「LoadCustomParameterDataSourcesAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadCustomParameterDataSourcesAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Description, XMLDefinition, Category, Scope, UserID,
                   CreateDateTime, ModifyDateTime, SerialID
            FROM dbo.tblCustomDesignReportCustomParameterDataSource;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.CustomParameterDataSources.Add(new CustomParameterDataSourceRecord
            {
                Description = reader.GetString(0),
                Xml = reader.IsDBNull(1) ? null : reader.GetString(1),
                Category = reader.GetInt32(2),
                Scope = reader.GetBoolean(3),
                UserId = reader.GetInt64(4),
                CreateDateTime = reader.GetDateTime(5).ToString("O"),
                ModifyDateTime = reader.GetDateTime(6).ToString("O"),
                SerialId = reader.GetGuid(7)
            });
        }
    }

    /// <summary>取得「LoadCsvFormatsAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadCsvFormatsAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FormatType, Version, Enable, Lastest, ModiTime, ParentFormatType
            FROM dbo.tblCSVFormat;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.CsvFormats.Add(new CsvFormatRecord
            {
                FormatType = reader.GetString(0),
                Version = reader.GetDecimal(1),
                Enable = reader.GetBoolean(2),
                Latest = reader.GetBoolean(3),
                ModifyTime = reader.GetDateTime(4).ToString("O"),
                ParentFormatType = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }
    }

    /// <summary>取得「LoadSchedulesAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadSchedulesAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ID, Description, Frequency, ExtensionParameter, EnableDateTime,
                   NextStartupDateTime, LastStartupDateTime, LastFinishDateTime,
                   Enabled, Category, Scope, TaskRunMode, ScheduleQueueID
            FROM dbo.tblSchedule;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.Schedules.Add(new ScheduleRecord
            {
                Id = reader.GetInt64(0),
                Description = reader.GetString(1),
                Frequency = reader.GetInt32(2),
                ExtensionParameter = reader.GetString(3),
                EnableDateTime = reader.GetDateTime(4).ToString("O"),
                NextStartupDateTime = reader.IsDBNull(5) ? null : reader.GetDateTime(5).ToString("O"),
                LastStartupDateTime = reader.IsDBNull(6) ? null : reader.GetDateTime(6).ToString("O"),
                LastFinishDateTime = reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("O"),
                Enabled = reader.GetBoolean(8),
                Category = reader.GetInt32(9),
                Scope = reader.GetInt32(10),
                TaskRunMode = reader.GetInt32(11),
                ScheduleQueueId = reader.GetInt32(12)
            });
        }
    }

    /// <summary>取得「LoadScheduleTasksAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task LoadScheduleTasksAsync(SqlConnection connection, DatabaseMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ScheduleID, TaskID, Name, Parameter, Description
            FROM dbo.tblScheduleTask;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            metadata.ScheduleTasks.Add(new ScheduleTaskRecord
            {
                ScheduleId = reader.GetInt64(0),
                TaskId = reader.GetInt32(1),
                Name = reader.GetString(2),
                Parameter = reader.IsDBNull(3) ? null : reader.GetString(3),
                Description = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }
    }

    /// <summary>執行「ScalarStringAsync」所代表的圖譜抽取或匯入工作。</summary>
    private static async Task<string> ScalarStringAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
    }
}

/// <summary>定義「DbObjectInfo」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class DbObjectInfo
{
    public string SchemaName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int ObjectId { get; init; }
    public string ObjectType { get; init; } = string.Empty;
    public string SqlType { get; init; } = string.Empty;
    public string TypeDescription { get; init; } = string.Empty;
    public string CreateDate { get; init; } = string.Empty;
    public string ModifyDate { get; init; } = string.Empty;
    public string? Definition { get; init; }
}

/// <summary>定義「DbColumnInfo」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class DbColumnInfo
{
    public string SchemaName { get; init; } = string.Empty;
    public string ObjectName { get; init; } = string.Empty;
    public int Ordinal { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DataType { get; init; } = string.Empty;
    public short MaxLength { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public bool IsNullable { get; init; }
    public bool IsIdentity { get; init; }
    public bool IsComputed { get; init; }
    public bool IsPrimaryKey { get; set; }
}

/// <summary>定義「DbParameterInfo」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class DbParameterInfo
{
    public string SchemaName { get; init; } = string.Empty;
    public string ObjectName { get; init; } = string.Empty;
    public int Ordinal { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DataType { get; init; } = string.Empty;
    public short MaxLength { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public bool IsOutput { get; init; }
    public bool IsReadonly { get; init; }
}

/// <summary>定義「DbForeignKeyInfo」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class DbForeignKeyInfo
{
    public string ConstraintName { get; init; } = string.Empty;
    public string ParentSchema { get; init; } = string.Empty;
    public string ParentTable { get; init; } = string.Empty;
    public string ParentColumn { get; init; } = string.Empty;
    public string ReferencedSchema { get; init; } = string.Empty;
    public string ReferencedTable { get; init; } = string.Empty;
    public string ReferencedColumn { get; init; } = string.Empty;
    public int Ordinal { get; init; }
}

/// <summary>定義「MenuRecord」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class MenuRecord
{
    public long Id { get; init; }
    public long? ParentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Released { get; init; }
    public int Weight { get; init; }
    public string LinkAddress { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string IconClass { get; init; } = string.Empty;
    public bool? IsOpenNewTab { get; init; }
}

/// <summary>定義「ReportTemplateRecord」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class ReportTemplateRecord
{
    public Guid TemplateId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Type { get; init; }
    public int? ProductType { get; init; }
    public int TickMode { get; init; }
    public bool IsDefault { get; init; }
    public string CreateDateTime { get; init; } = string.Empty;
    public string ModifyDateTime { get; init; } = string.Empty;
    public bool Scope { get; init; }
    public bool? IsGroup { get; init; }
    public bool? IsPackageMode { get; init; }
    public bool? IsFax { get; init; }
    public bool IsSendMail { get; init; }
    public string? Xml { get; init; }
}

/// <summary>定義「ReportDataSourceRecord」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class ReportDataSourceRecord
{
    public string Description { get; init; } = string.Empty;
    public string? Xml { get; init; }
    public int Category { get; init; }
    public bool Scope { get; init; }
    public long UserId { get; init; }
    public string CreateDateTime { get; init; } = string.Empty;
    public string ModifyDateTime { get; init; } = string.Empty;
    public Guid SerialId { get; init; }
    public bool IsSingleData { get; init; }
}

/// <summary>定義「CustomParameterDataSourceRecord」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class CustomParameterDataSourceRecord
{
    public string Description { get; init; } = string.Empty;
    public string? Xml { get; init; }
    public int Category { get; init; }
    public bool Scope { get; init; }
    public long UserId { get; init; }
    public string CreateDateTime { get; init; } = string.Empty;
    public string ModifyDateTime { get; init; } = string.Empty;
    public Guid SerialId { get; init; }
}

/// <summary>定義「CsvFormatRecord」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class CsvFormatRecord
{
    public string FormatType { get; init; } = string.Empty;
    public decimal Version { get; init; }
    public bool Enable { get; init; }
    public bool Latest { get; init; }
    public string ModifyTime { get; init; } = string.Empty;
    public string? ParentFormatType { get; init; }
}

/// <summary>定義「ScheduleRecord」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class ScheduleRecord
{
    public long Id { get; init; }
    public string Description { get; init; } = string.Empty;
    public int Frequency { get; init; }
    public string ExtensionParameter { get; init; } = string.Empty;
    public string EnableDateTime { get; init; } = string.Empty;
    public string? NextStartupDateTime { get; init; }
    public string? LastStartupDateTime { get; init; }
    public string? LastFinishDateTime { get; init; }
    public bool Enabled { get; init; }
    public int Category { get; init; }
    public int Scope { get; init; }
    public int TaskRunMode { get; init; }
    public int ScheduleQueueId { get; init; }
}

/// <summary>定義「ScheduleTaskRecord」資料結構或服務職責，供圖譜抽取流程使用。</summary>
sealed class ScheduleTaskRecord
{
    public long ScheduleId { get; init; }
    public int TaskId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Parameter { get; init; }
    public string? Description { get; init; }
}
