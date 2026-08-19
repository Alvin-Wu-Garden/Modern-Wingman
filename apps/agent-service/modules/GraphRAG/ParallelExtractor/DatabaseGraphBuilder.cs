namespace AgentService.Modules.GraphRAG.ParallelExtractor;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.SqlClient;

/// <summary>依資料庫 metadata 與原始碼線索建立資料庫邊界圖。</summary>
sealed class DatabaseGraphBuilder
{
    private static readonly Regex ExecRegex = new(@"\bEXEC(?:UTE)?\s+(?:(?:\[(?<schema>[A-Za-z_]\w*)\]|(?<schema>[A-Za-z_]\w*))\s*\.\s*)?(?:\[(?<name>[A-Za-z_]\w*)\]|(?<name>[A-Za-z_]\w*))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqlCommandRegex = new("new\\s+SqlCommand\\s*\\(\\s*(?:(?:\\\"(?<literal>[^\\\"]+)\\\")|(?<identifier>[A-Za-z_]\\w*))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqlParameterRegex = new("new\\s+SqlParameter\\s*\\(\\s*\\\"(?<name>@[^\\\"]+)\\\"\\s*,\\s*SqlDbType\\.(?<type>[A-Za-z_]\\w*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConnectionAddRegex = new("<add\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttributeRegex = new("(?<name>[A-Za-z_:][A-Za-z0-9_.:-]*)\\s*=\\s*\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.Compiled);
    private static readonly Regex MappingHeaderRegex = new("來源資料表：(?<name>[A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex MappingTypeRegex = new("來源資料表類型：(?<type>Table|View|UDF)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SourceDatabaseRegex = new("來源資料庫：(?<name>[^\\r\\n]+)", RegexOptions.Compiled);
    private static readonly Regex ViewCallRegex = new("\\bView\\s*\\(\\s*(?:\\\"(?<name>[^\\\"]+)\\\")?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FieldConstantRegex = new("public\\s+const\\s+string\\s+(?<name>[A-Za-z_]\\w*)\\s*=\\s*\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.Compiled);
    private static readonly Regex StoredProcedureArgumentRegex = new("^AUTO=EXECSTOREDPROCEDURE:(?<procedure>[^,\\s]+)(?<arguments>.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _sourceRoot;
    private readonly string _connectionString;
    private readonly string _server;
    private readonly string _source;
    private readonly DatabaseMetadata _database;
    private readonly CodeGraphIndex _codeIndex;
    private readonly RelationGraph _graph;
    private readonly Dictionary<string, string> _databaseObjectIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _templateIds = new();
    private readonly Dictionary<Guid, string> _dataSourceIds = new();
    private readonly Dictionary<Guid, string> _customParameterDataSourceIds = new();
    private readonly Dictionary<string, string> _menuIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _scheduleDefinitionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _storedProcedureParameterIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _categoryTypeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _projectIdsByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>建立資料庫圖建構器。</summary>
    public DatabaseGraphBuilder(string sourceRoot, string connectionString, DatabaseMetadata database, CodeGraphIndex codeIndex)
    {
        _sourceRoot = sourceRoot;
        _connectionString = connectionString;
        var connection = new SqlConnectionStringBuilder(connectionString);
        _server = connection.DataSource;
        _source = connection.InitialCatalog;
        _database = database;
        _codeIndex = codeIndex;
        _graph = new RelationGraph();
        foreach (var project in codeIndex.Projects)
        {
            _projectIdsByName[project.Name] = project.Id;
        }
    }

    /// <summary>依固定階段建立完整資料庫關係圖。</summary>
    public RelationGraph Build()
    {
        BuildDatabaseObjects();
        BuildDatabaseDependencies();
        BuildConnections();
        BuildSchedules();
        BuildReports();
        BuildMenus();
        BuildEfModels();
        BuildCustomMappings();
        ScanSourceCode();
        return _graph;
    }

    private void BuildDatabaseObjects()
    {
        var databaseId = StableId.For("database", _database.DatabaseName);
        _graph.AddNode("Database", databaseId, new Dictionary<string, object?>
        {
            ["name"] = _database.DatabaseName,
            ["server"] = _server,
            ["source"] = _source,
            ["rowDataImported"] = false,
            ["metadataOnly"] = true
        });

        foreach (var item in _database.Objects)
        {
            AddDatabaseObject(item);
        }

        foreach (var column in _database.Columns)
        {
            var objectId = GetDatabaseObjectId(column.SchemaName, column.ObjectName, null);
            if (objectId is null)
            {
                continue;
            }

            var columnId = StableId.For("db-column", _database.DatabaseName, column.SchemaName, column.ObjectName, column.Name);
            _graph.AddNode("DatabaseColumn", columnId, new Dictionary<string, object?>
            {
                ["databaseName"] = _database.DatabaseName,
                ["schemaName"] = column.SchemaName,
                ["objectName"] = column.ObjectName,
                ["name"] = column.Name,
                ["ordinal"] = column.Ordinal,
                ["dataType"] = column.DataType,
                ["maxLength"] = (int)column.MaxLength,
                ["precision"] = (int)column.Precision,
                ["scale"] = (int)column.Scale,
                ["isNullable"] = column.IsNullable,
                ["isIdentity"] = column.IsIdentity,
                ["isComputed"] = column.IsComputed,
                ["isPrimaryKey"] = column.IsPrimaryKey
            });
            _graph.AddRelationship("HAS_COLUMN", objectId, columnId, new Dictionary<string, object?>
            {
                ["ordinal"] = column.Ordinal
            });
        }

        foreach (var parameter in _database.Parameters)
        {
            var objectId = GetDatabaseObjectId(parameter.SchemaName, parameter.ObjectName, null);
            if (objectId is null)
            {
                continue;
            }

            var parameterId = StableId.For("db-parameter", _database.DatabaseName, parameter.SchemaName, parameter.ObjectName, parameter.Name);
            _storedProcedureParameterIds[$"{parameter.SchemaName}.{parameter.ObjectName}.{parameter.Name}"] = parameterId;
            _graph.AddNode("StoredProcedureParameter", parameterId, new Dictionary<string, object?>
            {
                ["databaseName"] = _database.DatabaseName,
                ["schemaName"] = parameter.SchemaName,
                ["objectName"] = parameter.ObjectName,
                ["name"] = parameter.Name,
                ["ordinal"] = parameter.Ordinal,
                ["dataType"] = parameter.DataType,
                ["maxLength"] = (int)parameter.MaxLength,
                ["precision"] = (int)parameter.Precision,
                ["scale"] = (int)parameter.Scale,
                ["isOutput"] = parameter.IsOutput,
                ["isReadonly"] = parameter.IsReadonly
            });
            _graph.AddRelationship("HAS_PARAMETER", objectId, parameterId, new Dictionary<string, object?>
            {
                ["ordinal"] = parameter.Ordinal
            });
        }

        foreach (var foreignKey in _database.ForeignKeys)
        {
            var sourceId = GetDatabaseObjectId(foreignKey.ParentSchema, foreignKey.ParentTable, "Table");
            var targetId = GetDatabaseObjectId(foreignKey.ReferencedSchema, foreignKey.ReferencedTable, "Table");
            if (sourceId is null || targetId is null)
            {
                continue;
            }

            _graph.AddRelationship("FOREIGN_KEY_TO", sourceId, targetId, new Dictionary<string, object?>
            {
                ["constraintName"] = foreignKey.ConstraintName,
                ["sourceColumn"] = foreignKey.ParentColumn,
                ["targetColumn"] = foreignKey.ReferencedColumn,
                ["ordinal"] = foreignKey.Ordinal
            });
        }
    }

    private string AddDatabaseObject(DbObjectInfo item, bool? existsInCurrentDatabase = null)
    {
        var key = $"{item.SchemaName}.{item.Name}";
        if (_databaseObjectIds.TryGetValue(key, out var existingId))
        {
            return existingId;
        }

        var id = StableId.For("db-object", _database.DatabaseName, item.SchemaName, item.ObjectType, item.Name);
        _databaseObjectIds[key] = id;
        _graph.AddNode("DatabaseObject", id, new Dictionary<string, object?>
        {
            ["databaseName"] = _database.DatabaseName,
            ["schemaName"] = item.SchemaName,
            ["name"] = item.Name,
            ["objectType"] = item.ObjectType,
            ["sqlType"] = item.SqlType,
            ["typeDescription"] = item.TypeDescription,
            ["objectId"] = item.ObjectId,
            ["createDate"] = item.CreateDate,
            ["modifyDate"] = item.ModifyDate,
            ["existsInCurrentDatabase"] = existsInCurrentDatabase ?? true,
            ["hasDefinition"] = !string.IsNullOrWhiteSpace(item.Definition),
            ["definitionHash"] = string.IsNullOrWhiteSpace(item.Definition) ? null : StableId.For("definition-hash", item.Definition),
            ["definitionLength"] = item.Definition?.Length ?? 0,
            ["metadataOnly"] = true
        });
        _graph.AddRelationship("CONTAINS_OBJECT", StableId.For("database", _database.DatabaseName), id, new Dictionary<string, object?>
        {
            ["objectType"] = item.ObjectType
        });
        return id;
    }

    private string EnsureStoredProcedure(string rawName)
    {
        var clean = rawName.Trim().Trim(';').Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var schema = parts.Length > 1 ? parts[^2] : "dbo";
        var name = parts[^1];
        var existing = _database.Resolve(schema, name, "StoredProcedure");
        if (existing is not null)
        {
            return GetDatabaseObjectId(existing.SchemaName, existing.Name, existing.ObjectType)!;
        }

        var key = $"{schema}.{name}";
        if (_databaseObjectIds.TryGetValue(key, out var existingId))
        {
            return existingId;
        }

        var id = StableId.For("db-object", _database.DatabaseName, schema, "StoredProcedure", name);
        _databaseObjectIds[key] = id;
        _graph.AddNode("DatabaseObject", id, new Dictionary<string, object?>
        {
            ["databaseName"] = _database.DatabaseName,
            ["schemaName"] = schema,
            ["name"] = name,
            ["objectType"] = "StoredProcedure",
            ["existsInCurrentDatabase"] = false,
            ["status"] = "CODE_ONLY_OR_HISTORICAL",
            ["metadataOnly"] = true
        });
        _graph.AddRelationship("CONTAINS_OBJECT", StableId.For("database", _database.DatabaseName), id, new Dictionary<string, object?>
        {
            ["objectType"] = "StoredProcedure",
            ["existsInCurrentDatabase"] = false
        });
        return id;
    }

    private string? GetDatabaseObjectId(string schemaName, string objectName, string? objectType)
    {
        var item = _database.Resolve(schemaName, objectName, objectType);
        if (item is null)
        {
            return null;
        }

        var key = $"{item.SchemaName}.{item.Name}";
        if (_databaseObjectIds.TryGetValue(key, out var id))
        {
            return id;
        }

        return AddDatabaseObject(item);
    }

    private void BuildDatabaseDependencies()
    {
        foreach (var item in _database.Objects.Where(item => !string.IsNullOrWhiteSpace(item.Definition)))
        {
            var sourceId = GetDatabaseObjectId(item.SchemaName, item.Name, item.ObjectType);
            if (sourceId is null)
            {
                continue;
            }

            foreach (var reference in SqlReferenceExtractor.Extract(item.Definition!, _database))
            {
                if (reference.Target.SchemaName.Equals(item.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                    reference.Target.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetId = GetDatabaseObjectId(reference.Target.SchemaName, reference.Target.Name, reference.Target.ObjectType);
                if (targetId is null)
                {
                    continue;
                }

                var relation = item.ObjectType switch
                {
                    "StoredProcedure" when reference.Target.ObjectType == "StoredProcedure" => "CALLS_STORED_PROCEDURE",
                    "StoredProcedure" when reference.Target.ObjectType == "UDF" => "CALLS_UDF",
                    _ when reference.Access == "WRITE" => "WRITES",
                    _ when reference.Access == "READ" => "READS",
                    _ => "REFERENCES_DATABASE_OBJECT"
                };
                _graph.AddRelationship(relation, sourceId, targetId, new Dictionary<string, object?>
                {
                    ["evidence"] = "sys.sql_modules_definition_token_match",
                    ["access"] = reference.Access,
                    ["confidence"] = reference.Confidence
                });
            }
        }
    }

    private void BuildConnections()
    {
        foreach (var file in EnumerateFiles(_sourceRoot, "*.config"))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            foreach (Match match in ConnectionAddRegex.Matches(text))
            {
                var attributes = AttributeRegex.Matches(match.Value)
                    .ToDictionary(item => item.Groups["name"].Value, item => WebUtility.HtmlDecode(item.Groups["value"].Value), StringComparer.OrdinalIgnoreCase);
                if (!attributes.TryGetValue("connectionString", out var connectionString) ||
                    !TargetsConfiguredDatabase(connectionString))
                {
                    continue;
                }

                attributes.TryGetValue("name", out var connectionName);
                attributes.TryGetValue("providerName", out var providerName);
                var profileId = StableId.For("connection-profile", StableId.NormalizePath(file), connectionName);
                _graph.AddNode("ConnectionProfile", profileId, new Dictionary<string, object?>
                {
                    ["name"] = connectionName ?? string.Empty,
                    ["providerName"] = providerName ?? string.Empty,
                    ["sourceFile"] = StableId.NormalizePath(file),
                    ["dataSource"] = ExtractConnectionPart(connectionString, "Data Source"),
                    ["initialCatalog"] = ExtractConnectionPart(connectionString, "Initial catalog") ?? ExtractConnectionPart(connectionString, "Initial Catalog"),
                    ["entityFramework"] = connectionString.Contains("metadata=", StringComparison.OrdinalIgnoreCase),
                    ["passwordStored"] = false
                });
                _graph.AddRelationship("TARGETS_DATABASE", profileId, StableId.For("database", _database.DatabaseName), new Dictionary<string, object?>
                {
                    ["connectionName"] = connectionName ?? string.Empty
                });

                var projectId = _codeIndex.FindProjectForPath(file);
                if (projectId is not null)
                {
                    _graph.AddRelationship("USES_CONNECTION", projectId, profileId, new Dictionary<string, object?>
                    {
                        ["sourceFile"] = StableId.NormalizePath(file)
                    });
                }
            }
        }
    }

    /// <summary>
    /// 只建立指向使用者目前設定資料庫的 ConnectionProfile；判斷依據來自連線設定，
    /// 不綁定特定客戶的 server 或 database 名稱。
    /// </summary>
    private bool TargetsConfiguredDatabase(string connectionString)
    {
        var dataSource = ExtractConnectionPart(connectionString, "Data Source");
        var initialCatalog = ExtractConnectionPart(connectionString, "Initial catalog")
            ?? ExtractConnectionPart(connectionString, "Initial Catalog");
        return string.Equals(dataSource, _server, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(initialCatalog, _source, StringComparison.OrdinalIgnoreCase) ||
               connectionString.Contains(_server, StringComparison.OrdinalIgnoreCase) ||
               connectionString.Contains(_source, StringComparison.OrdinalIgnoreCase);
    }

    private void BuildSchedules()
    {
        var definitions = new Dictionary<string, ScheduleDefinition>(StringComparer.OrdinalIgnoreCase);
        var definitionDirectory = Path.Combine(_sourceRoot, "RMScheduleService", "TaskDefinition");
        if (Directory.Exists(definitionDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(definitionDirectory, "*.xml", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var document = XDocument.Load(file, LoadOptions.SetLineInfo);
                    var root = document.Root;
                    if (root is null)
                    {
                        continue;
                    }

                    var taskName = AttributeValue(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
                    var taskId = StableId.For("schedule-definition", StableId.NormalizePath(file));
                    definitions[taskName] = new ScheduleDefinition(taskName, file, taskId);
                    _scheduleDefinitionIds[taskName] = taskId;
                    _graph.AddNode("ScheduleTaskDefinition", taskId, new Dictionary<string, object?>
                    {
                        ["name"] = taskName,
                        ["fileName"] = Path.GetFileName(file),
                        ["filePath"] = StableId.NormalizePath(file),
                        ["schemaVersion"] = AttributeValue(root, "SchemaVersion") ?? string.Empty,
                        ["sourceType"] = "RMScheduleService.TaskDefinition"
                    });

                    var scheduleProject = _projectIdsByName.TryGetValue("RMScheduleService", out var scheduleProjectId) ? scheduleProjectId : null;
                    if (scheduleProject is not null)
                    {
                        _graph.AddRelationship("DEFINED_IN_PROJECT", taskId, scheduleProject, new Dictionary<string, object?>
                        {
                            ["sourceFile"] = StableId.NormalizePath(file)
                        });
                    }

                    foreach (var argument in root.Descendants().Where(element => element.Name.LocalName.Equals("Argument", StringComparison.OrdinalIgnoreCase)))
                    {
                        var value = AttributeValue(argument, "Value");
                        if (value is null)
                        {
                            continue;
                        }

                        var match = StoredProcedureArgumentRegex.Match(value);
                        if (!match.Success)
                        {
                            continue;
                        }

                        var procedureName = match.Groups["procedure"].Value;
                        var procedureId = EnsureStoredProcedure(procedureName);
                        var argumentText = match.Groups["arguments"].Value.TrimStart(',');
                        var argumentNames = string.IsNullOrWhiteSpace(argumentText)
                            ? Array.Empty<string>()
                            : argumentText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var procedureParameterCount = _database.Parameters.Count(parameter =>
                            parameter.SchemaName.Equals(GetSchema(procedureName), StringComparison.OrdinalIgnoreCase) &&
                            parameter.ObjectName.Equals(GetObjectName(procedureName), StringComparison.OrdinalIgnoreCase));
                        _graph.AddRelationship("EXECUTES_STORED_PROCEDURE", taskId, procedureId, new Dictionary<string, object?>
                        {
                            ["rawArgument"] = value,
                            ["arguments"] = argumentNames,
                            ["argumentCount"] = argumentNames.Length,
                            ["databaseParameterCount"] = procedureParameterCount,
                            ["parameterCountMismatch"] = argumentNames.Length != procedureParameterCount,
                            ["sourceFile"] = StableId.NormalizePath(file),
                            ["sourceLine"] = (argument as IXmlLineInfo)?.LineNumber ?? 0,
                            ["confidence"] = "CONFIRMED"
                        });
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"排程 XML 已略過：{file}：{exception.Message}");
                }
            }
        }

        var definitionsByName = definitions.ToDictionary(item => item.Key, item => item.Value.TaskId, StringComparer.OrdinalIgnoreCase);
        foreach (var schedule in _database.Schedules)
        {
            var scheduleId = StableId.For("schedule", _database.DatabaseName, schedule.Id);
            _graph.AddNode("Schedule", scheduleId, new Dictionary<string, object?>
            {
                ["databaseName"] = _database.DatabaseName,
                ["scheduleId"] = schedule.Id,
                ["name"] = schedule.Description,
                ["description"] = schedule.Description,
                ["frequency"] = schedule.Frequency,
                ["extensionParameter"] = schedule.ExtensionParameter,
                ["enableDateTime"] = schedule.EnableDateTime,
                ["nextStartupDateTime"] = schedule.NextStartupDateTime,
                ["lastStartupDateTime"] = schedule.LastStartupDateTime,
                ["lastFinishDateTime"] = schedule.LastFinishDateTime,
                ["enabled"] = schedule.Enabled,
                ["category"] = schedule.Category,
                ["scope"] = schedule.Scope,
                ["taskRunMode"] = schedule.TaskRunMode,
                ["scheduleQueueId"] = schedule.ScheduleQueueId
            });

            foreach (var task in _database.ScheduleTasks.Where(item => item.ScheduleId == schedule.Id))
            {
                var taskInstanceId = StableId.For("schedule-task-instance", _database.DatabaseName, task.ScheduleId, task.TaskId);
                _graph.AddNode("ScheduleTaskInstance", taskInstanceId, new Dictionary<string, object?>
                {
                    ["databaseName"] = _database.DatabaseName,
                    ["scheduleId"] = task.ScheduleId,
                    ["taskId"] = task.TaskId,
                    ["name"] = task.Name,
                    ["parameter"] = task.Parameter,
                    ["description"] = task.Description
                });
                _graph.AddRelationship("HAS_SCHEDULE_TASK", scheduleId, taskInstanceId);
                if (definitionsByName.TryGetValue(task.Name, out var definitionId))
                {
                    _graph.AddRelationship("RESOLVES_TO_TASK_DEFINITION", taskInstanceId, definitionId, new Dictionary<string, object?>
                    {
                        ["confidence"] = "EXACT_NAME"
                    });
                }
            }
        }
    }

    /// <summary>建立報表模板、資料來源與資料庫物件之間的關係。</summary>
    private void BuildReports()
    {
        foreach (var template in _database.ReportTemplates)
        {
            var templateId = StableId.For("report-template", _database.DatabaseName, template.TemplateId);
            _templateIds[template.TemplateId] = templateId;
            _graph.AddNode("ReportTemplate", templateId, new Dictionary<string, object?>
            {
                ["databaseName"] = _database.DatabaseName,
                ["templateId"] = template.TemplateId.ToString(),
                ["name"] = template.Name,
                ["description"] = template.Description,
                ["type"] = template.Type,
                ["productType"] = template.ProductType,
                ["tickMode"] = template.TickMode,
                ["isDefault"] = template.IsDefault,
                ["createDateTime"] = template.CreateDateTime,
                ["modifyDateTime"] = template.ModifyDateTime,
                ["scope"] = template.Scope,
                ["isGroup"] = template.IsGroup,
                ["isPackageMode"] = template.IsPackageMode,
                ["isFax"] = template.IsFax,
                ["isSendMail"] = template.IsSendMail,
                ["xmlHash"] = template.Xml is null ? null : StableId.For("xml-hash", template.Xml),
                ["xmlLength"] = template.Xml?.Length ?? 0,
                ["metadataOnly"] = true
            });

            if (template.Xml is null)
            {
                continue;
            }

            var document = ParseXml(template.Xml);
            if (document is null)
            {
                continue;
            }

            var ordinal = 0;
            foreach (var reportDocument in document.Descendants().Where(element => element.Name.LocalName.Equals("ReportDocument", StringComparison.OrdinalIgnoreCase)))
            {
                ordinal++;
                var dataSourceText = AttributeValue(reportDocument, "DataSourceSerialID");
                var documentId = StableId.For("report-document", template.TemplateId, ordinal, dataSourceText ?? string.Empty);
                _graph.AddNode("ReportDocument", documentId, new Dictionary<string, object?>
                {
                    ["templateId"] = template.TemplateId.ToString(),
                    ["ordinal"] = ordinal,
                    ["dataSourceSerialId"] = dataSourceText,
                    ["metadataOnly"] = true
                });
                _graph.AddRelationship("CONTAINS_REPORT_DOCUMENT", templateId, documentId, new Dictionary<string, object?>
                {
                    ["ordinal"] = ordinal
                });

                if (Guid.TryParse(dataSourceText, out var dataSourceId) && _dataSourceIds.TryGetValue(dataSourceId, out var dataSourceNodeId))
                {
                    _graph.AddRelationship("USES_DATA_SOURCE", documentId, dataSourceNodeId, new Dictionary<string, object?>
                    {
                        ["confidence"] = "EXACT_SERIAL_ID",
                        ["sourceAttribute"] = "DataSourceSerialID"
                    });
                }
            }
        }

        foreach (var dataSource in _database.ReportDataSources)
        {
            var dataSourceId = StableId.For("report-data-source", _database.DatabaseName, dataSource.SerialId);
            _dataSourceIds[dataSource.SerialId] = dataSourceId;
            var xml = ParseXml(dataSource.Xml);
            var sqlText = ExtractSqlScript(xml);
            _graph.AddNode("ReportDataSource", dataSourceId, new Dictionary<string, object?>
            {
                ["databaseName"] = _database.DatabaseName,
                ["serialId"] = dataSource.SerialId.ToString(),
                ["name"] = dataSource.Description,
                ["description"] = dataSource.Description,
                ["category"] = dataSource.Category,
                ["scope"] = dataSource.Scope,
                ["userId"] = dataSource.UserId,
                ["createDateTime"] = dataSource.CreateDateTime,
                ["modifyDateTime"] = dataSource.ModifyDateTime,
                ["isSingleData"] = dataSource.IsSingleData,
                ["kind"] = sqlText is null ? "UnknownOrAssembly" : "SQLScriptDataSource",
                ["xmlHash"] = dataSource.Xml is null ? null : StableId.For("xml-hash", dataSource.Xml),
                ["xmlLength"] = dataSource.Xml?.Length ?? 0,
                ["sqlHash"] = sqlText is null ? null : StableId.For("sql-hash", sqlText),
                ["sqlLength"] = sqlText?.Length ?? 0,
                ["metadataOnly"] = true
            });
            BuildXmlDataSourceDetails(dataSourceId, xml, sqlText);
        }

        // ReportDocument 節點會早於 DataSource Catalog 建立；必須等所有
        // ReportDataSource 節點完成後，再解析精確的 SerialID 關聯。
        foreach (var template in _database.ReportTemplates.Where(item => item.Xml is not null))
        {
            var templateId = _templateIds[template.TemplateId];
            var document = ParseXml(template.Xml);
            if (document is null)
            {
                continue;
            }

            var ordinal = 0;
            foreach (var reportDocument in document.Descendants().Where(element => element.Name.LocalName.Equals("ReportDocument", StringComparison.OrdinalIgnoreCase)))
            {
                ordinal++;
                var dataSourceText = AttributeValue(reportDocument, "DataSourceSerialID");
                var documentId = StableId.For("report-document", template.TemplateId, ordinal, dataSourceText ?? string.Empty);
                if (Guid.TryParse(dataSourceText, out var dataSourceId) && _dataSourceIds.TryGetValue(dataSourceId, out var dataSourceNodeId))
                {
                    _graph.AddRelationship("USES_DATA_SOURCE", documentId, dataSourceNodeId, new Dictionary<string, object?>
                    {
                        ["confidence"] = "EXACT_SERIAL_ID",
                        ["sourceAttribute"] = "DataSourceSerialID"
                    });
                }
            }
        }

        foreach (var customDataSource in _database.CustomParameterDataSources)
        {
            var dataSourceId = StableId.For("custom-parameter-data-source", _database.DatabaseName, customDataSource.SerialId);
            _customParameterDataSourceIds[customDataSource.SerialId] = dataSourceId;
            var xml = ParseXml(customDataSource.Xml);
            var sqlText = ExtractSqlScript(xml);
            _graph.AddNode("CustomParameterDataSource", dataSourceId, new Dictionary<string, object?>
            {
                ["databaseName"] = _database.DatabaseName,
                ["serialId"] = customDataSource.SerialId.ToString(),
                ["name"] = customDataSource.Description,
                ["description"] = customDataSource.Description,
                ["category"] = customDataSource.Category,
                ["scope"] = customDataSource.Scope,
                ["userId"] = customDataSource.UserId,
                ["createDateTime"] = customDataSource.CreateDateTime,
                ["modifyDateTime"] = customDataSource.ModifyDateTime,
                ["kind"] = sqlText is null ? "UnknownOrAssembly" : "SQLScriptDataSource",
                ["xmlHash"] = customDataSource.Xml is null ? null : StableId.For("xml-hash", customDataSource.Xml),
                ["xmlLength"] = customDataSource.Xml?.Length ?? 0,
                ["sqlHash"] = sqlText is null ? null : StableId.For("sql-hash", sqlText),
                ["sqlLength"] = sqlText?.Length ?? 0,
                ["metadataOnly"] = true
            });
            BuildXmlDataSourceDetails(dataSourceId, xml, sqlText);
        }

        var categoryConstants = LoadCategoryConstants();
        foreach (var csvFormat in _database.CsvFormats)
        {
            var versionText = csvFormat.Version.ToString(CultureInfo.InvariantCulture);
            var csvId = StableId.For("csv-format", _database.DatabaseName, csvFormat.FormatType, versionText);
            _graph.AddNode("CsvFormat", csvId, new Dictionary<string, object?>
            {
                ["databaseName"] = _database.DatabaseName,
                ["formatType"] = csvFormat.FormatType,
                ["version"] = versionText,
                ["enable"] = csvFormat.Enable,
                ["latest"] = csvFormat.Latest,
                ["modifyTime"] = csvFormat.ModifyTime,
                ["parentFormatType"] = csvFormat.ParentFormatType,
                ["contentImported"] = false,
                ["metadataOnly"] = true
            });
            if (categoryConstants.TryGetValue(csvFormat.FormatType, out var constantName))
            {
                var categoryId = StableId.For("category-type", csvFormat.FormatType);
                _categoryTypeIds[csvFormat.FormatType] = categoryId;
                _graph.AddNode("CategoryType", categoryId, new Dictionary<string, object?>
                {
                    ["name"] = constantName,
                    ["value"] = csvFormat.FormatType,
                    ["sourceFile"] = StableId.NormalizePath(Path.Combine(_sourceRoot, "RMWebDefinition", "EnumCore.cs"))
                });
                _graph.AddRelationship("MAPS_TO_CATEGORY_TYPE", csvId, categoryId, new Dictionary<string, object?>
                {
                    ["match"] = "FormatType equals CategoryType constant value"
                });
            }
        }
    }

    /// <summary>解析報表 XML 與 SQL 內容，建立資料來源細節節點。</summary>
    private void BuildXmlDataSourceDetails(string dataSourceId, XDocument? document, string? sqlText)
    {
        if (document is null)
        {
            return;
        }

        if (sqlText is not null)
        {
            AddSqlReferences(dataSourceId, sqlText, "xml_sql_script");
        }

        var parameterOrdinal = 0;
        foreach (var parameter in document.Descendants().Where(element => element.Name.LocalName.Equals("Parameter", StringComparison.OrdinalIgnoreCase)))
        {
            var fieldType = AttributeValue(parameter, "FieldType");
            var extendValue = AttributeValue(parameter, "ExtendValue");
            if (fieldType is null && extendValue is null)
            {
                continue;
            }

            parameterOrdinal++;
            var parameterId = StableId.For("report-parameter", dataSourceId, parameterOrdinal, fieldType ?? string.Empty, extendValue ?? string.Empty);
            _graph.AddNode("ReportParameter", parameterId, new Dictionary<string, object?>
            {
                ["fieldType"] = fieldType,
                ["extendValue"] = extendValue,
                ["ordinal"] = parameterOrdinal,
                ["isCustomParameterDataSource"] = fieldType?.StartsWith("CustomParameterDataSource_", StringComparison.OrdinalIgnoreCase) == true,
                ["metadataOnly"] = true
            });
            _graph.AddRelationship("HAS_PARAMETER", dataSourceId, parameterId, new Dictionary<string, object?>
            {
                ["ordinal"] = parameterOrdinal
            });

            if (fieldType?.StartsWith("CustomParameterDataSource_", StringComparison.OrdinalIgnoreCase) == true &&
                !string.IsNullOrWhiteSpace(extendValue))
            {
                var match = _database.CustomParameterDataSources.FirstOrDefault(item => item.Description.Equals(extendValue, StringComparison.OrdinalIgnoreCase));
                if (match is not null && _customParameterDataSourceIds.TryGetValue(match.SerialId, out var customSourceId))
                {
                    _graph.AddRelationship("USES_CUSTOM_PARAMETER_SOURCE", parameterId, customSourceId, new Dictionary<string, object?>
                    {
                        ["lookupField"] = "Description",
                        ["lookupValue"] = extendValue,
                        ["confidence"] = "EXACT_DESCRIPTION"
                    });
                }
            }
        }

        var resultColumnOrdinal = 0;
        foreach (var column in document.Descendants().Where(element => element.Name.LocalName.Equals("Column", StringComparison.OrdinalIgnoreCase)))
        {
            var name = AttributeValue(column, "ColumnName");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            resultColumnOrdinal++;
            var columnId = StableId.For("result-column", dataSourceId, resultColumnOrdinal, name);
            _graph.AddNode("ResultColumn", columnId, new Dictionary<string, object?>
            {
                ["name"] = name,
                ["dataType"] = AttributeValue(column, "Type"),
                ["ordinal"] = resultColumnOrdinal,
                ["metadataOnly"] = true
            });
            _graph.AddRelationship("RETURNS_COLUMN", dataSourceId, columnId, new Dictionary<string, object?>
            {
                ["ordinal"] = resultColumnOrdinal
            });
        }
    }

    /// <summary>建立選單、路由、Controller 與前端頁面之間的關係。</summary>
    private void BuildMenus()
    {
        foreach (var menu in _database.Menus)
        {
            var normalizedLink = NormalizeLink(menu.LinkAddress);
            var linkType = ClassifyLink(normalizedLink);
            var menuId = StableId.For("menu-item", _database.DatabaseName, menu.Id);
            _menuIds[menu.Id.ToString(CultureInfo.InvariantCulture)] = menuId;
            _graph.AddNode("MenuItem", menuId, new Dictionary<string, object?>
            {
                ["databaseName"] = _database.DatabaseName,
                ["menuId"] = menu.Id,
                ["parentId"] = menu.ParentId,
                ["name"] = menu.Name,
                ["released"] = menu.Released,
                ["weight"] = menu.Weight,
                ["linkAddress"] = menu.LinkAddress,
                ["normalizedLink"] = normalizedLink,
                ["linkType"] = linkType,
                ["description"] = menu.Description,
                ["iconClass"] = menu.IconClass,
                ["isOpenNewTab"] = menu.IsOpenNewTab,
                ["metadataOnly"] = true
            });
        }

        foreach (var menu in _database.Menus)
        {
            var menuId = _menuIds[menu.Id.ToString(CultureInfo.InvariantCulture)];
            if (menu.ParentId.HasValue && _menuIds.TryGetValue(menu.ParentId.Value.ToString(CultureInfo.InvariantCulture), out var parentId))
            {
                _graph.AddRelationship("CHILD_OF", menuId, parentId, new Dictionary<string, object?>
                {
                    ["parentId"] = menu.ParentId.Value
                });
            }

            var normalizedLink = NormalizeLink(menu.LinkAddress);
            var segments = normalizedLink.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var linkType = ClassifyLink(normalizedLink);
            if (linkType == "CustomReport" && segments.Length >= 3 && Guid.TryParse(segments[2], out var templateId) && _templateIds.TryGetValue(templateId, out var templateNodeId))
            {
                _graph.AddRelationship("LINKS_TO_CUSTOM_REPORT", menuId, templateNodeId, new Dictionary<string, object?>
                {
                    ["templateId"] = templateId.ToString(),
                    ["confidence"] = "EXACT_TEMPLATE_ID"
                });
            }
            else if (linkType == "PluginReport" && segments.Length >= 3)
            {
                AddPluginRoute(menuId, segments[2]);
            }
            else if (linkType == "MVC")
            {
                AddMvcRoute(menuId, segments);
            }
        }
    }

    /// <summary>依 MVC 路由片段解析選單對應的 Controller 與 Action。</summary>
    private void AddMvcRoute(string menuId, string[] segments)
    {
        if (segments.Length == 0)
        {
            return;
        }

        var controllerName = segments[0] + "Controller";
        var actionName = segments.Length > 1 ? segments[1] : "Index";
        var candidates = _codeIndex.TypesByName.TryGetValue(controllerName, out var typeCandidates)
            ? typeCandidates.Where(candidate => _projectIdsByName.TryGetValue("RiskMaster_Web", out var projectId) && candidate.ProjectId == projectId).ToList()
            : new List<TypeIndexEntry>();
        foreach (var type in candidates)
        {
            var menuRouteProperties = new Dictionary<string, object?>
            {
                ["controller"] = controllerName,
                ["action"] = actionName,
                ["routeValues"] = segments.Skip(2).ToArray(),
                ["confidence"] = "MVC_ROUTE_CONVENTION"
            };
            _graph.AddRelationship("NAVIGATES_TO_CONTROLLER", menuId, type.Id, menuRouteProperties);
            var methodKey = (type.Name.ToLowerInvariant(), actionName.ToLowerInvariant());
            if (_codeIndex.RiskMasterWebMethods.TryGetValue(methodKey, out var methods))
            {
                foreach (var method in methods)
                {
                    _graph.AddRelationship("NAVIGATES_TO_ACTION", menuId, method.Id, menuRouteProperties);
                }
            }
        }
    }

    /// <summary>解析外掛報表路由並建立其程式碼對應關係。</summary>
    private void AddPluginRoute(string menuId, string encoded)
    {
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            decoded = string.Empty;
        }

        var separator = decoded.IndexOf('/');
        var assembly = separator >= 0 ? decoded[..separator] : decoded;
        var className = separator >= 0 ? decoded[(separator + 1)..] : string.Empty;
        var targetId = StableId.For("plugin-report-target", assembly, className);
        _graph.AddNode("PluginReportTarget", targetId, new Dictionary<string, object?>
        {
            ["assembly"] = assembly,
            ["classFullName"] = className,
            ["decodedValue"] = decoded,
            ["metadataOnly"] = true
        });
        _graph.AddRelationship("LINKS_TO_PLUGIN_REPORT", menuId, targetId, new Dictionary<string, object?>
        {
            ["confidence"] = string.IsNullOrWhiteSpace(decoded) ? "INVALID_BASE64" : "BASE64_DECODED"
        });

        if (_codeIndex.TypesByFullName.TryGetValue(className, out var type))
        {
            _graph.AddRelationship("IMPLEMENTED_BY_TYPE", targetId, type.Id, new Dictionary<string, object?>
            {
                ["confidence"] = "EXACT_FULL_NAME"
            });
        }
    }

    /// <summary>掃描 Entity Framework metadata，建立模型與資料庫物件的映射。</summary>
    private void BuildEfModels()
    {
        foreach (var file in EnumerateFiles(_sourceRoot, "*.edmx"))
        {
            XDocument? document;
            try
            {
                document = XDocument.Load(file);
            }
            catch
            {
                continue;
            }

            var modelId = StableId.For("ef-model", StableId.NormalizePath(file));
            _graph.AddNode("EfModel", modelId, new Dictionary<string, object?>
            {
                ["name"] = Path.GetFileNameWithoutExtension(file),
                ["filePath"] = StableId.NormalizePath(file),
                ["modelKind"] = "Entity Framework Database-First",
                ["metadataOnly"] = true
            });
            var projectId = _codeIndex.FindProjectForPath(file);
            if (projectId is not null)
            {
                _graph.AddRelationship("CONTAINS_EF_MODEL", projectId, modelId);
            }

            var mappingNames = document.Descendants().Where(element => element.Name.LocalName.Equals("EntityTypeMapping", StringComparison.OrdinalIgnoreCase))
                .SelectMany(element => element.Descendants().Where(child => child.Name.LocalName.Equals("MappingFragment", StringComparison.OrdinalIgnoreCase)).Select(fragment => new
                {
                    Store = AttributeValue(fragment, "StoreEntitySet"),
                    Conceptual = AttributeValue(element, "TypeName")
                }))
                .Where(item => !string.IsNullOrWhiteSpace(item.Store))
                .GroupBy(item => item.Store!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Conceptual ?? group.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var entitySet in document.Descendants().Where(element => element.Name.LocalName.Equals("EntitySet", StringComparison.OrdinalIgnoreCase)))
            {
                var storeType = entitySet.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals("Type", StringComparison.OrdinalIgnoreCase) && (attribute.Value.Equals("Tables", StringComparison.OrdinalIgnoreCase) || attribute.Value.Equals("Views", StringComparison.OrdinalIgnoreCase)))?.Value;
                if (storeType is null)
                {
                    continue;
                }

                var storeName = AttributeValue(entitySet, "Table") ?? AttributeValue(entitySet, "Name");
                var schemaName = AttributeValue(entitySet, "Schema") ?? "dbo";
                if (string.IsNullOrWhiteSpace(storeName))
                {
                    continue;
                }

                var objectType = storeType.Equals("Views", StringComparison.OrdinalIgnoreCase) ? "View" : "Table";
                var conceptualName = mappingNames.TryGetValue(storeName, out var mappedName) ? LastTypeNamePart(mappedName) : storeName;
                var entityId = StableId.For("ef-entity", StableId.NormalizePath(file), conceptualName, schemaName, storeName);
                _graph.AddNode("EfEntity", entityId, new Dictionary<string, object?>
                {
                    ["name"] = conceptualName,
                    ["storeEntitySet"] = storeName,
                    ["schemaName"] = schemaName,
                    ["objectType"] = objectType,
                    ["modelFile"] = StableId.NormalizePath(file),
                    ["metadataOnly"] = true
                });
                _graph.AddRelationship("DESCRIBES_ENTITY", modelId, entityId);

                var objectId = GetDatabaseObjectId(schemaName, storeName, objectType);
                if (objectId is not null)
                {
                    _graph.AddRelationship("EF_MAPS_TO", entityId, objectId, new Dictionary<string, object?>
                    {
                        ["confidence"] = "EDMX_SSDL_MSL_EXACT_NAME"
                    });
                }

                var type = FindTypeInProject(conceptualName, projectId);
                if (type is not null)
                {
                    _graph.AddRelationship("GENERATES_ENTITY_TYPE", entityId, type.Id, new Dictionary<string, object?>
                    {
                        ["confidence"] = "DESIGNER_CLASS_NAME"
                    });
                }
            }
        }
    }

    /// <summary>掃描既有 Mapping 類別註解，建立程式類別與資料庫物件關係。</summary>
    private void BuildCustomMappings()
    {
        var directories = new[] { "RMDAL", "RMDAL_Galelio", "RMDBDefinition", "RMDBDefinition_Galelio", "RMQR", "RMQR_Galelio" }
            .Select(name => Path.Combine(_sourceRoot, name))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            foreach (var file in EnumerateFiles(directory, "*.cs"))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch
                {
                    continue;
                }

                var tableMatch = MappingHeaderRegex.Match(text);
                var typeMatch = MappingTypeRegex.Match(text);
                if (!tableMatch.Success || !typeMatch.Success)
                {
                    continue;
                }

                var sourceDatabase = SourceDatabaseRegex.Match(text).Groups["name"].Value.Trim();
                var objectName = tableMatch.Groups["name"].Value;
                var objectType = typeMatch.Groups["type"].Value switch
                {
                    "View" => "View",
                    "UDF" => "UDF",
                    _ => "Table"
                };
                var objectId = GetDatabaseObjectId("dbo", objectName, objectType) ?? EnsureDatabaseObjectCandidate("dbo", objectName, objectType);
                var filePath = StableId.NormalizePath(file);
                if (_codeIndex.FileIdsByPath.TryGetValue(filePath, out var fileId) && _codeIndex.TypesByFileId.TryGetValue(fileId, out var types))
                {
                    foreach (var type in types)
                    {
                        _graph.AddRelationship("CUSTOM_MAPS_TO", type.Id, objectId, new Dictionary<string, object?>
                        {
                            ["generatorProject"] = Path.GetFileName(directory),
                            ["sourceDatabase"] = sourceDatabase,
                            ["sourceObjectType"] = objectType,
                            ["currentDatabaseExists"] = _database.Resolve("dbo", objectName, objectType) is not null,
                            ["evidence"] = "CodeGenerator header"
                        });
                    }
                }
            }
        }
    }

    /// <summary>建立程式碼宣告但資料庫 metadata 尚未找到的候選物件節點。</summary>
    private string EnsureDatabaseObjectCandidate(string schemaName, string objectName, string objectType)
    {
        var key = $"{schemaName}.{objectName}";
        if (_databaseObjectIds.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var id = StableId.For("db-object", _database.DatabaseName, schemaName, objectType, objectName);
        _databaseObjectIds[key] = id;
        _graph.AddNode("DatabaseObject", id, new Dictionary<string, object?>
        {
            ["databaseName"] = _database.DatabaseName,
            ["schemaName"] = schemaName,
            ["name"] = objectName,
            ["objectType"] = objectType,
            ["existsInCurrentDatabase"] = false,
            ["status"] = "CODE_MAPPING_SOURCE_OBJECT_NOT_FOUND",
            ["metadataOnly"] = true
        });
        _graph.AddRelationship("CONTAINS_OBJECT", StableId.For("database", _database.DatabaseName), id, new Dictionary<string, object?>
        {
            ["objectType"] = objectType,
            ["existsInCurrentDatabase"] = false
        });
        return id;
    }

    private void ScanSourceCode()
    {
        foreach (var file in EnumerateFiles(_sourceRoot, "*.cs"))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, path: file);
            var root = tree.GetRoot();
            var constants = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword))
                .SelectMany(field => field.Declaration.Variables.Select(variable => new
                {
                    Name = variable.Identifier.ValueText,
                    Value = (variable.Initializer?.Value as LiteralExpressionSyntax)?.Token.ValueText
                }))
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Value!).First(), StringComparer.OrdinalIgnoreCase);

            foreach (var methodNode in root.DescendantNodes().Where(node => node is MethodDeclarationSyntax or ConstructorDeclarationSyntax))
            {
                var methodText = methodNode.GetText().ToString();
                var calls = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var sqlLiteral in ExtractStringLiterals(methodNode))
                {
                    foreach (Match match in ExecRegex.Matches(sqlLiteral))
                    {
                        var schema = match.Groups["schema"].Success ? match.Groups["schema"].Value : "dbo";
                        var name = match.Groups["name"].Value;
                        if (schema.Equals("sys", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // 未限定 Schema 的 EXEC 名稱只有在外觀符合 Stored Procedure
                        // 命名時才保留，供歷史或缺失物件使用；避免 SQL／Log 中的一般文字
                        // 被誤判為呼叫關係。
                        var knownProcedure = _database.Resolve(schema, name, "StoredProcedure");
                        if (knownProcedure is null && !LooksLikeStoredProcedure(name))
                        {
                            continue;
                        }

                        AddCall(calls, $"{schema}.{name}");
                    }
                }

                if (methodText.Contains("CommandType.StoredProcedure", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match match in SqlCommandRegex.Matches(methodText))
                    {
                        var name = match.Groups["literal"].Success
                            ? match.Groups["literal"].Value
                            : constants.TryGetValue(match.Groups["identifier"].Value, out var constantValue) ? constantValue : null;
                        if (!string.IsNullOrWhiteSpace(name) && !name.Contains(' '))
                        {
                            AddCall(calls, name!);
                        }
                    }
                }

                if (calls.Count == 0 && methodText.Contains("Arguments.StoredProcedureName", StringComparison.OrdinalIgnoreCase))
                {
                    AddDynamicCall(file, methodNode);
                }

                if (calls.Count == 0)
                {
                    continue;
                }

                var filePath = StableId.NormalizePath(file);
                _codeIndex.FileIdsByPath.TryGetValue(filePath, out var fileId);
                var startLine = methodNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var methodId = fileId is not null && _codeIndex.MethodIdsByLocation.TryGetValue((fileId, startLine), out var resolvedMethodId)
                    ? resolvedMethodId
                    : fileId;
                if (methodId is null)
                {
                    continue;
                }

                foreach (var call in calls)
                {
                    var procedureId = EnsureStoredProcedure(call.Key);
                    var parameterNames = SqlParameterRegex.Matches(methodText).Select(match => match.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    _graph.AddRelationship(methodId == fileId ? "FILE_CALLS_STORED_PROCEDURE" : "CALLS_STORED_PROCEDURE", methodId, procedureId, new Dictionary<string, object?>
                    {
                        ["sourceFile"] = filePath,
                        ["sourceLine"] = startLine,
                        ["callApi"] = methodText.Contains("CommandType.StoredProcedure", StringComparison.OrdinalIgnoreCase) ? "SqlCommand.CommandType.StoredProcedure" : "EXEC SQL text",
                        ["parameterNames"] = parameterNames,
                        ["confidence"] = "ROSYN_SYNTAX_PLUS_LITERAL_MATCH"
                    });

                    AddParameterBindings(methodId, procedureId, parameterNames, filePath, startLine);
                }

                AddViewRelations(methodNode, methodId, filePath);
            }
        }
    }

    private void AddParameterBindings(string methodId, string procedureId, string[] parameterNames, string sourceFile, int sourceLine)
    {
        var procedureName = _graph.Nodes.FirstOrDefault(node => node.Id == procedureId)?.Properties.TryGetValue("name", out var name) == true ? name?.ToString() : null;
        var schemaName = _graph.Nodes.FirstOrDefault(node => node.Id == procedureId)?.Properties.TryGetValue("schemaName", out var schema) == true ? schema?.ToString() : "dbo";
        if (procedureName is null)
        {
            return;
        }

        foreach (var parameterName in parameterNames)
        {
            var key = $"{schemaName}.{procedureName}.{parameterName}";
            if (_storedProcedureParameterIds.TryGetValue(key, out var parameterId))
            {
                _graph.AddRelationship("BINDS_PARAMETER", methodId, parameterId, new Dictionary<string, object?>
                {
                    ["sourceFile"] = sourceFile,
                    ["sourceLine"] = sourceLine,
                    ["confidence"] = "PARAMETER_NAME_EXACT"
                });
            }
        }
    }

    private void AddDynamicCall(string file, SyntaxNode methodNode)
    {
        var filePath = StableId.NormalizePath(file);
        _codeIndex.FileIdsByPath.TryGetValue(filePath, out var fileId);
        var startLine = methodNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var methodId = fileId is not null && _codeIndex.MethodIdsByLocation.TryGetValue((fileId, startLine), out var resolvedMethodId)
            ? resolvedMethodId
            : fileId;
        if (methodId is null)
        {
            return;
        }

        var dynamicId = StableId.For("dynamic-sp-call", "Arguments.StoredProcedureName");
        _graph.AddNode("DynamicStoredProcedureCall", dynamicId, new Dictionary<string, object?>
        {
            ["name"] = "Arguments.StoredProcedureName",
            ["resolution"] = "runtime value required",
            ["sourceType"] = "RMBZ.Arguments"
        });
        _graph.AddRelationship("DYNAMICALLY_CALLS_STORED_PROCEDURE", methodId, dynamicId, new Dictionary<string, object?>
        {
            ["sourceFile"] = filePath,
            ["sourceLine"] = startLine,
            ["confidence"] = "DYNAMIC_RUNTIME_NAME"
        });
    }

    private void AddViewRelations(SyntaxNode methodNode, string methodId, string sourceFile)
    {
        var className = methodNode.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(className) || !className.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var controllerName = className[..^"Controller".Length];
        var methodName = methodNode switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            _ => string.Empty
        };
        foreach (Match match in ViewCallRegex.Matches(methodNode.GetText().ToString()))
        {
            var viewName = match.Groups["name"].Success && !string.IsNullOrWhiteSpace(match.Groups["name"].Value)
                ? match.Groups["name"].Value
                : methodName;
            var relative = viewName.Contains('/')
                ? viewName.TrimStart('~', '/').Replace('/', Path.DirectorySeparatorChar)
                : Path.Combine("Views", controllerName, viewName);
            var candidates = new[]
            {
                Path.Combine(_sourceRoot, "RiskMaster_Web", relative + ".cshtml"),
                Path.Combine(_sourceRoot, "RiskMaster_Web", relative + ".aspx"),
                Path.Combine(_sourceRoot, "RiskMaster_Web", relative)
            };
            var viewPath = candidates.FirstOrDefault(File.Exists);
            if (viewPath is null)
            {
                continue;
            }

            var viewId = StableId.For("view", StableId.NormalizePath(viewPath));
            _graph.AddNode("View", viewId, new Dictionary<string, object?>
            {
                ["name"] = Path.GetFileNameWithoutExtension(viewPath),
                ["path"] = StableId.NormalizePath(viewPath),
                ["relativePath"] = Path.GetRelativePath(_sourceRoot, viewPath),
                ["metadataOnly"] = true
            });
            _graph.AddRelationship("RENDERS_VIEW", methodId, viewId, new Dictionary<string, object?>
            {
                ["sourceFile"] = sourceFile,
                ["confidence"] = "VIEW_CALL_AND_FILE_EXISTENCE"
            });
        }
    }

    private void AddSqlReferences(string sourceId, string sqlText, string evidence)
    {
        foreach (var reference in SqlReferenceExtractor.Extract(sqlText, _database))
        {
            var targetId = GetDatabaseObjectId(reference.Target.SchemaName, reference.Target.Name, reference.Target.ObjectType);
            if (targetId is null)
            {
                continue;
            }

            var relation = reference.Target.ObjectType switch
            {
                "StoredProcedure" => "REFERENCES_STORED_PROCEDURE",
                "UDF" => "REFERENCES_UDF",
                _ => "REFERENCES_DATABASE_OBJECT"
            };
            _graph.AddRelationship(relation, sourceId, targetId, new Dictionary<string, object?>
            {
                ["evidence"] = evidence,
                ["access"] = reference.Access,
                ["confidence"] = reference.Confidence
            });
        }
    }

    private Dictionary<string, string> LoadCategoryConstants()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(_sourceRoot, "RMWebDefinition", "EnumCore.cs");
        if (!File.Exists(file))
        {
            return result;
        }

        foreach (Match match in FieldConstantRegex.Matches(File.ReadAllText(file)))
        {
            result.TryAdd(match.Groups["value"].Value, match.Groups["name"].Value);
        }

        return result;
    }

    private TypeIndexEntry? FindTypeInProject(string name, string? projectId)
    {
        if (!_codeIndex.TypesByName.TryGetValue(name, out var candidates))
        {
            return null;
        }

        return candidates.FirstOrDefault(candidate => projectId is null || candidate.ProjectId == projectId);
    }

    private static string NormalizeLink(string value)
    {
        var normalized = value.Trim().Replace('\\', '/');
        normalized = Regex.Replace(normalized, "(?<!:)/{2,}", "/");
        normalized = normalized.TrimEnd('/');
        return normalized.StartsWith("/", StringComparison.Ordinal) || normalized.StartsWith("->", StringComparison.Ordinal)
            ? normalized
            : "/" + normalized;
    }

    private static string ClassifyLink(string link)
    {
        if (link.StartsWith("->", StringComparison.Ordinal)) return "Redirect";
        if (link.StartsWith("/PluginReport/MenuIndex/", StringComparison.OrdinalIgnoreCase)) return "PluginReport";
        if (link.StartsWith("/CustomReport/MenuIndex/", StringComparison.OrdinalIgnoreCase)) return "CustomReport";
        if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "AbsoluteUrl";
        if (link.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return "JavaScript";
        return "MVC";
    }

    private static string? ExtractConnectionPart(string connectionString, string key)
    {
        var match = Regex.Match(connectionString, $"(?i)(?:^|;)\\s*{Regex.Escape(key)}\\s*=\\s*([^;]*)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static XDocument? ParseXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch { return null; }
    }

    private static string? ExtractSqlScript(XDocument? document)
        => document?.Descendants().Where(element => element.Name.LocalName.Equals("SQLScript", StringComparison.OrdinalIgnoreCase)).Select(element => element.Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? AttributeValue(XElement element, string name)
        => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? GetSchema(string name)
    {
        var parts = name.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal).Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 1 ? parts[^2] : "dbo";
    }

    private static string GetObjectName(string name)
        => name.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal).Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];

    private static string LastTypeNamePart(string value)
    {
        var clean = value.Replace("IsTypeOf(", string.Empty, StringComparison.Ordinal).Trim(')', ' ');
        return clean.Split('.').LastOrDefault() ?? clean;
    }

    private static IEnumerable<string> ExtractStringLiterals(SyntaxNode node)
    {
        foreach (var literal in node.DescendantNodes().OfType<LiteralExpressionSyntax>()
                     .Where(expression => expression.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            yield return literal.Token.ValueText;
        }

        foreach (var interpolated in node.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
        {
            yield return interpolated.GetText().ToString();
        }
    }

    private static bool LooksLikeStoredProcedure(string name)
        => name.StartsWith("apex_sp_", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("fbl_sp_", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("get", StringComparison.OrdinalIgnoreCase);

    private static void AddCall(Dictionary<string, HashSet<string>> calls, string name)
    {
        var parts = name.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal).Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = parts.Length > 1 ? $"{parts[^2]}.{parts[^1]}" : $"dbo.{parts[^1]}";
        calls.TryAdd(normalized, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var file in files)
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/packages/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return file;
        }
    }

    private sealed record ScheduleDefinition(string Name, string FilePath, string TaskId);
}

sealed record SqlReference(DbObjectInfo Target, string Access, string Confidence);

static class SqlReferenceExtractor
{
    private static readonly Regex QualifiedToken = new(@"(?<schema>\[?[A-Za-z_]\w*\]?)\s*\.\s*(?<name>\[?[A-Za-z_]\w*\]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"\[?[A-Za-z_]\w*\]?", RegexOptions.Compiled);

    public static IEnumerable<SqlReference> Extract(string sql, DatabaseMetadata database)
    {
        var clean = Regex.Replace(sql, @"/\*[\s\S]*?\*/|--[^\r\n]*", " ");
        var covered = new List<(int Start, int Length)>();
        foreach (Match match in QualifiedToken.Matches(clean))
        {
            var schema = match.Groups["schema"].Value.Trim('[', ']');
            var name = match.Groups["name"].Value.Trim('[', ']');
            var target = database.Resolve(schema, name);
            if (target is null) continue;
            covered.Add((match.Index, match.Length));
            yield return new SqlReference(target, AccessAround(clean, match.Index, target), "SCHEMA_QUALIFIED_NAME");
        }

        foreach (Match match in Identifier.Matches(clean))
        {
            if (covered.Any(span => match.Index >= span.Start && match.Index < span.Start + span.Length)) continue;
            var name = match.Value.Trim('[', ']');
            var target = database.Resolve(null, name);
            if (target is null) continue;
            yield return new SqlReference(target, AccessAround(clean, match.Index, target), "UNQUALIFIED_EXACT_NAME");
        }
    }

    private static string AccessAround(string sql, int index, DbObjectInfo target)
    {
        var prefix = sql[Math.Max(0, index - 180)..index];
        if (target.ObjectType == "StoredProcedure" && Regex.IsMatch(prefix, @"(?i)\bEXEC(?:UTE)?\s*$")) return "CALL";
        if (Regex.IsMatch(prefix, @"(?i)\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE\s+TABLE)\s*$")) return "WRITE";
        if (Regex.IsMatch(prefix, @"(?i)\b(?:FROM|JOIN|APPLY)\s*$")) return "READ";
        return "REFERENCE";
    }
}
