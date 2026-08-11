namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 將 SQL Server 通用系統目錄快照加入同一份權威 GraphDocument。
/// 此 Resolver 只接受已由使用者設定連線載入的 metadata，不持有也不解析連線字串。
/// </summary>
public sealed class DatabaseMetadataGraphResolver
{
    /// <summary>
    /// 建立 Database、DatabaseObject、DatabaseColumn、StoredProcedureParameter 與直接 catalog 關係。
    /// 找不到端點的 metadata 會安全略過，不以字串猜測不存在的資料庫物件。
    /// </summary>
    public ExtractionResult Resolve(
        ExtractionResult input,
        DatabaseMetadataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog.DatabaseName);

        var builder = GraphDocumentBuilder.FromDocument(
            input.Document,
            input.Document.Metadata.BuildStage);
        var databaseKey = CreateDatabaseKey(catalog.Provider, catalog.DatabaseName);
        builder.AddNode(
            GraphNodeKind.Database,
            databaseKey,
            new Dictionary<string, object?>
            {
                ["provider"] = catalog.Provider,
                ["database"] = catalog.DatabaseName,
                ["name"] = catalog.DatabaseName,
                ["metadata_only"] = true,
            });

        var objects = catalog.Objects
            .GroupBy(
                item => CreateObjectLookupKey(item.SchemaName, item.ObjectName),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var databaseObject in objects.Values
                     .OrderBy(item => item.SchemaName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase))
        {
            var objectKey = databaseObject.CreateNodeKey();
            builder.AddNode(
                GraphNodeKind.DatabaseObject,
                objectKey,
                new Dictionary<string, object?>
                {
                    ["provider"] = catalog.Provider,
                    ["database"] = catalog.DatabaseName,
                    ["schema"] = databaseObject.SchemaName,
                    ["name"] = databaseObject.ObjectName,
                    ["object_kind"] = databaseObject.Kind.ToString(),
                });
            builder.AddRelationship(
                GraphRelationshipKind.ContainsDatabaseObject,
                databaseKey,
                objectKey,
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.DatabaseRow,
                    DatabaseObject = "sys.objects",
                    RowKey = $"{databaseObject.SchemaName}.{databaseObject.ObjectName}",
                });
        }

        var columnKeys = AddColumns(builder, catalog, objects);
        AddParameters(builder, catalog, objects);
        AddForeignKeys(builder, catalog, columnKeys);
        AddDependencies(builder, catalog, objects);
        return new ExtractionResult(builder.Build(), input.Issues);
    }

    /// <summary>加入 table/view 欄位並回傳可供 FK 精準連接的欄位索引。</summary>
    private static IReadOnlyDictionary<string, string> AddColumns(
        GraphDocumentBuilder builder,
        DatabaseMetadataCatalog catalog,
        IReadOnlyDictionary<string, DatabaseObjectCatalogItem> objects)
    {
        var columnKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in catalog.Columns
                     .OrderBy(item => item.SchemaName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Ordinal))
        {
            var objectLookupKey = CreateObjectLookupKey(column.SchemaName, column.ObjectName);
            if (!objects.TryGetValue(objectLookupKey, out var databaseObject))
            {
                continue;
            }

            var columnKey = CreateColumnKey(
                catalog.Provider,
                catalog.DatabaseName,
                column.SchemaName,
                column.ObjectName,
                column.Ordinal,
                column.Name);
            columnKeys[CreateColumnLookupKey(
                column.SchemaName,
                column.ObjectName,
                column.Name)] = columnKey;
            builder.AddNode(
                GraphNodeKind.DatabaseColumn,
                columnKey,
                new Dictionary<string, object?>
                {
                    ["provider"] = catalog.Provider,
                    ["database"] = catalog.DatabaseName,
                    ["schema"] = column.SchemaName,
                    ["object_name"] = column.ObjectName,
                    ["name"] = column.Name,
                    ["ordinal"] = column.Ordinal,
                    ["data_type"] = column.DataType,
                    ["max_length"] = column.MaxLength,
                    ["precision"] = column.Precision,
                    ["scale"] = column.Scale,
                    ["is_nullable"] = column.IsNullable,
                    ["is_identity"] = column.IsIdentity,
                    ["is_computed"] = column.IsComputed,
                    ["is_primary_key"] = column.IsPrimaryKey,
                });
            builder.AddRelationship(
                GraphRelationshipKind.HasColumn,
                databaseObject.CreateNodeKey(),
                columnKey,
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.DatabaseRow,
                    DatabaseObject = "sys.columns",
                    DatabaseColumn = column.Name,
                    RowKey = $"{column.SchemaName}.{column.ObjectName}:{column.Ordinal}",
                });
        }

        return columnKeys;
    }

    /// <summary>加入 Stored Procedure／Function 參數。</summary>
    private static void AddParameters(
        GraphDocumentBuilder builder,
        DatabaseMetadataCatalog catalog,
        IReadOnlyDictionary<string, DatabaseObjectCatalogItem> objects)
    {
        foreach (var parameter in catalog.Parameters
                     .OrderBy(item => item.SchemaName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ObjectName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Ordinal))
        {
            var objectLookupKey = CreateObjectLookupKey(parameter.SchemaName, parameter.ObjectName);
            if (!objects.TryGetValue(objectLookupKey, out var databaseObject))
            {
                continue;
            }

            var parameterKey = CreateParameterKey(
                catalog.Provider,
                catalog.DatabaseName,
                parameter.SchemaName,
                parameter.ObjectName,
                parameter.Ordinal,
                parameter.Name);
            builder.AddNode(
                GraphNodeKind.StoredProcedureParameter,
                parameterKey,
                new Dictionary<string, object?>
                {
                    ["provider"] = catalog.Provider,
                    ["database"] = catalog.DatabaseName,
                    ["schema"] = parameter.SchemaName,
                    ["object_name"] = parameter.ObjectName,
                    ["name"] = parameter.Name,
                    ["ordinal"] = parameter.Ordinal,
                    ["data_type"] = parameter.DataType,
                    ["max_length"] = parameter.MaxLength,
                    ["precision"] = parameter.Precision,
                    ["scale"] = parameter.Scale,
                    ["is_output"] = parameter.IsOutput,
                    ["is_read_only"] = parameter.IsReadOnly,
                });
            builder.AddRelationship(
                GraphRelationshipKind.HasParameter,
                databaseObject.CreateNodeKey(),
                parameterKey,
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.DatabaseRow,
                    DatabaseObject = "sys.parameters",
                    DatabaseColumn = parameter.Name,
                    RowKey = $"{parameter.SchemaName}.{parameter.ObjectName}:{parameter.Ordinal}",
                });
        }
    }

    /// <summary>只在來源與目標欄位都存在時建立外鍵關係。</summary>
    private static void AddForeignKeys(
        GraphDocumentBuilder builder,
        DatabaseMetadataCatalog catalog,
        IReadOnlyDictionary<string, string> columnKeys)
    {
        foreach (var foreignKey in catalog.ForeignKeys
                     .OrderBy(item => item.SourceSchemaName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SourceObjectName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ConstraintName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Ordinal))
        {
            if (!columnKeys.TryGetValue(
                    CreateColumnLookupKey(
                        foreignKey.SourceSchemaName,
                        foreignKey.SourceObjectName,
                        foreignKey.SourceColumnName),
                    out var sourceColumnKey)
                || !columnKeys.TryGetValue(
                    CreateColumnLookupKey(
                        foreignKey.TargetSchemaName,
                        foreignKey.TargetObjectName,
                        foreignKey.TargetColumnName),
                    out var targetColumnKey))
            {
                continue;
            }

            builder.AddRelationship(
                GraphRelationshipKind.ForeignKeyTo,
                sourceColumnKey,
                targetColumnKey,
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.DatabaseRow,
                    DatabaseObject = "sys.foreign_key_columns",
                    RowKey = $"{foreignKey.ConstraintName}:{foreignKey.Ordinal}",
                },
                new Dictionary<string, object?>
                {
                    ["constraint_name"] = foreignKey.ConstraintName,
                    ["ordinal"] = foreignKey.Ordinal,
                    ["provider"] = catalog.Provider,
                    ["database"] = catalog.DatabaseName,
                });
        }
    }

    /// <summary>將 sys.sql_expression_dependencies 的同資料庫相依轉為既有 DEPENDS_ON。</summary>
    private static void AddDependencies(
        GraphDocumentBuilder builder,
        DatabaseMetadataCatalog catalog,
        IReadOnlyDictionary<string, DatabaseObjectCatalogItem> objects)
    {
        foreach (var dependency in catalog.Dependencies
                     .OrderBy(item => item.SourceSchemaName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SourceObjectName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.TargetSchemaName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.TargetObjectName, StringComparer.OrdinalIgnoreCase))
        {
            if (!objects.TryGetValue(
                    CreateObjectLookupKey(
                        dependency.SourceSchemaName,
                        dependency.SourceObjectName),
                    out var sourceObject)
                || !objects.TryGetValue(
                    CreateObjectLookupKey(
                        dependency.TargetSchemaName,
                        dependency.TargetObjectName),
                    out var targetObject))
            {
                continue;
            }

            builder.AddRelationship(
                GraphRelationshipKind.DependsOn,
                sourceObject.CreateNodeKey(),
                targetObject.CreateNodeKey(),
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.SqlDefinition,
                    DatabaseObject = "sys.sql_expression_dependencies",
                    RowKey = $"{dependency.SourceSchemaName}.{dependency.SourceObjectName}" +
                             $"->{dependency.TargetSchemaName}.{dependency.TargetObjectName}",
                },
                new Dictionary<string, object?>
                {
                    ["provider"] = catalog.Provider,
                    ["database"] = catalog.DatabaseName,
                });
        }
    }

    /// <summary>Database key 只使用 provider 與 user-selected database，不保存 server。</summary>
    private static string CreateDatabaseKey(string provider, string databaseName) =>
        $"database:{provider.Trim().ToLowerInvariant()}:{databaseName.Trim()}";

    /// <summary>欄位 key 包含 provider、database、schema、object、ordinal 與欄位名稱。</summary>
    private static string CreateColumnKey(
        string provider,
        string databaseName,
        string schemaName,
        string objectName,
        int ordinal,
        string columnName) =>
        $"database-column:{provider.Trim().ToLowerInvariant()}:{databaseName.Trim()}:" +
        $"{schemaName.Trim()}:{objectName.Trim()}:{ordinal}:{columnName.Trim()}";

    /// <summary>參數 key 包含 provider、database、schema、object、ordinal 與參數名稱。</summary>
    private static string CreateParameterKey(
        string provider,
        string databaseName,
        string schemaName,
        string objectName,
        int ordinal,
        string parameterName) =>
        $"stored-procedure-parameter:{provider.Trim().ToLowerInvariant()}:{databaseName.Trim()}:" +
        $"{schemaName.Trim()}:{objectName.Trim()}:{ordinal}:{parameterName.Trim()}";

    private static string CreateObjectLookupKey(string schemaName, string objectName) =>
        $"{schemaName}\u001f{objectName}";

    private static string CreateColumnLookupKey(
        string schemaName,
        string objectName,
        string columnName) =>
        $"{schemaName}\u001f{objectName}\u001f{columnName}";
}
