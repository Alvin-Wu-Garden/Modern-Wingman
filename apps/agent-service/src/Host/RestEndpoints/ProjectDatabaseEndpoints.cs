using AgentService.Application.Contracts;
using AgentService.Modules.GraphRAG;

namespace AgentService.Host.RestEndpoints;

/// <summary>專案右鍵選單使用的 SQL Server／SQLite 設定 API。</summary>
public static class ProjectDatabaseEndpoints
{
    public sealed record SaveProjectDatabaseRequest(
        string Provider,
        string? Server,
        int? Port,
        string? DatabaseName,
        string? Authentication,
        string? Username,
        string? Password,
        bool? TrustServerCertificate,
        string? SqlitePath);

    /// <summary>
    /// 註冊專案資料庫設定端點。設定異動只標記既有 Graph 索引失效，
    /// 不在 HTTP request 內啟動耗時的重新索引。
    /// </summary>
    /// <param name="app">ASP.NET Core endpoint builder。</param>
    /// <returns>原始 builder，供 host 繼續註冊其他端點。</returns>
    public static IEndpointRouteBuilder MapProjectDatabaseEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/database");
        group.MapGet("/", Get);
        group.MapGet("/all", GetAll);
        group.MapPut("/", Save);
        group.MapPut("/{provider}", SaveProvider);
        group.MapDelete("/", Delete);
        group.MapDelete("/{provider}", DeleteProvider);
        group.MapPost("/test", Test);
        group.MapPost("/databases", ListDatabases);
        return app;
    }

    private static async Task<IResult> Get(
        string projectId,
        IProjectRepository projects,
        IProjectDatabaseConfigurationStore configurations,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound();
        var configuration = await configurations.GetAsync(projectId, false, ct);
        return configuration is null
            ? Results.NoContent()
            : Results.Ok(ToDto(configuration));
    }

    /// <summary>取得專案全部已設定 Provider，供多資料來源設定頁使用。</summary>
    private static async Task<IResult> GetAll(
        string projectId,
        IProjectRepository projects,
        IProjectDatabaseConfigurationStore configurations,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound();
        return Results.Ok((await configurations.GetAllAsync(projectId, false, ct))
            .Select(ToDto)
            .ToArray());
    }

    /// <summary>
    /// 驗證並保存一組專案資料庫設定，成功後只將索引標成待更新。
    /// 此方法不測試或抽取外部資料庫，避免設定 request 被長時間工作阻塞。
    /// </summary>
    /// <returns>成功時回傳不含密碼的設定 DTO；輸入不合法時回傳 400。</returns>
    private static async Task<IResult> Save(
        string projectId,
        SaveProjectDatabaseRequest request,
        IProjectRepository projects,
        IProjectDatabaseConfigurationStore configurations,
        GraphIndexingService indexing,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound();

        var validation = await ValidateRequestAsync(
            projectId,
            request,
            configurations,
            useStoredPassword: false,
            ct);
        if (validation.Error is not null)
            return Results.BadRequest(new { error = validation.Error });

        await configurations.SaveAsync(validation.Configuration!, ct);
        // 設定版本不應直接重建圖譜；先使專案呈現 PendingChanges，
        // 由既有索引操作在使用者可控的時間重新連線與發布。
        await indexing.MarkPendingChangesAsync(projectId, cancellationToken: ct);
        return Results.Ok(ToDto(
            (await configurations.GetAsync(
                projectId,
                validation.Configuration!.Provider,
                false,
                ct))!));
    }

    /// <summary>依 Provider 分流的 PUT 別名，讓 SQL Server 與 SQLite 可各自保存。</summary>
    private static Task<IResult> SaveProvider(
        string projectId,
        string provider,
        SaveProjectDatabaseRequest request,
        IProjectRepository projects,
        IProjectDatabaseConfigurationStore configurations,
        GraphIndexingService indexing,
        CancellationToken ct)
    {
        var normalized = string.Equals(provider, request.Provider, StringComparison.OrdinalIgnoreCase)
            ? request
            : request with { Provider = provider };
        return Save(projectId, normalized, projects, configurations, indexing, ct);
    }

    /// <summary>
    /// 驗證 Modal 傳入的候選設定並建立記憶體內領域模型。
    /// 測試連線可以解密並暫時沿用既有密碼；儲存流程則保留 null，
    /// 由 repository 延用既有 DPAPI 密文，兩條路徑都不會把密碼回傳前端。
    /// </summary>
    private static async Task<ValidationResult> ValidateRequestAsync(
        string projectId,
        SaveProjectDatabaseRequest request,
        IProjectDatabaseConfigurationStore configurations,
        bool useStoredPassword,
        CancellationToken ct)
    {
        if (!Enum.TryParse<ProjectDatabaseProvider>(
                request.Provider,
                ignoreCase: true,
                out var provider))
        {
            return new(null, "Provider 必須是 SqlServer 或 Sqlite。");
        }

        var existing = await configurations.GetAsync(
            projectId,
            provider,
            includePassword: useStoredPassword,
            ct: ct);
        if (provider == ProjectDatabaseProvider.SqlServer)
        {
            if (string.IsNullOrWhiteSpace(request.Server) ||
                string.IsNullOrWhiteSpace(request.DatabaseName))
            {
                return new(null, "請填寫 SQL Server 與資料庫名稱。");
            }
            if (request.Port is <= 0 or > 65535)
                return new(null, "連接埠必須介於 1 到 65535。");
            if (!Enum.TryParse<SqlServerAuthentication>(
                    request.Authentication,
                    ignoreCase: true,
                    out var authentication))
            {
                return new(
                    null,
                    "Authentication 必須是 SqlPassword 或 IntegratedSecurity。");
            }

            var candidatePassword = string.IsNullOrEmpty(request.Password)
                ? null
                : request.Password;
            var effectivePassword = useStoredPassword && candidatePassword is null
                ? existing?.Password
                : candidatePassword;
            var passwordAvailable = useStoredPassword
                ? !string.IsNullOrEmpty(effectivePassword)
                : candidatePassword is not null || existing?.HasPassword == true;
            if (authentication == SqlServerAuthentication.SqlPassword &&
                (string.IsNullOrWhiteSpace(request.Username) ||
                 !passwordAvailable))
            {
                return new(null, "SQL 帳號驗證需要使用者名稱與密碼。");
            }

            var configuration = new ProjectDatabaseConfiguration(
                projectId,
                provider,
                request.Server.Trim(),
                request.Port,
                request.DatabaseName.Trim(),
                authentication,
                authentication == SqlServerAuthentication.SqlPassword
                    ? request.Username?.Trim()
                    : null,
                useStoredPassword ? effectivePassword : candidatePassword,
                existing?.HasPassword == true || candidatePassword is not null,
                request.TrustServerCertificate ?? true,
                null,
                DateTimeOffset.UtcNow);
            return new(configuration, null);
        }

        if (string.IsNullOrWhiteSpace(request.SqlitePath))
            return new(null, "請選擇 SQLite 資料庫檔案。");
        var sqlitePath = Path.GetFullPath(request.SqlitePath);
        if (!File.Exists(sqlitePath))
            return new(null, "SQLite 資料庫檔案不存在。");
        return new(
            new ProjectDatabaseConfiguration(
                projectId,
                provider,
                null,
                null,
                Path.GetFileNameWithoutExtension(sqlitePath),
                null,
                null,
                null,
                false,
                false,
                sqlitePath,
                DateTimeOffset.UtcNow),
            null);
    }

    /// <summary>
    /// 刪除專案資料庫設定並將既有索引標成待更新。
    /// 只刪除本機加密設定，不會對使用者的外部資料庫執行任何變更。
    /// </summary>
    /// <returns>專案存在時回傳 204，否則回傳 404。</returns>
    private static async Task<IResult> Delete(
        string projectId,
        IProjectRepository projects,
        IProjectDatabaseConfigurationStore configurations,
        GraphIndexingService indexing,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound();
        await configurations.DeleteAsync(projectId, provider: null, ct: ct);
        await indexing.MarkPendingChangesAsync(projectId, cancellationToken: ct);
        return Results.NoContent();
    }

    /// <summary>只刪除指定 Provider 的本機設定，不影響其他資料來源。</summary>
    private static async Task<IResult> DeleteProvider(
        string projectId,
        string provider,
        IProjectRepository projects,
        IProjectDatabaseConfigurationStore configurations,
        GraphIndexingService indexing,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound();
        if (!Enum.TryParse<ProjectDatabaseProvider>(provider, true, out var parsed))
            return Results.BadRequest(new { error = "Provider 必須是 SqlServer 或 Sqlite。" });
        await configurations.DeleteAsync(projectId, parsed, ct);
        await indexing.MarkPendingChangesAsync(projectId, cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Test(
        string projectId,
        SaveProjectDatabaseRequest request,
        IProjectRepository projects,
        IProjectDatabaseConfigurationStore configurations,
        ProjectGraphDatabaseExtractor extractor,
        ISensitiveDataRedactor redactor,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound();

        var validation = await ValidateRequestAsync(
            projectId,
            request,
            configurations,
            useStoredPassword: true,
            ct);
        if (validation.Error is not null)
            return Results.BadRequest(new { error = validation.Error });

        try
        {
            // 只從候選設定建立暫時連線來源；此路徑刻意不呼叫 SaveAsync。
            var source = ProjectGraphDatabaseSourceProvider.Build(
                validation.Configuration!);
            await extractor.TestConnectionAsync(source, ct);
            return Results.Ok(new { success = true, message = "資料庫連線成功。" });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Ok(new
            {
                success = false,
                error = redactor.Redact(exception.Message),
            });
        }
    }

    /// <summary>
    /// 使用 Modal 內尚未儲存的 SQL Server 連線資料讀取可用資料庫名稱。
    /// 查詢固定連到 master，整條路徑不呼叫 SaveAsync，因此不會保存候選密碼。
    /// </summary>
    private static async Task<IResult> ListDatabases(
        string projectId,
        SaveProjectDatabaseRequest request,
        IProjectRepository projects,
        IProjectDatabaseConfigurationStore configurations,
        ProjectGraphDatabaseExtractor extractor,
        ISensitiveDataRedactor redactor,
        CancellationToken ct)
    {
        if (await projects.GetAsync(projectId, ct) is null)
            return Results.NotFound();
        if (!string.Equals(
                request.Provider,
                nameof(ProjectDatabaseProvider.SqlServer),
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "只有 SQL Server 支援資料庫清單。" });
        }

        // 清單查詢不需要使用者先知道資料庫名稱；master 只作為系統目錄入口。
        var validation = await ValidateRequestAsync(
            projectId,
            request with { DatabaseName = "master" },
            configurations,
            useStoredPassword: true,
            ct);
        if (validation.Error is not null)
            return Results.BadRequest(new { error = validation.Error });

        try
        {
            var source = ProjectGraphDatabaseSourceProvider.Build(
                validation.Configuration!);
            var databases = await extractor.ListSqlServerDatabasesAsync(source, ct);
            return Results.Ok(new { databases });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.BadRequest(new
            {
                error = redactor.Redact(exception.Message),
            });
        }
    }

    private sealed record ValidationResult(
        ProjectDatabaseConfiguration? Configuration,
        string? Error);

    private static object ToDto(ProjectDatabaseConfiguration configuration) => new
    {
        projectId = configuration.ProjectId,
        provider = configuration.Provider.ToString(),
        configuration.Server,
        configuration.Port,
        configuration.DatabaseName,
        authentication = configuration.Authentication?.ToString(),
        configuration.Username,
        configuration.HasPassword,
        configuration.TrustServerCertificate,
        configuration.SqlitePath,
        configuration.UpdatedAt,
    };
}
