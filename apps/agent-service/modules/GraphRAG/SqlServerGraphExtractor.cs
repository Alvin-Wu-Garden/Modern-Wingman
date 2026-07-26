using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// live SQL Server 抽取所需的執行期來源資訊。
/// ConnectionString 只在記憶體中用來開啟唯讀連線，禁止序列化、寫入 log、evidence 或 canonical snapshot。
/// </summary>
/// <param name="ConnectionString">由 Secret Manager、環境變數或 UI secret store 提供的連線字串。</param>
/// <param name="DatabaseName">寫入 stable ID 的 logical database name，不可包含 server 或帳號。</param>
/// <param name="CommandTimeoutSeconds">metadata 查詢逾時秒數。</param>
public sealed record SqlServerGraphSource(
    [property: JsonIgnore] string ConnectionString,
    string DatabaseName,
    int CommandTimeoutSeconds = 30);

/// <summary>
/// 使用 Microsoft ScriptDom 抽取靜態 T-SQL，並以參數化唯讀查詢抽取 FBL 動態業務設定。
/// SQL AST 負責可確定的 READS／WRITES；live DB 只 materialize 能連回 Menu、報表、排程、CSV、Enum
/// 或 SQL module dependency 的物件，不把整個資料庫 schema 無差別灌入圖譜。
/// </summary>
public sealed partial class SqlServerGraphExtractor(ILogger<SqlServerGraphExtractor> logger) : IGraphExtractor
{
    private const int MaximumEvidenceItems = 40;
    private static readonly IReadOnlySet<string> IgnoredBusinessTables = new HashSet<string>(
    [
        "tblCustomProductTypeDetail",
        "tblBatchReportFTP",
        "tblCustomDesignRiskReportTemplateDetail",
        "tblCustomDesignReportDataSourceUseSetting",
        "tblScheduleConfig",
        "tblMQSchedule",
        "tblMQScheduleTask",
    ], StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string Id => "sqlserver-scriptdom-v3";

    /// <inheritdoc />
    public string Version => "3.2.0";

    /// <inheritdoc />
    public async Task<GraphFragment> ExtractAsync(
        string projectRoot,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"SQL 專案根目錄不存在：{root}");

        var fragment = new GraphFragment();
        foreach (var file in files
                     .Where(path => string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase))
                     .Select(Path.GetFullPath)
                     .Where(path => IsInsideRoot(root, path))
                     .Where(path => !IgnoredPath(root, path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GraphIdentity.NormalizePath(Path.GetRelativePath(root, file));
            var sql = await File.ReadAllTextAsync(file, cancellationToken);
            ExtractSqlFile(fragment, relativePath, sql);
        }
        var csharpFiles = files
            .Where(path => string.Equals(
                Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Where(path => IsInsideRoot(root, path))
            .Where(path => !IgnoredPath(root, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var parsedSources = new ConcurrentBag<CSharpSourceFile>();
        await Parallel.ForEachAsync(
            csharpFiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            },
            async (file, token) =>
            {
                var source = await File.ReadAllTextAsync(file, token);
                // 下列 token 涵蓋此 extractor 所有可能產生 C#→Data 關係的入口。
                // 沒有任何 token 的檔案即使建立 AST 也不會產生 node/edge，因此可安全略過；
                // 這不是抽樣，命中檔仍完整解析，避免對近萬個純業務型別重複 parse 兩次。
                if (!MayContainCSharpDataAccess(source)) return;
                var relativePath = GraphIdentity.NormalizePath(
                    Path.GetRelativePath(root, file));
                var tree = CSharpSyntaxTree.ParseText(
                    source,
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
                    relativePath,
                    cancellationToken: token);
                parsedSources.Add(new CSharpSourceFile(
                    relativePath,
                    source,
                    (CompilationUnitSyntax)tree.GetRoot(token)));
            });
        var csharpSources = parsedSources
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
        var dataTableConstants = CollectDataTableConstants(
            csharpSources, fragment, cancellationToken);
        var extractedCSharp = new ConcurrentBag<(string Path, GraphFragment Fragment)>();
        await Parallel.ForEachAsync(
            csharpSources,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            },
            (file, token) =>
            {
                var local = new GraphFragment();
                ExtractCSharpDataAccess(
                    local,
                    file.RelativePath,
                    file.Source,
                    file.Root,
                    dataTableConstants,
                    token);
                extractedCSharp.Add((file.RelativePath, local));
                return ValueTask.CompletedTask;
            });
        foreach (var item in extractedCSharp.OrderBy(
                     item => item.Path, StringComparer.Ordinal))
            AppendFragment(fragment, item.Fragment);
        foreach (var file in files
                     .Where(path => string.Equals(
                         Path.GetExtension(path), ".java", StringComparison.OrdinalIgnoreCase))
                     .Select(Path.GetFullPath)
                     .Where(path => IsInsideRoot(root, path))
                     .Where(path => !IgnoredPath(root, path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GraphIdentity.NormalizePath(
                Path.GetRelativePath(root, file));
            ExtractJavaDataAccess(
                fragment,
                relativePath,
                await File.ReadAllTextAsync(file, cancellationToken));
        }
        logger.LogInformation(
            "Static SQL GraphRAG V3 抽取完成：{NodeCount} 個節點、{EdgeCount} 條關係。",
            fragment.Nodes.Count, fragment.Edges.Count);
        return fragment;
    }

    /// <summary>
    /// 從 live SQL Server 抽取必要的 module dependency 與 FBL 業務設定。
    /// 所有命令都是固定 SQL 文字、唯讀 SELECT、無使用者值串接；連線字串不會出現在 exception 訊息或 diagnostic。
    /// </summary>
    /// <param name="source">記憶體中的安全連線來源。</param>
    /// <param name="cancellationToken">取消 DB 探索工作的 token。</param>
    /// <returns>可與原始碼 fragment 合併的 DB 圖譜片段。</returns>
    public async Task<GraphFragment> ExtractDatabaseAsync(
        SqlServerGraphSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.DatabaseName);
        if (source.CommandTimeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(
                nameof(source), "SQL command timeout 必須介於 1 到 300 秒。");

        var fragment = new GraphFragment();
        try
        {
            await using var connection = new SqlConnection(source.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;",
                source.CommandTimeoutSeconds,
                cancellationToken);

            await ExtractSqlModuleDependenciesAsync(
                connection, source, fragment, cancellationToken);
            var menus = await ExtractMenusAsync(
                connection, source, fragment, cancellationToken);
            await ExtractApprovalAsync(
                connection, source, fragment, menus, cancellationToken);
            await ExtractCsvAndProductTypesAsync(
                connection, source, fragment, cancellationToken);
            var reports = await ExtractCustomReportsAsync(
                connection, source, fragment, menus, cancellationToken);
            await ExtractCustomEnumsAsync(
                connection, source, fragment, cancellationToken);
            var batchReports = await ExtractBatchReportsAsync(
                connection, source, fragment, reports, cancellationToken);
            await ExtractSchedulesAsync(
                connection, source, fragment, batchReports, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            // 這裡刻意不把 exception.Message 放進 diagnostic，SqlClient 例外有機會含 server 或 login。
            // UI 只需要知道 live DB 抽取失敗且可重試，完整敏感細節留在受控本機診斷管道。
            logger.LogWarning(
                "Live SQL Server GraphRAG 抽取失敗；已遮蔽連線與登入細節。ExceptionType={ExceptionType}",
                exception.GetType().Name);
            fragment.Diagnostics.Add(new GraphDiagnostic(
                "LIVE_DB_UNAVAILABLE",
                GraphDiagnosticSeverity.Warning,
                $"db:{NormalizeDatabase(source.DatabaseName)}",
                "無法完成即時資料庫圖譜抽取；已保留原始碼圖譜，修復連線後可安全重試。",
                true));
            fragment.CapabilityGaps.Add("live-sql-server");
        }

        logger.LogInformation(
            "Live SQL Server GraphRAG V3 抽取完成：{NodeCount} 個節點、{EdgeCount} 條關係。",
            fragment.Nodes.Count, fragment.Edges.Count);
        return fragment;
    }

    /// <summary>
    /// 計算 live DB 的非敏感內容指紋，供 no-op 判斷。
    /// 指紋只讀取 business row 的 stable key／修改時間與 SQL module modify_date，
    /// 不讀取 Email、ParameterValue、FTP、交易資料或 connection string。
    /// </summary>
    /// <param name="source">記憶體中的安全連線來源。</param>
    /// <param name="cancellationToken">取消指紋查詢的 token。</param>
    /// <returns>成功時為 SHA-256；連線暫時不可用時為 null，呼叫端必須禁止 no-op。</returns>
    public async Task<string?> ComputeDatabaseFingerprintAsync(
        SqlServerGraphSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            await using var connection = new SqlConnection(source.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            const string query = """
                SELECT fingerprint_key
                FROM (
                    SELECT CONCAT('module|', s.name, '|', o.name, '|', o.type, '|',
                                  CONVERT(varchar(33), o.modify_date, 126))
                           COLLATE DATABASE_DEFAULT AS fingerprint_key
                    FROM sys.objects AS o
                    INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                    WHERE o.type IN ('P', 'V')

                    UNION ALL
                    SELECT CONCAT('menu|', ID, '|', COALESCE(Parent, -1), '|', Released, '|',
                                  COALESCE(LinkAddress, N''), '|',
                                  CONVERT(varchar(33), ModifyDateTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblMenuMap

                    UNION ALL
                    SELECT CONCAT('approval|', ConfirmSourceType, '|', MaintainMenuID, '|', MenuID, '|',
                                  CONVERT(varchar(33), ModiTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblAsyncConfirmSourceTypeMapping

                    UNION ALL
                    SELECT CONCAT('csv|', FormatType, '|', Version, '|', Enable, '|', Lastest, '|',
                                  CONVERT(varchar(33), ModiTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblCSVFormat

                    UNION ALL
                    SELECT CONCAT('product-csv|', ID, '|', ProductTypeID, '|', CustomTypeID, '|',
                                  FormatType, '|', Required)
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblProductTypeMappingCsvFormatType

                    UNION ALL
                    SELECT CONCAT('report-template|', TemplateID, '|',
                                  CONVERT(varchar(33), ModifyDateTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblCustomDesignRiskReportTemplate

                    UNION ALL
                    SELECT CONCAT('report-source|', SerialID, '|',
                                  CONVERT(varchar(33), ModifyDateTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblCustomDesignReportDataSource

                    UNION ALL
                    SELECT CONCAT('enum|', EnumName, '|', UID, '|',
                                  CONVERT(varchar(33), ModiTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblCustomEnum

                    UNION ALL
                    SELECT CONCAT('enum-item|', EnumName, '|', UID, '|',
                                  CONVERT(varchar(33), ModiTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblCustomEnumItem

                    UNION ALL
                    SELECT CONCAT('schedule|', ID, '|', Enabled, '|',
                                  CONVERT(varchar(33), ModifyDateTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblSchedule

                    UNION ALL
                    SELECT CONCAT('schedule-task|', ScheduleID, '|', TaskID, '|', Name, '|',
                                  CONVERT(varchar(33), ModifyDateTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblScheduleTask

                    UNION ALL
                    SELECT CONCAT('batch|', BatchReportID, '|',
                                  CONVERT(varchar(33), ModifyDateTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblBatchReport

                    UNION ALL
                    SELECT CONCAT('batch-detail|', BatchReportID, '|', ReportID, '|',
                                  ReportType, '|', COALESCE(ReportSourceKey, N''), '|',
                                  CONVERT(varchar(33), ModifyTime, 126))
                           COLLATE DATABASE_DEFAULT
                    FROM dbo.tblBatchReportDetail
                ) AS fingerprints
                -- FBL 歷史資料表可能同時使用 Latin1 與 Chinese_Taiwan collation；
                -- fingerprint 內容保持原值，只在排序比較時統一成目前 database collation，
                -- 避免 collation conflict 讓安全 no-op 永久退化成 full rebuild。
                ORDER BY fingerprint_key COLLATE DATABASE_DEFAULT;
                """;
            var rows = await QueryAsync(
                connection, query, source.CommandTimeoutSeconds, cancellationToken);
            var payload = string.Join(
                '\n', rows.Select(row => row.RequiredString("fingerprint_key")));
            return GraphIdentity.Sha256(payload);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            logger.LogWarning(
                "Live SQL Server fingerprint 失敗；本次禁止 no-op。ExceptionType={ExceptionType}",
                exception.GetType().Name);
            return null;
        }
    }

    private static void ExtractSqlFile(GraphFragment fragment, string relativePath, string sql)
    {
        AddDynamicSqlDiagnostic(fragment, relativePath, sql, null);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var parsed = parser.Parse(reader, out var errors);
        foreach (var error in errors.Take(100))
        {
            fragment.Diagnostics.Add(new GraphDiagnostic(
                "SQL_PARSE_PARTIAL",
                GraphDiagnosticSeverity.Warning,
                relativePath,
                $"T-SQL 第 {error.Line} 行無法完整解析，該區段不會被當成精確圖譜事實。",
                false));
        }

        var owner = ResolveSqlOwner(parsed, relativePath);
        fragment.Nodes.Add(owner.Node);
        AddParsedDependencies(
            fragment,
            owner.Node,
            parsed,
            relativePath,
            lineOffset: 0,
            GraphEvidenceSource.Ast,
            GraphConfidence.Exact);
    }

    /// <summary>
    /// 將已解析的 ScriptDom table references 掛到指定 Code／Data owner。
    /// 這個共用入口讓獨立 .sql module 與 C#／Java 內嵌 SQL 使用完全相同的 READS／WRITES 判定，
    /// 避免 regex 把 UPDATE target 誤判為 read。
    /// </summary>
    private static void AddParsedDependencies(
        GraphFragment fragment,
        GraphNode owner,
        TSqlFragment parsed,
        string relativePath,
        int lineOffset,
        GraphEvidenceSource source,
        GraphConfidence confidence)
    {
        var visitor = new SqlDependencyVisitor();
        parsed.Accept(visitor);
        foreach (var table in visitor.AllTables
                     .Where(table => !table.Name.StartsWith("#", StringComparison.Ordinal))
                     .Distinct()
                     .OrderBy(table => table.Schema, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase))
        {
            var dataId = GraphIdentity.SqlData("project-db", table.Schema, "table", table.Name);
            var line = table.Line <= 0 ? (int?)null : table.Line + lineOffset;
            var evidence = new GraphEvidence(
                source,
                confidence,
                relativePath,
                "由 Microsoft ScriptDom 的 NamedTableReference 解析 SQL 物件。",
                line,
                line,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["schema"] = table.Schema,
                    ["object"] = table.Name,
                });
            fragment.Nodes.Add(DataNode(
                dataId, GraphRoles.Table, table.Name, "sql", "scriptdom",
                [table.Name, $"{table.Schema}.{table.Name}"], evidence,
                new Dictionary<string, string>
                {
                    ["schema"] = table.Schema,
                    ["objectType"] = "table",
                }));
            var kind = visitor.WriteTables.Contains(table)
                ? GraphEdgeKind.Writes
                : GraphEdgeKind.Reads;
            fragment.Edges.Add(CreateEdge(
                owner.Id,
                kind,
                dataId,
                evidence with
                {
                    Reason = kind == GraphEdgeKind.Writes
                        ? "由 ScriptDom 的 INSERT／UPDATE／DELETE／MERGE target 判定此 SQL 會修改資料物件。"
                        : "由 ScriptDom 的 table reference 判定此 SQL 會讀取資料物件。",
                }));
        }
    }

    /// <summary>
    /// 從 C# AST 抽取 Table attribute、EF ToTable 與常見 ADO.NET／Dapper／EF SQL literal。
    /// Code 仍維持 type-level；欄位與 SQL method 只進 evidence，不建立舊式 Method／Column node。
    /// </summary>
    private static void ExtractCSharpDataAccess(
        GraphFragment fragment,
        string relativePath,
        string source,
        CompilationUnitSyntax root,
        IReadOnlyDictionary<string, string> dataTableConstants,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var declaration in root.DescendantNodes()
                     .OfType<BaseTypeDeclarationSyntax>())
        {
            foreach (var attribute in declaration.AttributeLists
                         .SelectMany(list => list.Attributes)
                         .Where(attribute => AttributeName(attribute) == "Table"))
            {
                var table = LiteralArgument(attribute.ArgumentList?.Arguments.FirstOrDefault());
                if (string.IsNullOrWhiteSpace(table)) continue;
                var schema = attribute.ArgumentList?.Arguments
                    .FirstOrDefault(argument =>
                        string.Equals(
                            argument.NameEquals?.Name.Identifier.ValueText,
                            "Schema",
                            StringComparison.Ordinal))
                    is { } schemaArgument
                    ? LiteralArgument(schemaArgument)
                    : null;
                AddOrmMapping(
                    fragment,
                    CreateCSharpOwner(declaration, relativePath),
                    schema ?? "dbo",
                    table,
                    relativePath,
                    LineAt(source, attribute.SpanStart),
                    "由 C# Table attribute 解析 ORM type 與資料表映射。",
                    GraphConfidence.Resolved);
            }
        }

        foreach (Match match in CSharpToTableRegex().Matches(source))
        {
            var typeName = match.Groups["type"].Value;
            var declaration = root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .FirstOrDefault(type =>
                    string.Equals(
                        type.Identifier.ValueText, typeName, StringComparison.Ordinal));
            var owner = declaration is null
                ? CreateCSharpPlaceholder(
                    CSharpNamespace(root), typeName, relativePath,
                    LineAt(source, match.Index))
                : CreateCSharpOwner(declaration, relativePath);
            AddOrmMapping(
                fragment,
                owner,
                match.Groups["schema"].Success
                    ? match.Groups["schema"].Value
                    : "dbo",
                match.Groups["table"].Value,
                relativePath,
                LineAt(source, match.Index),
                "由 EF fluent ToTable 解析 ORM type 與資料表映射。",
                declaration is null
                    ? GraphConfidence.Heuristic
                    : GraphConfidence.Resolved);
        }

        foreach (var literal in root.DescendantNodes()
                     .OfType<LiteralExpressionSyntax>()
                     .Where(node => node.IsKind(
                         Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)))
        {
            var argument = literal.Parent as ArgumentSyntax;
            var argumentList = argument?.Parent as ArgumentListSyntax;
            if (argumentList is null) continue;
            var operation = argumentList.Parent;
            var operationName = operation switch
            {
                InvocationExpressionSyntax invocation =>
                    InvocationName(invocation.Expression),
                ObjectCreationExpressionSyntax creation =>
                    creation.Type.ToString(),
                _ => string.Empty,
            };
            if (!CSharpSqlOperation(operationName)) continue;
            var value = literal.Token.ValueText.Trim();
            if (value.Length == 0) continue;
            var declaration = literal.Ancestors()
                .OfType<BaseTypeDeclarationSyntax>()
                .FirstOrDefault();
            if (declaration is null) continue;
            var owner = CreateCSharpOwner(declaration, relativePath);
            var line = LineAt(source, literal.SpanStart);
            if (LooksLikeSql(value))
            {
                AddEmbeddedSql(
                    fragment, owner, relativePath, value, line,
                    GraphEvidenceSource.Ast, GraphConfidence.Resolved);
            }
            else if (LooksLikeSqlObjectIdentifier(value) &&
                     LooksLikeProcedureOperation(operationName))
            {
                AddProcedureDependency(
                    fragment, owner, relativePath, value, line,
                    GraphEvidenceSource.Ast, GraphConfidence.Heuristic);
            }
        }

        ExtractDataDefinitionSqlDependencies(
            fragment,
            root,
            relativePath,
            source,
            dataTableConstants);
    }

    /// <summary>
    /// FBL 的 code generator 會把 table name 放在 DD*.DataTableName const，
    /// 再以字串串接生成 SELECT／INSERT／UPDATE／DELETE。這不是任意命名猜測：
    /// table identity 直接來自 const literal，操作方向直接來自同一個 expression 的 SQL keyword。
    /// </summary>
    private static void ExtractDataDefinitionSqlDependencies(
        GraphFragment fragment,
        CompilationUnitSyntax root,
        string relativePath,
        string source,
        IReadOnlyDictionary<string, string> dataTableConstants)
    {
        var expressions = root.DescendantNodes()
            .Select(node => node switch
            {
                VariableDeclaratorSyntax
                {
                    Initializer: { Value: { } value },
                } => value,
                AssignmentExpressionSyntax assignment => assignment.Right,
                ArgumentSyntax argument => argument.Expression,
                ArrowExpressionClauseSyntax arrow => arrow.Expression,
                ReturnStatementSyntax
                {
                    Expression: { } value,
                } => value,
                _ => null,
            })
            .Where(expression => expression is not null)
            .Select(expression => expression!)
            .DistinctBy(expression => (expression.SpanStart, expression.Span.Length))
            .OrderBy(expression => expression.SpanStart);

        foreach (var expression in expressions)
        {
            var references = expression.DescendantNodesAndSelf()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(member => member.Name.Identifier.ValueText.Equals(
                    "DataTableName", StringComparison.Ordinal))
                .Select(member => new
                {
                    DefinitionType = member.Expression.ToString()
                        .Split('.')
                        .Last(),
                    Member = member,
                })
                .Where(item => dataTableConstants.ContainsKey(item.DefinitionType))
                .ToList();
            if (references.Count == 0) continue;

            var sqlKeywords = string.Join(
                ' ',
                expression.DescendantTokens()
                    .Where(token => token.IsKind(
                        Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralToken))
                    .Select(token => token.ValueText));
            var write = Regex.IsMatch(
                sqlKeywords,
                @"\b(?:INSERT|UPDATE|DELETE|MERGE)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var read = Regex.IsMatch(
                sqlKeywords,
                @"\b(?:SELECT|FROM|JOIN)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!write && !read) continue;

            var tables = references
                .Select(item => (
                    item.DefinitionType,
                    Table: dataTableConstants[item.DefinitionType],
                    item.Member))
                .DistinctBy(item => item.Table, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // 一個動態 expression 若同時引用多張表且含寫入語句，單靠字串片段無法可靠判斷
            // 哪張是 target；寧可不產生 edge，也不把 read table 誤標成 write。
            if (write && tables.Count != 1)
            {
                fragment.Diagnostics.Add(new GraphDiagnostic(
                    "DYNAMIC_SQL_MULTIPLE_TABLES",
                    GraphDiagnosticSeverity.Warning,
                    relativePath,
                    "同一動態 SQL expression 同時引用多個 DataTableName，無法可靠判定寫入目標；已略過未證實關係。",
                    false));
                continue;
            }

            var declaration = expression.Ancestors()
                .OfType<BaseTypeDeclarationSyntax>()
                .FirstOrDefault();
            if (declaration is null) continue;
            var owner = CreateCSharpOwner(declaration, relativePath);
            fragment.Nodes.Add(owner);
            foreach (var table in tables)
            {
                var kind = write ? GraphEdgeKind.Writes : GraphEdgeKind.Reads;
                var line = LineAt(source, table.Member.SpanStart);
                var evidence = new GraphEvidence(
                    GraphEvidenceSource.Ast,
                    GraphConfidence.Resolved,
                    relativePath,
                    write
                        ? "由 DD*.DataTableName 常數與同一字串 expression 的 INSERT／UPDATE／DELETE／MERGE 關鍵字確認寫入資料表。"
                        : "由 DD*.DataTableName 常數與同一字串 expression 的 SELECT／FROM／JOIN 關鍵字確認讀取資料表。",
                    line,
                    line,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["definitionType"] = table.DefinitionType,
                        ["table"] = table.Table,
                        ["operation"] = kind.ToString().ToUpperInvariant(),
                    });
                var dataId = GraphIdentity.SqlData(
                    "project-db", "dbo", "table", table.Table);
                fragment.Nodes.Add(DataNode(
                    dataId,
                    GraphRoles.Table,
                    table.Table,
                    "sql",
                    "db-definition-constant",
                    [table.Table, $"dbo.{table.Table}"],
                    evidence,
                    new Dictionary<string, string>
                    {
                        ["schema"] = "dbo",
                        ["objectType"] = "table",
                    }));
                fragment.Edges.Add(CreateEdge(owner.Id, kind, dataId, evidence));
            }
        }
    }

    private static IReadOnlyDictionary<string, string> CollectDataTableConstants(
        IReadOnlyList<CSharpSourceFile> files,
        GraphFragment fragment,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var field in file.Root.DescendantNodes()
                         .OfType<FieldDeclarationSyntax>()
                         .Where(field => field.Modifiers.Any(
                             Microsoft.CodeAnalysis.CSharp.SyntaxKind.ConstKeyword)))
            {
                var declarationType = field.Ancestors()
                    .OfType<BaseTypeDeclarationSyntax>()
                    .FirstOrDefault();
                if (declarationType is null) continue;
                foreach (var variable in field.Declaration.Variables)
                {
                    if (!variable.Identifier.ValueText.Equals(
                            "DataTableName", StringComparison.Ordinal) ||
                        variable.Initializer?.Value is not LiteralExpressionSyntax literal ||
                        !literal.IsKind(
                            Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression) ||
                        string.IsNullOrWhiteSpace(literal.Token.ValueText))
                        continue;
                    if (!candidates.TryGetValue(
                            declarationType.Identifier.ValueText, out var values))
                    {
                        values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        candidates.Add(declarationType.Identifier.ValueText, values);
                    }
                    values.Add(literal.Token.ValueText.Trim());
                }
            }
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (definitionType, values) in candidates)
        {
            if (values.Count == 1)
            {
                result[definitionType] = values.Single();
                continue;
            }
            fragment.Diagnostics.Add(new GraphDiagnostic(
                "AMBIGUOUS_DATA_TABLE_CONSTANT",
                GraphDiagnosticSeverity.Warning,
                "project:csharp",
                $"資料定義型別 {definitionType} 出現互相衝突的 DataTableName 常數；已略過未證實映射。",
                false));
        }
        return result;
    }

    private static bool MayContainCSharpDataAccess(string source) =>
        source.Contains("Table", StringComparison.Ordinal) ||
        source.Contains("ToTable", StringComparison.Ordinal) ||
        source.Contains("DataTableName", StringComparison.Ordinal) ||
        source.Contains("SqlCommand", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("FromSql", StringComparison.Ordinal) ||
        source.Contains("ExecuteSql", StringComparison.Ordinal) ||
        source.Contains("Query", StringComparison.Ordinal) ||
        source.Contains("Execute", StringComparison.Ordinal) ||
        source.Contains("StoredProcedure", StringComparison.OrdinalIgnoreCase);

    private static void AppendFragment(GraphFragment target, GraphFragment source)
    {
        target.Nodes.AddRange(source.Nodes);
        target.Edges.AddRange(source.Edges);
        target.Diagnostics.AddRange(source.Diagnostics);
        target.CapabilityGaps.AddRange(source.CapabilityGaps);
    }

    /// <summary>
    /// 從 Java annotation 與 JdbcTemplate 類 SQL literal 抽取 type→Data 關係。
    /// Java 無 compiler binding 時明確使用 Heuristic confidence，不把文字匹配冒充 Exact。
    /// </summary>
    private static void ExtractJavaDataAccess(
        GraphFragment fragment,
        string relativePath,
        string source)
    {
        var packageName = JavaPackageRegex().Match(source).Groups["package"].Value;
        foreach (Match match in JavaTableRegex().Matches(source))
        {
            var owner = CreateJavaPlaceholder(
                packageName,
                match.Groups["type"].Value,
                relativePath,
                LineAt(source, match.Index));
            AddOrmMapping(
                fragment,
                owner,
                match.Groups["schema"].Success
                    ? match.Groups["schema"].Value
                    : "dbo",
                match.Groups["table"].Value,
                relativePath,
                LineAt(source, match.Index),
                "由 Java JPA Table annotation 解析 type 與資料表映射。",
                GraphConfidence.Heuristic);
        }

        var typeMatches = JavaTypeRegex().Matches(source).Cast<Match>().ToList();
        foreach (Match match in JavaSqlLiteralRegex().Matches(source))
        {
            var sql = Regex.Unescape(match.Groups["sql"].Value);
            if (!LooksLikeSql(sql)) continue;
            var type = typeMatches.LastOrDefault(item => item.Index < match.Index);
            if (type is null) continue;
            var owner = CreateJavaPlaceholder(
                packageName,
                type.Groups["type"].Value,
                relativePath,
                LineAt(source, type.Index));
            AddEmbeddedSql(
                fragment,
                owner,
                relativePath,
                sql,
                LineAt(source, match.Index),
                GraphEvidenceSource.Heuristic,
                GraphConfidence.Heuristic);
        }
    }

    private static void AddEmbeddedSql(
        GraphFragment fragment,
        GraphNode owner,
        string relativePath,
        string sql,
        int line,
        GraphEvidenceSource source,
        GraphConfidence confidence)
    {
        fragment.Nodes.Add(owner);
        AddDynamicSqlDiagnostic(fragment, relativePath, sql, owner.Id);
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var parsed = parser.Parse(reader, out var errors);
        foreach (var error in errors.Take(20))
            fragment.Diagnostics.Add(new GraphDiagnostic(
                "SQL_PARSE_PARTIAL",
                GraphDiagnosticSeverity.Warning,
                relativePath,
                $"內嵌 T-SQL 第 {line + Math.Max(0, error.Line - 1)} 行無法完整解析；未產生無法證明的資料關係。",
                false,
                owner.Id));
        AddParsedDependencies(
            fragment,
            owner,
            parsed,
            relativePath,
            line - 1,
            source,
            confidence);
    }

    private static void AddDynamicSqlDiagnostic(
        GraphFragment fragment,
        string relativePath,
        string sql,
        string? ownerId)
    {
        if (!Regex.IsMatch(
                sql,
                @"\bsp_executesql\b|\bEXEC(?:UTE)?\s*\(|\b(?:FROM|JOIN|UPDATE|INTO)\s*(?:\+|@)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return;
        fragment.Diagnostics.Add(new GraphDiagnostic(
            "DYNAMIC_SQL_IDENTIFIER",
            GraphDiagnosticSeverity.Warning,
            relativePath,
            "偵測到由變數或字串組合的動態 SQL identifier；只保留可由 ScriptDom 證明的依賴，不推測實際資料表。",
            false,
            ownerId));
        fragment.CapabilityGaps.Add(
            "動態 SQL identifier 無法在不執行來源系統的前提下可靠解析。");
    }

    private static void AddProcedureDependency(
        GraphFragment fragment,
        GraphNode owner,
        string relativePath,
        string identifier,
        int line,
        GraphEvidenceSource source,
        GraphConfidence confidence)
    {
        var (schema, name) = SplitSqlIdentifier(identifier);
        var evidence = new GraphEvidence(
            source,
            confidence,
            relativePath,
            "由資料存取 API 的 procedure literal 建立 Code→Procedure 依賴；未執行 Stored Procedure。",
            line,
            line,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["procedure"] = $"{schema}.{name}",
            });
        var dataId = GraphIdentity.SqlData(
            "project-db", schema, "procedure", name);
        fragment.Nodes.Add(owner);
        fragment.Nodes.Add(DataNode(
            dataId,
            GraphRoles.Procedure,
            name,
            "sql",
            "source-literal",
            [name, $"{schema}.{name}"],
            evidence,
            new Dictionary<string, string>
            {
                ["schema"] = schema,
                ["objectType"] = "procedure",
            }));
        fragment.Edges.Add(CreateEdge(
            owner.Id,
            GraphEdgeKind.DependsOn,
            dataId,
            evidence));
    }

    private static void AddOrmMapping(
        GraphFragment fragment,
        GraphNode owner,
        string schema,
        string table,
        string relativePath,
        int line,
        string reason,
        GraphConfidence confidence)
    {
        schema = string.IsNullOrWhiteSpace(schema) ? "dbo" : schema;
        var evidence = new GraphEvidence(
            GraphEvidenceSource.Framework,
            confidence,
            relativePath,
            reason,
            line,
            line,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["schema"] = schema,
                ["table"] = table,
            });
        var dataId = GraphIdentity.SqlData(
            "project-db", schema, "table", table);
        fragment.Nodes.Add(owner);
        fragment.Nodes.Add(DataNode(
            dataId,
            GraphRoles.Table,
            table,
            "sql",
            "orm",
            [table, $"{schema}.{table}"],
            evidence,
            new Dictionary<string, string>
            {
                ["schema"] = schema,
                ["objectType"] = "table",
            }));
        fragment.Edges.Add(CreateEdge(
            owner.Id,
            GraphEdgeKind.MapsTo,
            dataId,
            evidence));
    }

    private static GraphNode CreateCSharpOwner(
        BaseTypeDeclarationSyntax declaration,
        string relativePath)
    {
        var nestedTypes = declaration.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .Select(type => type.Identifier.ValueText)
            .Append(declaration.Identifier.ValueText);
        var namespaceName = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Name.ToString())
            .FirstOrDefault() ?? string.Empty;
        var qualified = string.Join(
            '.',
            string.IsNullOrWhiteSpace(namespaceName)
                ? nestedTypes
                : [namespaceName, .. nestedTypes]);
        var lineSpan = declaration.GetLocation().GetLineSpan();
        return CreateCodePlaceholder(
            GraphIdentity.CSharpCode(qualified),
            declaration.Identifier.ValueText,
            qualified,
            "csharp",
            relativePath,
            lineSpan.StartLinePosition.Line + 1,
            GraphConfidence.Resolved);
    }

    private static GraphNode CreateCSharpPlaceholder(
        string namespaceName,
        string typeName,
        string relativePath,
        int line)
    {
        var qualified = string.IsNullOrWhiteSpace(namespaceName)
            ? typeName
            : $"{namespaceName}.{typeName}";
        return CreateCodePlaceholder(
            GraphIdentity.CSharpCode(qualified),
            typeName,
            qualified,
            "csharp",
            relativePath,
            line,
            GraphConfidence.Heuristic);
    }

    private static GraphNode CreateJavaPlaceholder(
        string packageName,
        string typeName,
        string relativePath,
        int line)
    {
        var qualified = string.IsNullOrWhiteSpace(packageName)
            ? typeName
            : $"{packageName}.{typeName}";
        return CreateCodePlaceholder(
            GraphIdentity.JavaCode(qualified),
            typeName,
            qualified,
            "java",
            relativePath,
            line,
            GraphConfidence.Heuristic);
    }

    private static GraphNode CreateCodePlaceholder(
        string id,
        string name,
        string qualifiedName,
        string language,
        string relativePath,
        int line,
        GraphConfidence confidence)
    {
        var evidence = new GraphEvidence(
            GraphEvidenceSource.Framework,
            confidence,
            relativePath,
            "由 ORM／資料存取語法保留 type-level Code owner，將與語言 extractor 的同 ID 節點合併。",
            line,
            line);
        return new GraphNode(
            id,
            GraphNodeKind.Code,
            PlaceholderRole(language, name),
            name,
            $"{qualifiedName} {name} data",
            language,
            "data-access",
            confidence == GraphConfidence.Heuristic ? "source-only" : "active",
            [name, qualifiedName],
            relativePath,
            line,
            line,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["qualifiedName"] = qualifiedName,
            },
            [evidence]);
    }

    private static string PlaceholderRole(string language, string name)
    {
        if (name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.Controller;
        if (name.EndsWith("Repository", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Dao", StringComparison.OrdinalIgnoreCase) ||
            language == "csharp" &&
            name.StartsWith("QR", StringComparison.Ordinal))
            return GraphRoles.Repository;
        if (name.EndsWith("Service", StringComparison.OrdinalIgnoreCase) ||
            language == "csharp" &&
            name.EndsWith("BZ", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.BusinessService;
        if (language == "csharp" &&
            (name.EndsWith("Report", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith("ReportPlugin", StringComparison.OrdinalIgnoreCase)))
            return GraphRoles.ReportPlugin;
        if (name.EndsWith("Entity", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Model", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Dto", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.DataModel;
        if (language == "csharp" &&
            name.Contains("Migration", StringComparison.OrdinalIgnoreCase))
            return GraphRoles.Migration;
        return GraphRoles.Type;
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        var value = attribute.Name.ToString().Split('.').Last();
        return value.EndsWith("Attribute", StringComparison.Ordinal)
            ? value[..^9]
            : value;
    }

    private static string? LiteralArgument(AttributeArgumentSyntax? argument) =>
        argument?.Expression is LiteralExpressionSyntax literal
            ? literal.Token.ValueText
            : null;

    private static string InvocationName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => expression.ToString(),
    };

    private static bool CSharpSqlOperation(string operation) =>
        operation.Contains("SqlCommand", StringComparison.OrdinalIgnoreCase) ||
        operation is "FromSqlRaw" or "FromSqlInterpolated" or
            "ExecuteSqlRaw" or "ExecuteSqlInterpolated" or
            "Query" or "QueryAsync" or "QueryFirst" or "QueryFirstAsync" or
            "QuerySingle" or "QuerySingleAsync" or
            "Execute" or "ExecuteAsync" or
            "ExecuteReader" or "ExecuteReaderAsync" or
            "ExecuteStoredProcedure" or "ExecuteProcedure";

    private static bool LooksLikeProcedureOperation(string operation) =>
        operation.Contains("Procedure", StringComparison.OrdinalIgnoreCase) ||
        operation.Contains("Stored", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSql(string value) =>
        Regex.IsMatch(
            value,
            @"\b(SELECT|INSERT|UPDATE|DELETE|MERGE|EXEC(?:UTE)?|WITH)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool LooksLikeSqlObjectIdentifier(string value) =>
        Regex.IsMatch(
            value,
            @"^(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_$]*)(?:\.(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_$]*))?$",
            RegexOptions.CultureInvariant);

    private static (string Schema, string Name) SplitSqlIdentifier(string identifier)
    {
        var parts = identifier.Replace("][", ".", StringComparison.Ordinal)
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim('[', ']', '"', '`'))
            .ToArray();
        return parts.Length >= 2
            ? (parts[^2], parts[^1])
            : ("dbo", parts[0]);
    }

    private static string CSharpNamespace(CompilationUnitSyntax root) =>
        root.Members.OfType<BaseNamespaceDeclarationSyntax>()
            .Select(item => item.Name.ToString())
            .FirstOrDefault() ?? string.Empty;

    private static int LineAt(string content, int index)
    {
        var line = 1;
        for (var offset = 0;
             offset < Math.Clamp(index, 0, content.Length);
             offset++)
            if (content[offset] == '\n') line++;
        return line;
    }

    private static SqlOwner ResolveSqlOwner(TSqlFragment fragment, string relativePath)
    {
        var ownerVisitor = new SqlOwnerVisitor();
        fragment.Accept(ownerVisitor);
        if (ownerVisitor.Owners.Count == 1)
        {
            var sqlOwner = ownerVisitor.Owners[0];
            var id = GraphIdentity.SqlData(
                "project-db", sqlOwner.Schema, sqlOwner.Role, sqlOwner.Name);
            var evidence = new GraphEvidence(
                GraphEvidenceSource.Ast,
                GraphConfidence.Exact,
                relativePath,
                $"由 ScriptDom 的 CREATE {sqlOwner.Role.ToUpperInvariant()} 宣告取得 SQL module。",
                sqlOwner.Line,
                sqlOwner.Line);
            return new SqlOwner(DataNode(
                id,
                sqlOwner.Role == "view" ? GraphRoles.View : GraphRoles.Procedure,
                sqlOwner.Name,
                "sql",
                "scriptdom",
                [sqlOwner.Name, $"{sqlOwner.Schema}.{sqlOwner.Name}"],
                evidence,
                new Dictionary<string, string>
                {
                    ["schema"] = sqlOwner.Schema,
                    ["objectType"] = sqlOwner.Role,
                }));
        }

        var codeEvidence = new GraphEvidence(
            GraphEvidenceSource.Ast,
            GraphConfidence.Exact,
            relativePath,
            "此 SQL 檔案包含零個或多個 module 宣告，因此以可修改的 migration 檔案作為關係 owner。",
            1,
            null);
        return new SqlOwner(new GraphNode(
            GraphIdentity.SqlCode(relativePath),
            GraphNodeKind.Code,
            GraphRoles.Migration,
            Path.GetFileName(relativePath),
            relativePath,
            "sql",
            "scriptdom",
            "active",
            [Path.GetFileNameWithoutExtension(relativePath)],
            relativePath,
            1,
            null,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["relativePath"] = relativePath,
            },
            [codeEvidence]));
    }

    private static async Task ExtractSqlModuleDependenciesAsync(
        SqlConnection connection,
        SqlServerGraphSource source,
        GraphFragment fragment,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT
                OBJECT_SCHEMA_NAME(d.referencing_id) AS source_schema,
                OBJECT_NAME(d.referencing_id) AS source_name,
                o.type AS source_type,
                COALESCE(d.referenced_schema_name, N'dbo') AS target_schema,
                d.referenced_entity_name AS target_name,
                ro.type AS target_type,
                d.is_ambiguous
            FROM sys.sql_expression_dependencies AS d
            INNER JOIN sys.objects AS o ON o.object_id = d.referencing_id
            LEFT JOIN sys.objects AS ro ON ro.object_id = d.referenced_id
            WHERE d.referenced_entity_name IS NOT NULL
              AND o.type IN ('P', 'V')
              AND (ro.object_id IS NULL OR ro.type IN ('U', 'V', 'P'));
            """;
        var rows = await QueryAsync(
            connection, query, source.CommandTimeoutSeconds, cancellationToken);
        foreach (var row in rows)
        {
            var sourceSchema = row.RequiredString("source_schema");
            var sourceName = row.RequiredString("source_name");
            var sourceRole = row.RequiredString("source_type") == "V" ? "view" : "procedure";
            var targetSchema = row.RequiredString("target_schema");
            var targetName = row.RequiredString("target_name");
            var targetRole = row.String("target_type") switch
            {
                "V" => "view",
                "P" => "procedure",
                _ => "table",
            };
            var sourceId = GraphIdentity.SqlData(
                source.DatabaseName, sourceSchema, sourceRole, sourceName);
            var targetId = GraphIdentity.SqlData(
                source.DatabaseName, targetSchema, targetRole, targetName);
            var confidence = row.Bool("is_ambiguous")
                ? GraphConfidence.Heuristic
                : GraphConfidence.Resolved;
            var evidence = DatabaseEvidence(
                source,
                "sys.sql_expression_dependencies",
                confidence,
                row.Bool("is_ambiguous")
                    ? "由 SQL Server dependency metadata 取得，但資料庫標示此參照具有歧義。"
                    : "由 SQL Server dependency metadata 唯一解析 SQL module 依賴。",
                new Dictionary<string, string>
                {
                    ["source"] = $"{sourceSchema}.{sourceName}",
                    ["target"] = $"{targetSchema}.{targetName}",
                });
            fragment.Nodes.Add(SqlObjectNode(
                source, sourceId, sourceRole, sourceName, sourceSchema, evidence));
            fragment.Nodes.Add(SqlObjectNode(
                source, targetId, targetRole, targetName, targetSchema, evidence));
            fragment.Edges.Add(CreateEdge(
                sourceId,
                targetRole == "procedure" ? GraphEdgeKind.DependsOn : GraphEdgeKind.Reads,
                targetId,
                evidence));
        }
    }

    private static async Task<IReadOnlyDictionary<long, MenuRow>> ExtractMenusAsync(
        SqlConnection connection,
        SqlServerGraphSource source,
        GraphFragment fragment,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT ID, Parent, Name, Released, LinkAddress, Description
            FROM dbo.tblMenuMap;
            """;
        var rows = await QueryAsync(
            connection, query, source.CommandTimeoutSeconds, cancellationToken);
        var menus = rows.Select(row => new MenuRow(
                row.Int64("ID"),
                row.NullableInt64("Parent"),
                row.String("Name") ?? $"Menu {row.Int64("ID")}",
                row.Bool("Released"),
                row.String("LinkAddress"),
                row.String("Description")))
            .ToDictionary(row => row.Id);
        foreach (var menu in menus.Values.OrderBy(row => row.Id))
        {
            var path = BuildMenuPath(menu, menus);
            var featureId = GraphIdentity.MenuFeature(menu.Id.ToString());
            var evidence = DatabaseEvidence(
                source,
                $"tblMenuMap/{menu.Id}",
                GraphConfidence.Exact,
                "由 tblMenuMap 的選單階層與 LinkAddress 直接取得業務功能。",
                new Dictionary<string, string>
                {
                    ["menuPath"] = path,
                    ["linkAddress"] = menu.LinkAddress ?? string.Empty,
                });
            fragment.Nodes.Add(new GraphNode(
                featureId,
                GraphNodeKind.Feature,
                menu.LinkAddress?.Contains(
                    "/CustomReport/", StringComparison.OrdinalIgnoreCase) == true
                    ? GraphRoles.CustomReport
                    : GraphRoles.MenuFeature,
                menu.Name,
                $"{path} {menu.Description} {menu.LinkAddress}",
                "business",
                "tblMenuMap",
                menu.Released ? "active" : "inactive",
                [menu.Id.ToString(), menu.Name],
                null,
                null,
                null,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["menuId"] = menu.Id.ToString(),
                    ["parentId"] = menu.Parent?.ToString() ?? string.Empty,
                    ["menuPath"] = path,
                    ["released"] = menu.Released.ToString(),
                },
                [evidence]));

            if (TryParseMenuRoute(menu.LinkAddress, out var controller, out var action))
            {
                var entryId = GraphIdentity.WebEntry(controller, action);
                fragment.Nodes.Add(PlaceholderWebEntry(entryId, controller, action, evidence));
                fragment.Edges.Add(CreateEdge(
                    featureId,
                    GraphEdgeKind.RoutesTo,
                    entryId,
                    evidence with
                    {
                        Reason = "由 tblMenuMap.LinkAddress 唯一解析至 Controller／Action 入口。",
                    }));
            }
            if (TryParseCustomReportId(menu.LinkAddress, out var reportId))
            {
                var reportFeatureId = GraphIdentity.CustomReportFeature(reportId);
                fragment.Nodes.Add(PlaceholderFeature(
                    reportFeatureId,
                    GraphRoles.CustomReport,
                    $"CustomReport {reportId}",
                    evidence with
                    {
                        Reason = "由 Menu LinkAddress 中的 TemplateID 建立待合併自訂報表功能。",
                    }));
                fragment.Edges.Add(CreateEdge(
                    featureId,
                    GraphEdgeKind.DependsOn,
                    reportFeatureId,
                    evidence with
                    {
                        Reason = "由 CustomReport Menu LinkAddress 確認選單功能依賴指定報表模板。",
                    }));
            }
        }
        return menus;
    }

    private static async Task ExtractApprovalAsync(
        SqlConnection connection,
        SqlServerGraphSource source,
        GraphFragment fragment,
        IReadOnlyDictionary<long, MenuRow> menus,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT ConfirmSourceType, MaintainMenuID, MenuID, WaitForConfirmName
            FROM dbo.tblAsyncConfirmSourceTypeMapping;
            """;
        var rows = await QueryAsync(
            connection, query, source.CommandTimeoutSeconds, cancellationToken);
        foreach (var row in rows)
        {
            var maintainId = row.NullableInt64("MaintainMenuID");
            var confirmId = row.NullableInt64("MenuID");
            if (maintainId is null || confirmId is null ||
                !menus.ContainsKey(maintainId.Value) || !menus.ContainsKey(confirmId.Value))
            {
                fragment.Diagnostics.Add(new GraphDiagnostic(
                    "ORPHAN_CONFIGURATION_SKIPPED",
                    GraphDiagnosticSeverity.Warning,
                    DatabaseArtifact(source, "tblAsyncConfirmSourceTypeMapping"),
                    "覆核對應找不到維護或覆核 Menu，已排除該孤兒設定。",
                    false));
                continue;
            }
            var sourceId = GraphIdentity.MenuFeature(maintainId.Value.ToString());
            var targetId = GraphIdentity.MenuFeature(confirmId.Value.ToString());
            var evidence = DatabaseEvidence(
                source,
                $"tblAsyncConfirmSourceTypeMapping/{row.Int32("ConfirmSourceType")}",
                GraphConfidence.Exact,
                "由 ConfirmSourceType、MaintainMenuID 與 MenuID 直接取得維護到覆核關係。",
                new Dictionary<string, string>
                {
                    ["confirmSourceType"] = row.Int32("ConfirmSourceType").ToString(),
                    ["waitForConfirmName"] = row.String("WaitForConfirmName") ?? string.Empty,
                });
            // 覆核畫面本身仍是 tblMenuMap 的同一個 Feature，不另建 Confirm node。
            // 這裡只把目標 Menu 的 role 提升為 approval-feature，讓檢索能區分維護端與覆核端；
            // ConfirmSourceType 與等待名稱保留在 edge evidence，避免把 mapping row 過度節點化。
            var confirmNodeIndex = fragment.Nodes.FindIndex(node =>
                string.Equals(node.Id, targetId, StringComparison.Ordinal));
            if (confirmNodeIndex < 0)
                throw new InvalidOperationException(
                    $"覆核 Menu {confirmId.Value} 已通過主檔檢查，但對應 Feature 不存在。");
            var confirmNode = fragment.Nodes[confirmNodeIndex];
            fragment.Nodes[confirmNodeIndex] = confirmNode with
            {
                Role = GraphRoles.ApprovalFeature,
                Evidence = confirmNode.Evidence.Append(evidence).ToList(),
            };
            fragment.Edges.Add(CreateEdge(
                sourceId, GraphEdgeKind.Triggers, targetId, evidence));
        }
    }

    private static async Task ExtractCsvAndProductTypesAsync(
        SqlConnection connection,
        SqlServerGraphSource source,
        GraphFragment fragment,
        CancellationToken cancellationToken)
    {
        const string formatsQuery = """
            WITH ranked AS (
                SELECT FormatType, Version, Enable, Lastest, Content, ParentFormatType,
                       ROW_NUMBER() OVER (
                           PARTITION BY FormatType
                           ORDER BY Lastest DESC, Enable DESC, Version DESC) AS rn
                FROM dbo.tblCSVFormat
            )
            SELECT FormatType, Version, Enable, Lastest, Content, ParentFormatType
            FROM ranked
            WHERE rn = 1;
            """;
        var formats = await QueryAsync(
            connection, formatsQuery, source.CommandTimeoutSeconds, cancellationToken);
        var knownFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in formats)
        {
            var formatType = row.RequiredString("FormatType");
            knownFormats.Add(formatType);
            var content = row.String("Content");
            var fields = ExtractBoundedXmlNames(content);
            var evidence = DatabaseEvidence(
                source,
                $"tblCSVFormat/{formatType}",
                GraphConfidence.Exact,
                "每個 FormatType 只保留 Lastest／Enable／Version 排序後的目前有效定義；欄位只存 bounded evidence。",
                new Dictionary<string, string>
                {
                    ["version"] = row.Decimal("Version").ToString(),
                    ["enabled"] = row.Bool("Enable").ToString(),
                    ["latest"] = row.Bool("Lastest").ToString(),
                    ["parentFormatType"] = row.String("ParentFormatType") ?? string.Empty,
                    ["fieldNames"] = string.Join(" | ", fields),
                });
            fragment.Nodes.Add(DataNode(
                GraphIdentity.BusinessData("csv-format", formatType),
                GraphRoles.CsvFormat,
                formatType,
                "business",
                "tblCSVFormat",
                [formatType],
                evidence));
        }

        const string productsQuery = """
            SELECT CAST(TypeID AS bigint) AS id, Name, CName, CAST(NULL AS nvarchar(200)) AS alias_name,
                   CAST(0 AS bit) AS is_custom
            FROM dbo.tblProductType
            UNION ALL
            SELECT CAST(CustomTypeID AS bigint), CustomName, CustomName, AliasName, CAST(1 AS bit)
            FROM dbo.tblCustomProductType
            WHERE Visible = 1;
            """;
        var products = await QueryAsync(
            connection, productsQuery, source.CommandTimeoutSeconds, cancellationToken);
        var productIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in products)
        {
            var isCustom = row.Bool("is_custom");
            var id = row.Int64("id").ToString();
            var nodeId = GraphIdentity.BusinessData(
                isCustom ? "custom-product-type" : "product-type", id);
            productIds.Add(nodeId);
            var name = row.String("CName") ?? row.String("Name") ?? id;
            var evidence = DatabaseEvidence(
                source,
                $"{(isCustom ? "tblCustomProductType" : "tblProductType")}/{id}",
                GraphConfidence.Exact,
                "由商品類型主檔直接取得可用商品分類。",
                new Dictionary<string, string>());
            fragment.Nodes.Add(DataNode(
                nodeId,
                isCustom ? GraphRoles.CustomProductType : GraphRoles.ProductType,
                name,
                "business",
                isCustom ? "tblCustomProductType" : "tblProductType",
                new[] { id, row.String("Name"), row.String("alias_name") }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!),
                evidence));
        }

        const string mappingQuery = """
            SELECT ProductTypeID, CustomTypeID, FormatType, Required, DisplayName
            FROM dbo.tblProductTypeMappingCsvFormatType;
            """;
        var mappings = await QueryAsync(
            connection, mappingQuery, source.CommandTimeoutSeconds, cancellationToken);
        foreach (var row in mappings)
        {
            var customId = row.NullableInt64("CustomTypeID");
            var productId = row.NullableInt64("ProductTypeID");
            var sourceId = customId is > 0
                ? GraphIdentity.BusinessData("custom-product-type", customId.Value.ToString())
                : GraphIdentity.BusinessData("product-type", (productId ?? 0).ToString());
            var format = row.RequiredString("FormatType");
            var targetId = GraphIdentity.BusinessData("csv-format", format);
            if (!productIds.Contains(sourceId) || !knownFormats.Contains(format))
            {
                if (row.Bool("Required"))
                    fragment.Diagnostics.Add(new GraphDiagnostic(
                        "CSV_MAPPING_UNRESOLVED",
                        GraphDiagnosticSeverity.Warning,
                        DatabaseArtifact(source, "tblProductTypeMappingCsvFormatType"),
                        "必要的商品類型或 CSV Format 對應找不到主檔，已排除孤兒關係。",
                        false,
                        sourceId));
                continue;
            }
            var evidence = DatabaseEvidence(
                source,
                $"tblProductTypeMappingCsvFormatType/{row.Int32("ID", fallback: 0)}",
                GraphConfidence.Exact,
                "由 ProductType／CustomType 與 FormatType mapping row 直接取得格式映射。",
                new Dictionary<string, string>
                {
                    ["required"] = row.Bool("Required").ToString(),
                    ["displayName"] = row.String("DisplayName") ?? string.Empty,
                });
            fragment.Edges.Add(CreateEdge(
                sourceId, GraphEdgeKind.MapsTo, targetId, evidence));
        }
    }

    private static async Task<IReadOnlySet<string>> ExtractCustomReportsAsync(
        SqlConnection connection,
        SqlServerGraphSource source,
        GraphFragment fragment,
        IReadOnlyDictionary<long, MenuRow> menus,
        CancellationToken cancellationToken)
    {
        const string sourceQuery = """
            SELECT SerialID, Description, XMLDefinition, Category, Scope, IsSingleData
            FROM dbo.tblCustomDesignReportDataSource;
            """;
        var sourceRows = await QueryAsync(
            connection, sourceQuery, source.CommandTimeoutSeconds, cancellationToken);
        var dataSources = new Dictionary<string, DbRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in sourceRows)
        {
            var id = row.Guid("SerialID").ToString("D");
            dataSources[id] = row;
            var evidence = DatabaseEvidence(
                source,
                $"tblCustomDesignReportDataSource/{id}",
                GraphConfidence.Exact,
                "由報表 DataSource 主檔取得；XML 只抽取物件引用與 bounded 欄位名稱。",
                new Dictionary<string, string>
                {
                    ["category"] = row.Int32("Category").ToString(),
                    ["singleData"] = row.Bool("IsSingleData").ToString(),
                });
            fragment.Nodes.Add(DataNode(
                GraphIdentity.BusinessData("report-source", id),
                GraphRoles.ReportDataSource,
                row.String("Description") ?? id,
                "business",
                "tblCustomDesignReportDataSource",
                [id],
                evidence));
            LinkXmlDataReferences(
                fragment, source,
                GraphIdentity.BusinessData("report-source", id),
                row.String("XMLDefinition"),
                evidence);
        }

        const string groupQuery = """
            SELECT g.GroupID, g.GroupName, d.DetailSerialID
            FROM dbo.tblCustomDesignReportDataSourceGroup AS g
            LEFT JOIN dbo.tblCustomDesignReportDataSourceGroupDetail AS d
              ON d.GroupID = g.GroupID;
            """;
        var groupRows = await QueryAsync(
            connection, groupQuery, source.CommandTimeoutSeconds, cancellationToken);
        foreach (var group in groupRows.GroupBy(row => row.Guid("GroupID")))
        {
            var id = group.Key.ToString("D");
            var first = group.First();
            var groupId = GraphIdentity.BusinessData("report-source-group", id);
            var evidence = DatabaseEvidence(
                source,
                $"tblCustomDesignReportDataSourceGroup/{id}",
                GraphConfidence.Exact,
                "由報表資料來源群組與 detail mapping 取得；detail row 不建立節點。",
                new Dictionary<string, string>());
            fragment.Nodes.Add(DataNode(
                groupId,
                GraphRoles.ReportDataSourceGroup,
                first.String("GroupName") ?? id,
                "business",
                "tblCustomDesignReportDataSourceGroup",
                [id],
                evidence));
            foreach (var detail in group)
            {
                var serial = detail.NullableGuid("DetailSerialID")?.ToString("D");
                if (serial is null || !dataSources.ContainsKey(serial)) continue;
                fragment.Edges.Add(CreateEdge(
                    groupId,
                    GraphEdgeKind.DependsOn,
                    GraphIdentity.BusinessData("report-source", serial),
                    evidence with
                    {
                        Reason = "由 DataSourceGroupDetail 確認群組依賴此報表資料來源。",
                    }));
            }
        }

        const string templateQuery = """
            SELECT TemplateID, TemplateName, Description, TemplateXML, Scope, IsGroup
            FROM dbo.tblCustomDesignRiskReportTemplate;
            """;
        var templates = await QueryAsync(
            connection, templateQuery, source.CommandTimeoutSeconds, cancellationToken);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in templates)
        {
            var id = row.Guid("TemplateID").ToString("D");
            known.Add(id);
            var featureId = GraphIdentity.CustomReportFeature(id);
            var xml = row.String("TemplateXML");
            var references = ExtractGuids(xml).ToList();
            var evidence = DatabaseEvidence(
                source,
                $"tblCustomDesignRiskReportTemplate/{id}",
                GraphConfidence.Exact,
                "由自訂報表模板主檔取得；Excel cell、parameter 與 XML element 不建立節點。",
                new Dictionary<string, string>
                {
                    ["dataSourceReferenceCount"] = references.Count.ToString(),
                    ["isGroup"] = row.Bool("IsGroup").ToString(),
                });
            fragment.Nodes.Add(new GraphNode(
                featureId,
                GraphNodeKind.Feature,
                GraphRoles.CustomReport,
                row.String("TemplateName") ?? id,
                $"{row.String("TemplateName")} {row.String("Description")}",
                "business",
                "tblCustomDesignRiskReportTemplate",
                "active",
                [id],
                null,
                null,
                null,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["templateId"] = id,
                },
                [evidence]));
            foreach (var reference in references)
            {
                var normalized = reference.ToString("D");
                var dataSourceId = GraphIdentity.BusinessData("report-source", normalized);
                var groupId = GraphIdentity.BusinessData("report-source-group", normalized);
                if (dataSources.ContainsKey(normalized))
                    fragment.Edges.Add(CreateEdge(
                        featureId, GraphEdgeKind.DependsOn, dataSourceId, evidence with
                        {
                            Reason = "由 TemplateXML 中的 SerialID 確認報表依賴此 DataSource。",
                        }));
                else if (groupRows.Any(item => item.Guid("GroupID") == reference))
                    fragment.Edges.Add(CreateEdge(
                        featureId, GraphEdgeKind.DependsOn, groupId, evidence with
                        {
                            Reason = "由 TemplateXML 中的 GroupID 確認報表依賴此 DataSource Group。",
                        }));
            }
        }
        return known;
    }

    private static async Task ExtractCustomEnumsAsync(
        SqlConnection connection,
        SqlServerGraphSource source,
        GraphFragment fragment,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT e.EnumName, e.EnumCategory, e.[Desc], i.Item, i.Value, i.[Desc] AS ItemDesc
            FROM dbo.tblCustomEnum AS e
            LEFT JOIN dbo.tblCustomEnumItem AS i ON i.EnumName = e.EnumName
            ORDER BY e.EnumName, i.Item;
            """;
        var rows = await QueryAsync(
            connection, query, source.CommandTimeoutSeconds, cancellationToken);
        foreach (var group in rows.GroupBy(
                     row => row.RequiredString("EnumName"), StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var items = group.Where(row => row.String("Item") is not null)
                // CustomEnum 在真實系統中也可能被用作收件人清單；
                // 即使資料表名稱不是 Contact，Email 仍不得進 graph、log 或 prompt。
                .Where(row => !ContainsEmail(
                    row.String("Item"),
                    row.String("Value"),
                    row.String("ItemDesc")))
                .Take(MaximumEvidenceItems)
                .Select(row => $"{row.String("Item")}={row.String("Value")} ({row.String("ItemDesc")})")
                .ToList();
            var evidence = DatabaseEvidence(
                source,
                $"tblCustomEnum/{group.Key}",
                GraphConfidence.Exact,
                "CustomEnum 必須以 EnumName 聚合；DataID 不是跨主檔與 Item 的穩定 join key。",
                new Dictionary<string, string>
                {
                    ["category"] = first.Int32("EnumCategory").ToString(),
                    ["itemCount"] = group.Count(row => row.String("Item") is not null).ToString(),
                    ["sampleItems"] = string.Join(" | ", items),
                });
            fragment.Nodes.Add(DataNode(
                GraphIdentity.CustomEnumData(group.Key),
                GraphRoles.CustomEnum,
                group.Key,
                "business",
                "tblCustomEnum",
                [group.Key, first.String("Desc") ?? string.Empty],
                evidence));
        }
    }

    private static async Task<IReadOnlySet<string>> ExtractBatchReportsAsync(
        SqlConnection connection,
        SqlServerGraphSource source,
        GraphFragment fragment,
        IReadOnlySet<string> customReports,
        CancellationToken cancellationToken)
    {
        const string headerQuery = """
            SELECT BatchReportID, BatchReportDescription, IsStore, IsSendReport, IsSendToFTP
            FROM dbo.tblBatchReport;
            """;
        var headers = await QueryAsync(
            connection, headerQuery, source.CommandTimeoutSeconds, cancellationToken);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in headers)
        {
            var id = row.Guid("BatchReportID").ToString("D");
            known.Add(id);
            var evidence = DatabaseEvidence(
                source,
                $"tblBatchReport/{id}",
                GraphConfidence.Exact,
                "由 BatchReport header 建立批次報表功能；收件人、Email 與 FTP 設定不進入圖譜。",
                new Dictionary<string, string>
                {
                    ["storesOutput"] = row.Bool("IsStore").ToString(),
                    ["sendsReport"] = row.Bool("IsSendReport").ToString(),
                    ["sendsToFtp"] = row.Bool("IsSendToFTP").ToString(),
                });
            fragment.Nodes.Add(new GraphNode(
                GraphIdentity.BatchReportFeature(id),
                GraphNodeKind.Feature,
                GraphRoles.BatchReport,
                row.String("BatchReportDescription") ?? id,
                row.String("BatchReportDescription") ?? id,
                "business",
                "tblBatchReport",
                "active",
                [id],
                null,
                null,
                null,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["batchReportId"] = id,
                },
                [evidence]));
        }

        const string detailQuery = """
            SELECT d.BatchReportID, d.ReportID, d.ReportSourceKey, d.ReportType, d.AliasReportName,
                   p.ParameterName, p.ParameterValue
            FROM dbo.tblBatchReportDetail AS d
            LEFT JOIN dbo.tblBatchReportParameter AS p
              ON p.BatchReportID = d.BatchReportID AND p.ReportID = d.ReportID;
            """;
        var details = await QueryAsync(
            connection, detailQuery, source.CommandTimeoutSeconds, cancellationToken);
        foreach (var group in details.GroupBy(row => new
                 {
                     Batch = row.Guid("BatchReportID"),
                     Report = row.Guid("ReportID"),
                     Type = row.Int32("ReportType"),
                     Source = row.String("ReportSourceKey"),
                 }))
        {
            var batchId = group.Key.Batch.ToString("D");
            if (!known.Contains(batchId)) continue;
            var sourceId = GraphIdentity.BatchReportFeature(batchId);
            var parameterNames = group.Select(row => row.String("ParameterName"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumEvidenceItems)
                .ToList();
            var evidence = DatabaseEvidence(
                source,
                $"tblBatchReportDetail/{batchId}/{group.Key.Report:D}",
                GraphConfidence.Exact,
                "由 BatchReport detail 取得報表依賴；parameter 只保留名稱，不保存可能含敏感值的 ParameterValue。",
                new Dictionary<string, string>
                {
                    ["reportType"] = group.Key.Type.ToString(),
                    ["parameterNames"] = string.Join(" | ", parameterNames),
                });
            var reportId = group.Key.Report.ToString("D");
            if (customReports.Contains(reportId))
            {
                fragment.Edges.Add(CreateEdge(
                    sourceId,
                    GraphEdgeKind.DependsOn,
                    GraphIdentity.CustomReportFeature(reportId),
                    evidence));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(group.Key.Source))
            {
                var codeId = GraphIdentity.CSharpCode(group.Key.Source!);
                fragment.Nodes.Add(new GraphNode(
                    codeId,
                    GraphNodeKind.Code,
                    GraphRoles.ReportPlugin,
                    group.Key.Source!,
                    group.Key.Source!,
                    "csharp",
                    "batch-report-plugin",
                    "unresolved",
                    [group.Key.Source!],
                    null,
                    null,
                    null,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["qualifiedName"] = group.Key.Source!,
                    },
                    [evidence with
                    {
                        Confidence = GraphConfidence.Heuristic,
                        Reason = "由 BatchReport.ReportSourceKey 建立待與 C# extractor 合併的 Plugin Code。",
                    }]));
                fragment.Edges.Add(CreateEdge(
                    sourceId,
                    GraphEdgeKind.DependsOn,
                    codeId,
                    evidence));
            }
        }
        return known;
    }

    private static async Task ExtractSchedulesAsync(
        SqlConnection connection,
        SqlServerGraphSource source,
        GraphFragment fragment,
        IReadOnlySet<string> batchReports,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT s.ID, s.Description, s.Frequency, s.Enabled, t.TaskID, t.Name, t.Parameter, t.Description AS TaskDescription
            FROM dbo.tblSchedule AS s
            INNER JOIN dbo.tblScheduleTask AS t ON t.ScheduleID = s.ID
            ORDER BY s.ID, t.TaskID;
            """;
        var rows = await QueryAsync(
            connection, query, source.CommandTimeoutSeconds, cancellationToken);
        foreach (var schedule in rows.GroupBy(row => row.Int64("ID")))
        {
            var first = schedule.First();
            var featureId = GraphIdentity.ScheduleFeature(schedule.Key.ToString());
            var evidence = DatabaseEvidence(
                source,
                $"tblSchedule/{schedule.Key}",
                GraphConfidence.Exact,
                "由 Schedule header 建立排程功能；EmailForError 與收件資料不抽取。",
                new Dictionary<string, string>
                {
                    ["frequency"] = first.Int32("Frequency").ToString(),
                    ["enabled"] = first.Bool("Enabled").ToString(),
                    ["taskCount"] = schedule.Count().ToString(),
                });
            fragment.Nodes.Add(new GraphNode(
                featureId,
                GraphNodeKind.Feature,
                GraphRoles.Schedule,
                first.String("Description") ?? $"Schedule {schedule.Key}",
                first.String("Description") ?? $"Schedule {schedule.Key}",
                "business",
                "tblSchedule",
                first.Bool("Enabled") ? "active" : "inactive",
                [schedule.Key.ToString()],
                null,
                null,
                null,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["scheduleId"] = schedule.Key.ToString(),
                },
                [evidence]));

            foreach (var task in schedule)
            {
                var taskName = task.String("Name");
                if (string.IsNullOrWhiteSpace(taskName)) continue;
                var taskId = GraphIdentity.TaskEntry(taskName);
                var taskEvidence = DatabaseEvidence(
                    source,
                    $"tblScheduleTask/{schedule.Key}/{task.Int32("TaskID")}",
                    GraphConfidence.Exact,
                    "由 ScheduleTask.Name 建立共享 Task 入口；task row 與 Parameter 不建立節點。",
                    new Dictionary<string, string>
                    {
                        ["taskId"] = task.Int32("TaskID").ToString(),
                        ["description"] = task.String("TaskDescription") ?? string.Empty,
                    });
                fragment.Nodes.Add(new GraphNode(
                    taskId,
                    GraphNodeKind.EntryPoint,
                    GraphRoles.ScheduledTask,
                    taskName,
                    $"{taskName} {task.String("TaskDescription")}",
                    "business",
                    "tblScheduleTask",
                    "active",
                    [taskName],
                    null,
                    null,
                    null,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["taskName"] = taskName,
                    },
                    [taskEvidence]));
                fragment.Edges.Add(CreateEdge(
                    featureId,
                    GraphEdgeKind.Triggers,
                    taskId,
                    taskEvidence));

                foreach (var reportGuid in ExtractGuids(task.String("Parameter")))
                {
                    var reportId = reportGuid.ToString("D");
                    if (!batchReports.Contains(reportId)) continue;
                    fragment.Edges.Add(CreateEdge(
                        taskId,
                        GraphEdgeKind.Triggers,
                        GraphIdentity.BatchReportFeature(reportId),
                        taskEvidence with
                        {
                            Reason = "由 Task Parameter 中的 BatchReportID 確認排程 Task 觸發批次報表；不保存原始 Parameter。",
                        }));
                }
            }
        }
    }

    private static void LinkXmlDataReferences(
        GraphFragment fragment,
        SqlServerGraphSource source,
        string sourceId,
        string? xml,
        GraphEvidence parentEvidence)
    {
        foreach (var reference in ExtractSqlObjectNames(xml))
        {
            var (schema, name) = SplitSqlName(reference);
            var targetId = GraphIdentity.SqlData(
                source.DatabaseName, schema, "table", name);
            var evidence = parentEvidence with
            {
                Confidence = GraphConfidence.Heuristic,
                Reason = "由報表 DataSource XML 中的 SQL 物件名稱抽取資料讀取關係。",
                Details = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sqlObject"] = $"{schema}.{name}",
                },
            };
            fragment.Nodes.Add(SqlObjectNode(
                source, targetId, "table", name, schema, evidence));
            fragment.Edges.Add(CreateEdge(
                sourceId, GraphEdgeKind.Reads, targetId, evidence));
        }
    }

    private static IEnumerable<string> ExtractSqlObjectNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (Match match in SqlObjectNameRegex().Matches(value))
            yield return match.Groups["name"].Value;
    }

    private static IEnumerable<string> ExtractBoundedXmlNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        try
        {
            var document = XDocument.Parse(value, LoadOptions.None);
            return document.Descendants()
                .SelectMany(element => element.Attributes()
                    .Where(attribute => attribute.Name.LocalName.Contains(
                        "name", StringComparison.OrdinalIgnoreCase))
                    .Select(attribute => attribute.Value)
                    .Prepend(element.Name.LocalName))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumEvidenceItems)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<Guid> ExtractGuids(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (Match match in GuidRegex().Matches(value))
            if (Guid.TryParse(match.Value, out var guid))
                yield return guid;
    }

    private static (string Schema, string Name) SplitSqlName(string value)
    {
        var normalized = value.Replace("[", string.Empty).Replace("]", string.Empty);
        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? (segments[^2], segments[^1]) : ("dbo", segments[^1]);
    }

    private static bool ContainsEmail(params string?[] values) =>
        EmailValueRegex().IsMatch(
            string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value))));

    private static GraphNode SqlObjectNode(
        SqlServerGraphSource source,
        string id,
        string objectRole,
        string name,
        string schema,
        GraphEvidence evidence) =>
        DataNode(
            id,
            objectRole switch
            {
                "view" => GraphRoles.View,
                "procedure" => GraphRoles.Procedure,
                _ => GraphRoles.Table,
            },
            name,
            "sql",
            "sql-server",
            [name, $"{schema}.{name}"],
            evidence,
            new Dictionary<string, string>
            {
                ["database"] = NormalizeDatabase(source.DatabaseName),
                ["schema"] = schema,
                ["objectType"] = objectRole,
            });

    private static GraphNode DataNode(
        string id,
        string role,
        string name,
        string language,
        string technology,
        IEnumerable<string> aliases,
        GraphEvidence evidence,
        IReadOnlyDictionary<string, string>? attributes = null) =>
        new(
            id,
            GraphNodeKind.Data,
            role,
            name,
            string.Join(' ', aliases.Where(value => !string.IsNullOrWhiteSpace(value))),
            language,
            technology,
            "active",
            aliases.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            null,
            null,
            null,
            attributes ?? new SortedDictionary<string, string>(StringComparer.Ordinal),
            [evidence]);

    private static GraphNode PlaceholderWebEntry(
        string id,
        string controller,
        string action,
        GraphEvidence evidence) =>
        new(
            id,
            GraphNodeKind.EntryPoint,
            GraphRoles.ControllerAction,
            $"{controller}/{action}",
            $"{controller} {action}",
            "business",
            "http",
            "unresolved",
            [$"/{controller}/{action}"],
            null,
            null,
            null,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["controller"] = controller,
                ["action"] = action,
            },
            [evidence]);

    private static GraphNode PlaceholderFeature(
        string id,
        string role,
        string name,
        GraphEvidence evidence) =>
        new(
            id,
            GraphNodeKind.Feature,
            role,
            name,
            name,
            "business",
            "database-mapping",
            "unresolved",
            [name],
            null,
            null,
            null,
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            [evidence]);

    private static GraphEvidence DatabaseEvidence(
        SqlServerGraphSource source,
        string logicalArtifact,
        GraphConfidence confidence,
        string reason,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(
            GraphEvidenceSource.Sql,
            confidence,
            DatabaseArtifact(source, logicalArtifact),
            reason,
            null,
            null,
            details);

    private static string DatabaseArtifact(
        SqlServerGraphSource source,
        string logicalArtifact) =>
        $"db:{NormalizeDatabase(source.DatabaseName)}/{logicalArtifact.Trim('/')}";

    private static string NormalizeDatabase(string value) =>
        GraphIdentity.NormalizeRequiredToken(value, nameof(value));

    private static GraphEdge CreateEdge(
        string source,
        GraphEdgeKind kind,
        string target,
        GraphEvidence evidence) =>
        new(GraphIdentity.Edge(source, kind, target), source, kind, target, [evidence]);

    private static async Task<IReadOnlyList<DbRow>> QueryAsync(
        SqlConnection connection,
        string commandText,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = timeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<DbRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
                values[reader.GetName(index)] = await reader.IsDBNullAsync(index, cancellationToken)
                    ? null
                    : reader.GetValue(index);
            rows.Add(new DbRow(values));
        }
        return rows;
    }

    private static async Task ExecuteNonQueryAsync(
        SqlConnection connection,
        string commandText,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildMenuPath(
        MenuRow menu,
        IReadOnlyDictionary<long, MenuRow> menus)
    {
        var names = new Stack<string>();
        var visited = new HashSet<long>();
        MenuRow? current = menu;
        while (current is not null && visited.Add(current.Id))
        {
            names.Push(current.Name);
            current = current.Parent is not null &&
                      menus.TryGetValue(current.Parent.Value, out var parent)
                ? parent
                : null;
        }
        return string.Join(" > ", names);
    }

    private static bool TryParseMenuRoute(
        string? linkAddress,
        out string controller,
        out string action)
    {
        controller = string.Empty;
        action = string.Empty;
        if (string.IsNullOrWhiteSpace(linkAddress)) return false;
        var path = linkAddress.Split('?', '#')[0].Trim('~', '/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            segments[0].Equals("CustomReport", StringComparison.OrdinalIgnoreCase))
            return false;
        controller = segments[0];
        action = segments[1];
        return true;
    }

    private static bool TryParseCustomReportId(string? linkAddress, out string reportId)
    {
        reportId = string.Empty;
        if (string.IsNullOrWhiteSpace(linkAddress)) return false;
        var match = CustomReportRouteRegex().Match(linkAddress);
        if (!match.Success || !Guid.TryParse(match.Groups["id"].Value, out var guid))
            return false;
        reportId = guid.ToString("D");
        return true;
    }

    private static bool IsInsideRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IgnoredPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Split('/').Any(segment =>
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("vendor", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SqlOwner(GraphNode Node);
    private sealed record SqlObjectReference(string Schema, string Name, int Line);
    private sealed record SqlModuleOwner(string Schema, string Name, string Role, int Line);
    private sealed record MenuRow(
        long Id,
        long? Parent,
        string Name,
        bool Released,
        string? LinkAddress,
        string? Description);

    private sealed class SqlDependencyVisitor : TSqlFragmentVisitor
    {
        internal HashSet<SqlObjectReference> AllTables { get; } = [];
        internal HashSet<SqlObjectReference> WriteTables { get; } = [];

        public override void ExplicitVisit(NamedTableReference node)
        {
            var reference = ToReference(node.SchemaObject, node.StartLine);
            if (reference is not null) AllTables.Add(reference);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertSpecification node)
        {
            AddWriteTarget(node.Target);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            AddWriteTarget(node.Target);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            AddWriteTarget(node.Target);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeSpecification node)
        {
            AddWriteTarget(node.Target);
            base.ExplicitVisit(node);
        }

        private void AddWriteTarget(TableReference? target)
        {
            if (target is NamedTableReference named)
            {
                var reference = ToReference(named.SchemaObject, named.StartLine);
                if (reference is not null) WriteTables.Add(reference);
            }
        }

        private static SqlObjectReference? ToReference(
            SchemaObjectName? name,
            int line)
        {
            if (name is null || name.BaseIdentifier is null) return null;
            return new SqlObjectReference(
                name.SchemaIdentifier?.Value ?? "dbo",
                name.BaseIdentifier.Value,
                line);
        }
    }

    private sealed class SqlOwnerVisitor : TSqlFragmentVisitor
    {
        internal List<SqlModuleOwner> Owners { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            var name = node.ProcedureReference?.Name;
            if (name?.BaseIdentifier is not null)
                Owners.Add(new SqlModuleOwner(
                    name.SchemaIdentifier?.Value ?? "dbo",
                    name.BaseIdentifier.Value,
                    "procedure",
                    node.StartLine));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateViewStatement node)
        {
            var name = node.SchemaObjectName;
            if (name?.BaseIdentifier is not null)
                Owners.Add(new SqlModuleOwner(
                    name.SchemaIdentifier?.Value ?? "dbo",
                    name.BaseIdentifier.Value,
                    "view",
                    node.StartLine));
            base.ExplicitVisit(node);
        }
    }

    private sealed class DbRow(IReadOnlyDictionary<string, object?> values)
    {
        internal string? String(string name) =>
            values.TryGetValue(name, out var value) ? Convert.ToString(value) : null;

        internal string RequiredString(string name) =>
            String(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"SQL metadata 欄位 {name} 不可為空。");

        internal int Int32(string name, int? fallback = null) =>
            values.TryGetValue(name, out var value) && value is not null
                ? Convert.ToInt32(value)
                : fallback ?? throw new InvalidOperationException($"SQL metadata 欄位 {name} 不可為空。");

        internal long Int64(string name) =>
            values.TryGetValue(name, out var value) && value is not null
                ? Convert.ToInt64(value)
                : throw new InvalidOperationException($"SQL metadata 欄位 {name} 不可為空。");

        internal long? NullableInt64(string name) =>
            values.TryGetValue(name, out var value) && value is not null
                ? Convert.ToInt64(value)
                : null;

        internal bool Bool(string name) =>
            values.TryGetValue(name, out var value) && value is not null &&
            Convert.ToBoolean(value);

        internal decimal Decimal(string name) =>
            values.TryGetValue(name, out var value) && value is not null
                ? Convert.ToDecimal(value)
                : 0m;

        internal Guid Guid(string name) =>
            NullableGuid(name) ??
            throw new InvalidOperationException($"SQL metadata 欄位 {name} 不可為空。");

        internal Guid? NullableGuid(string name)
        {
            if (!values.TryGetValue(name, out var value) || value is null) return null;
            return value is Guid guid ? guid : System.Guid.Parse(Convert.ToString(value)!);
        }
    }

    private sealed record CSharpSourceFile(
        string RelativePath,
        string Source,
        CompilationUnitSyntax Root);

    [GeneratedRegex(
        @"(?i)(?<name>(?:\[?[A-Za-z_][\w$]*\]?\.)?\[?(?:tbl|vw|v_|usp_|sp_)[A-Za-z_][\w$]*\]?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SqlObjectNameRegex();

    [GeneratedRegex(
        @"(?i)/CustomReport/MenuIndex/(?<id>[0-9a-f-]{36})",
        RegexOptions.CultureInvariant)]
    private static partial Regex CustomReportRouteRegex();

    [GeneratedRegex(
        @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();

    [GeneratedRegex(
        @"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailValueRegex();

    [GeneratedRegex(
        """Entity\s*<\s*(?<type>[A-Za-z_][A-Za-z0-9_.]*)\s*>\s*\([^)]*\)[\s\S]{0,500}?ToTable\(\s*"(?<table>[^"]+)"(?:\s*,\s*"(?<schema>[^"]+)")?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CSharpToTableRegex();

    [GeneratedRegex(
        @"\bpackage\s+(?<package>[A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.CultureInvariant)]
    private static partial Regex JavaPackageRegex();

    [GeneratedRegex(
        """@Table\s*\(\s*(?:name\s*=\s*)?"(?<table>[^"]+)"(?:\s*,\s*schema\s*=\s*"(?<schema>[^"]+)")?[^)]*\)[\s\S]{0,300}?\bclass\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaTableRegex();

    [GeneratedRegex(
        @"\b(?:class|record|interface|enum)\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex JavaTypeRegex();

    [GeneratedRegex(
        @"\b(?:query|queryForObject|queryForList|update|execute)\s*\(\s*""(?<sql>(?:\\.|[^""])*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaSqlLiteralRegex();
}
