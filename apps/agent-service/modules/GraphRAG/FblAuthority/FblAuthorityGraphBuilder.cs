namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 描述一次 FBL 權威圖建置所需的最小輸入。
/// SQL 資料由唯讀介面提供，讓正式資料庫、測試替身與離線快照共用同一抽取流程。
/// </summary>
public sealed record FblAuthorityBuildRequest(
    string RootPath,
    IFblAuthoritySqlSource SqlSource,
    int? ExpectedMenuCount = null,
    string? SourceCommit = null,
    string? DatabaseSnapshotId = null,
    string Provider = "SqlServer",
    string DatabaseName = "",
    bool ValidateGoldenPaths = false);

/// <summary>
/// 回傳同一個 run 的完整圖與 Preflight 診斷。
/// 呼叫端只有在 <see cref="PreflightResult.HasBlockingErrors"/> 為 false 時才能發布此圖。
/// </summary>
public sealed record FblAuthorityBuildResult(
    GraphDocument Document,
    PreflightResult Diagnostics);

/// <summary>
/// FBL 投資交易系統確定性抽取的唯一入口。
/// 此 façade 固定依序建立菜單入口、MVC、前端腳本、PluginReport、CustomReport、覆核、轉檔與後端資料鏈，
/// 不包含 Neo4j 發布、Community Summary、Embedding 或問答邏輯，避免抽取核心與儲存層互相綁定。
/// </summary>
public sealed class FblAuthorityGraphBuilder
{
    /// <summary>
    /// 依已驗證的 FBL 順序建立完整 GraphDocument，並在回傳前執行 schema、拓樸、數量與 golden path 驗證。
    /// 本方法只讀取原始碼與 <see cref="IFblAuthoritySqlSource"/>，不會修改專案檔案或資料庫。
    /// </summary>
    /// <param name="request">原始碼根目錄、唯讀 SQL source 與版本資訊。</param>
    /// <param name="cancellationToken">取消時會停止檔案掃描及所有 SQL 查詢。</param>
    public async Task<FblAuthorityBuildResult> BuildAsync(
        FblAuthorityBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SqlSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        if (request.ExpectedMenuCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.ExpectedMenuCount,
                "ExpectedMenuCount 必須大於零。");
        }

        var rootPath = Path.GetFullPath(request.RootPath);
        var databaseName = string.IsNullOrWhiteSpace(request.DatabaseName) &&
                           request.SqlSource is FblSqlServerAuthoritySource sqlSource
            ? sqlSource.DatabaseName
            : request.DatabaseName;
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"FBL 原始碼根目錄不存在：{rootPath}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        GraphSchema.EnsureCompleteMappings();

        // Menu 是 SQL Server FBL authority graph 的中心，不從檔名或向量相似度額外猜測入口。
        var menus = await request.SqlSource
            .LoadMenusAsync(cancellationToken)
            .ConfigureAwait(false);
        var inventory = menus.Count > 0
            ? new MenuInventoryExtractor().Extract(
                menus,
                rootPath,
                request.SourceCommit,
                request.DatabaseSnapshotId,
                request.Provider,
                databaseName)
            : CreateEmptyInventory(
                rootPath,
                request.SourceCommit,
                request.DatabaseSnapshotId,
                request.Provider,
                databaseName);
        var extraction = new ExtractionResult(
            inventory,
            Array.Empty<PreflightIssue>());

        // C#、MVC View 與瀏覽器端 JavaScript／TypeScript 各建立一次記憶體索引，後續 resolver 只查索引。
        var csharpIndex = await CSharpSourceIndex
            .CreateAsync(rootPath, cancellationToken)
            .ConfigureAwait(false);
        var viewIndex = await ViewSourceIndex
            .CreateAsync(rootPath, cancellationToken)
            .ConfigureAwait(false);
        var browserScriptIndex = await ClientScriptSourceIndex
            .CreateAsync(rootPath, cancellationToken)
            .ConfigureAwait(false);
        extraction = new StandardWebEntryResolver(csharpIndex, viewIndex, browserScriptIndex)
            .Resolve(extraction.Document);

        // 通用 SQL Server metadata 與 FBL overlay 共用同一組 user-configured 唯讀來源；
        // 支援 optional capability 的正式來源只掃描 sys catalog 一次，測試替身仍可只提供舊物件清單。
        IReadOnlyList<DatabaseObjectCatalogItem> databaseObjects;
        if (request.SqlSource is IGenericDatabaseMetadataSource metadataSource)
        {
            var metadata = await metadataSource
                .LoadDatabaseMetadataAsync(cancellationToken)
                .ConfigureAwait(false);
            databaseObjects = metadata.Objects;
            extraction = new DatabaseMetadataGraphResolver().Resolve(extraction, metadata);
        }
        else
        {
            databaseObjects = await request.SqlSource
                .LoadDatabaseObjectsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        extraction = await new PluginReportResolver(csharpIndex, rootPath)
            .ResolveAsync(extraction, cancellationToken)
            .ConfigureAwait(false);

        var customReportCatalog = await request.SqlSource
            .LoadCustomReportsAsync(cancellationToken)
            .ConfigureAwait(false);
        extraction = new CustomReportResolver(customReportCatalog, databaseObjects)
            .Resolve(extraction);

        var confirmSourceIndex = await ConfirmSourceTypeIndex
            .CreateAsync(rootPath, cancellationToken)
            .ConfigureAwait(false);
        var confirmMappings = await request.SqlSource
            .LoadConfirmMappingsAsync(cancellationToken)
            .ConfigureAwait(false);
        extraction = new ConfirmResolver(csharpIndex, confirmSourceIndex, confirmMappings)
            .Resolve(extraction);

        extraction = new FileTransformResolver(csharpIndex).Resolve(extraction);
        extraction = new BackendDependencyResolver(csharpIndex, databaseObjects).Resolve(extraction);

        // FBL domain overlay 完成後，再加入 ParallelExtractor 方法論的 Roslyn semantic graph，
        // 讓既有 CodeClass／WebAction 能直接連到精準的 Type／Method；此階段只讀原始碼，
        // 不接觸使用者資料庫連線或 Neo4j 發布流程。
        var semantic = await new RoslynSemanticGraphExtractor()
            .ExtendAsync(extraction.Document, rootPath, cancellationToken)
            .ConfigureAwait(false);
        extraction = new ExtractionResult(
            semantic.Document,
            extraction.Issues.Concat(semantic.Issues).ToArray());

        // 所有 resolver 完成後才標記 CompleteExtraction，避免半成品被上層誤發布。
        var completedDocument = GraphDocumentBuilder
            .FromDocument(extraction.Document, GraphBuildStage.CompleteExtraction)
            .Build();
        var issues = extraction.Issues
            .Concat(request.ValidateGoldenPaths
                ? GoldenPathValidator.Validate(completedDocument)
                : Array.Empty<PreflightIssue>())
            .ToArray();
        var diagnostics = new GraphDocumentValidator(new PreflightValidatorOptions
        {
            ExpectedCenterMenuCount = request.ExpectedMenuCount ?? menus.Count,
            RequiredDatabaseName = databaseName,
            RequiredProvider = request.Provider,
            RequireCompleteExtraction = true,
        }).Validate(completedDocument, issues);

        return new FblAuthorityBuildResult(completedDocument, diagnostics);
    }

    /// <summary>
    /// Generic SQL Server 沒有 tblMenuMap 時建立空的 FBL overlay 起點；
    /// 不建立不存在的 tblMenuMap 節點，但仍保留相同 provider/database scope 與完整發布階段。
    /// </summary>
    private static GraphDocument CreateEmptyInventory(
        string rootPath,
        string? sourceCommit,
        string? databaseSnapshotIdentity,
        string provider,
        string databaseName)
    {
        var metadata = new GraphRunMetadata(
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            rootPath,
            databaseName,
            GraphBuildStage.MenuInventory,
            sourceCommit,
            databaseSnapshotIdentity,
            provider);
        return new GraphDocumentBuilder(metadata).Build();
    }
}
