using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Modules.GraphRAG;
using Neo4j.Driver;

namespace AgentService.Host.RestEndpoints;

/// <summary>
/// 企業程式碼解析 REST 端點（WS3）。
///
/// GET    /api/projects                       → 專案列表
/// POST   /api/projects                       → 新增專案
/// DELETE /api/projects/{id}                  → 移除專案（含圖譜）
/// POST   /api/projects/{id}/index            → 啟動全量索引（背景執行）
/// POST   /api/projects/{id}/index/incremental → 增量索引
/// GET    /api/projects/{id}/index/progress   → 索引進度（輪詢）
/// GET    /api/projects/{id}/summaries/progress → 查詢背景 AI 社群摘要進度
/// POST   /api/projects/{id}/query            → GraphRAG 問答（auto/global/local）
/// GET    /api/projects/{id}/repomap          → Repo Map
/// </summary>
public static class ProjectEndpoints
{
    public sealed record CreateProjectRequest(string Name, string RootPath);
    public sealed record ImportProjectRequest(
        string SourceType,
        string Name,
        string ProfileId,
        string RepositoryUrl,
        string? Ref,
        string DestinationPath,
        string? OperationId = null);
    public sealed record GraphQueryRequest(string Cypher, int? Limit);
    public sealed record GraphNeighborsRequest(
        IReadOnlyList<string> NodeKeys,
        int? Depth,
        int? Limit,
        string? Mode);
    /// <summary>Viewer Contract 的 bounded 初始圖請求。</summary>
    public sealed record GraphViewRequest(
        IReadOnlyList<GraphViewerSearchFilter>? Filters,
        int? Limit);
    /// <summary>Viewer Contract 的全域搜尋請求。</summary>
    public sealed record GraphSearchRequest(
        string Query,
        IReadOnlyList<GraphViewerSearchFilter>? Filters,
        int? Take);
    public sealed record ProjectListItem(
        string Id,
        string Name,
        string RootPath,
        string Languages,
        ProjectIndexStatus IndexStatus,
        DateTimeOffset? IndexedAt,
        string? IndexError,
        int NodeCount,
        int EdgeCount,
        DateTimeOffset CreatedAt,
        string? VcsType,
        string? CurrentRef,
        string? Revision,
        string? RepositoryPath,
        bool? Dirty,
        string? IndexManifestVersion,
        int PendingFileCount);

    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapGet("/", ListProjects);
        group.MapPost("/", CreateProject);
        group.MapPost("/import", ImportProject);
        group.MapGet("/import-progress/{operationId}", GetImportProgress);
        group.MapGet("/{id}/vcs", GetVcsBinding);
        group.MapDelete("/{id}", DeleteProject);
        group.MapPost("/{id}/index", StartIndex);
        group.MapPost("/{id}/index/incremental", IncrementalIndex);
        group.MapGet("/{id}/index/progress", GetProgress);
        group.MapGet("/{id}/summaries/progress", GetSummaryProgress);
        group.MapGet("/{id}/graph/schema", GetGraphSchema);
        group.MapGet("/{id}/graph", GetGraph);
        group.MapPost("/{id}/graph/view", GetGraphView);
        group.MapPost("/{id}/graph/search", SearchGraph);
        group.MapPost("/{id}/graph/query", QueryGraph);
        group.MapPost("/{id}/graph/neighbors", ExpandGraphNeighbors);

        return app;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListProjects(
        IProjectRepository repo,
        IVcsStateRepository vcsState,
        IGitClient git,
        ISvnClient svn,
        CancellationToken ct)
    {
        var result = new List<ProjectListItem>();
        foreach (var project in await repo.ListAsync(ct))
        {
            var binding = await vcsState.GetBindingAsync(project.Id, ct);
            bool? dirty = null;
            if (binding is not null)
            {
                try
                {
                    if (binding.VcsType == VcsType.Git)
                    {
                        var status = await git.StatusAsync(project.RootPath, ct);
                        dirty = status.Success && status.Output.Split('\n').Any(line =>
                            !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));
                    }
                    else
                    {
                        var status = await svn.StatusAsync(project.RootPath, ct);
                        dirty = status.Success && status.Output.Contains("<entry", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    dirty = null;
                }
            }
            result.Add(new ProjectListItem(
                project.Id,
                project.Name,
                project.RootPath,
                project.Languages,
                project.IndexStatus,
                project.IndexedAt,
                project.IndexError,
                project.NodeCount,
                project.EdgeCount,
                project.CreatedAt,
                binding?.VcsType.ToString().ToLowerInvariant(),
                binding?.CurrentRef,
                binding?.Revision,
                binding?.RepositoryPath,
                dirty,
                project.IndexManifestVersion,
                project.PendingFileCount));
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateProject(
        CreateProjectRequest request,
        IProjectRepository repo,
        IGraphIndexWatcherRegistry watcherRegistry,
        CancellationToken ct)
    {
        if (!Directory.Exists(request.RootPath))
            return Results.BadRequest(new { error = $"目錄不存在: {request.RootPath}" });

        var project = new ProjectEntity
        {
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? Path.GetFileName(request.RootPath.TrimEnd('\\', '/'))
                : request.Name,
            RootPath = request.RootPath,
        };
        await repo.SaveAsync(project, ct);
        // SQLite 寫入成功後立即開始監看，不再等待背景服務下一次輪詢。
        watcherRegistry.RegisterProject(project);
        return Results.Ok(project);
    }

    private static async Task<IResult> ImportProject(
        ImportProjectRequest request,
        IProjectRepository projects,
        IVcsStateRepository vcsState,
        IGitClient git,
        ISvnClient svn,
        IProjectImportProgressStore importProgress,
        ISensitiveDataRedactor redactor,
        IGraphIndexWatcherRegistry watcherRegistry,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationPath) ||
            string.IsNullOrWhiteSpace(request.RepositoryUrl) ||
            string.IsNullOrWhiteSpace(request.ProfileId))
        {
            return Results.BadRequest(new { error = "Profile, repository URL, and destination are required." });
        }

        var sourceType = request.SourceType.Trim().ToLowerInvariant();
        if (sourceType is not ("git" or "svn"))
            return Results.BadRequest(new { error = "sourceType must be git or svn." });

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId.Trim();
        if (operationId.Length > 80 || operationId.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            return Results.BadRequest(new { error = "Invalid operationId." });

        importProgress.Begin(operationId, sourceType);
        try
        {
            var destination = Path.GetFullPath(request.DestinationPath);
            ValueTask Report(AgentService.Application.Models.ProcessOutputLine line, CancellationToken _) 
            {
                importProgress.Report(operationId, line.IsError, redactor.Redact(line.Text));
                return ValueTask.CompletedTask;
            }

            VcsType vcsType;
            string? revision;
            if (sourceType == "git")
            {
                var branch = string.IsNullOrWhiteSpace(request.Ref) ? "main" : request.Ref;
                var result = await git.CloneAsync(
                    request.ProfileId,
                    request.RepositoryUrl,
                    branch,
                    destination,
                    ct,
                    Report);
                if (!result.Success)
                {
                    importProgress.Fail(operationId, redactor.Redact(result.Error ?? "Git clone failed."));
                    return Results.BadRequest(result with { Error = redactor.Redact(result.Error ?? "Git clone failed.") });
                }
                var status = await git.StatusAsync(destination, ct);
                revision = status.Output.Split('\n')
                    .FirstOrDefault(line => line.StartsWith("# branch.oid ", StringComparison.Ordinal))?[13..]
                    .Trim();
                vcsType = VcsType.Git;
            }
            else
            {
                var result = await svn.CheckoutAsync(
                    request.ProfileId,
                    request.RepositoryUrl,
                    destination,
                    ct,
                    Report);
                if (!result.Success)
                {
                    importProgress.Fail(operationId, redactor.Redact(result.Error ?? "SVN checkout failed."));
                    return Results.BadRequest(result with { Error = redactor.Redact(result.Error ?? "SVN checkout failed.") });
                }
                var current = await svn.GetRevisionAsync(request.ProfileId, destination, ct);
                revision = current.Success ? current.Output.Trim() : result.Revision;
                vcsType = VcsType.Svn;
            }

            var project = new ProjectEntity
            {
                Name = string.IsNullOrWhiteSpace(request.Name)
                    ? Path.GetFileName(destination.TrimEnd('\\', '/'))
                    : request.Name,
                RootPath = destination,
            };
            await projects.SaveAsync(project, ct);
            // Clone/checkout 與專案資料列均完成後，立即註冊實際 destination。
            watcherRegistry.RegisterProject(project);
            await vcsState.SaveBindingAsync(new ProjectVcsBinding
            {
                ProjectId = project.Id,
                VcsType = vcsType,
                ConnectionProfileId = request.ProfileId,
                RepositoryUrl = request.RepositoryUrl,
                RepositoryPath = destination,
                CurrentRef = request.Ref,
                Revision = revision,
            }, ct);
            importProgress.Complete(operationId, "專案取得完成，正在建立索引...");
            return Results.Ok(project);
        }
        catch (OperationCanceledException)
        {
            importProgress.Cancel(operationId);
            throw;
        }
        catch (Exception ex)
        {
            importProgress.Fail(operationId, redactor.Redact(ex.Message));
            throw;
        }
    }

    private static IResult GetImportProgress(string operationId, IProjectImportProgressStore progress) =>
        progress.Get(operationId) is { } current ? Results.Ok(current) : Results.NotFound();

    private static async Task<IResult> GetVcsBinding(
        string id,
        IVcsStateRepository vcsState,
        CancellationToken ct)
    {
        var binding = await vcsState.GetBindingAsync(id, ct);
        return binding is null ? Results.NoContent() : Results.Ok(binding);
    }

    private static async Task<IResult> DeleteProject(
        string id,
        IProjectRepository repo,
        IGraphStore graphStore,
        GraphIndexingService indexService,
        IGraphIndexWatcherRegistry watcherRegistry,
        CancellationToken ct)
    {
        try
        {
            await graphStore.DeleteProjectAsync(id, ct);
        }
        catch
        {
            // Neo4j 未啟動時仍允許移除專案記錄
        }
        indexService.ForgetProjectState(id);
        await repo.DeleteAsync(id, ct);
        // 先完成持久化刪除，再解除監看；若 SQLite 刪除失敗，仍保留原有 Watcher。
        watcherRegistry.UnregisterProject(id);
        return Results.NoContent();
    }

    private static async Task<IResult> StartIndex(
        string id,
        GraphIndexingService indexService,
        IProjectJobQueue executionQueue,
        CancellationToken ct)
    {
        await executionQueue.EnqueueAsync(
            workerCt => indexService.IndexProjectAsync(id, workerCt),
            ct);
        return Results.Accepted($"/api/projects/{id}/index/progress");
    }

    private static async Task<IResult> IncrementalIndex(
        string id, GraphIndexingService indexService, CancellationToken ct)
    {
        var project = await indexService.IncrementalIndexAsync(id, ct);
        return Results.Ok(new { changed = project is not null, project });
    }

    private static IResult GetProgress(string id, GraphIndexingService indexService)
    {
        var progress = indexService.GetProgress(id);
        return progress is null
            ? Results.Ok(new { phase = "idle", message = "", percent = 0 })
            : Results.Ok(progress);
    }

    private static IResult GetSummaryProgress(string id, GraphCommunityAiService communityAi) =>
        Results.Ok(communityAi.GetProgress(id));

    private static async Task<IResult> GetGraphSchema(
        string id,
        IProjectRepository repo,
        IGraphStore graphStore,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        var readiness = await EnsureProjectGraphReadyAsync(
            id, repo, graphStore, neo4jLifecycle, ct);
        if (readiness is not null) return readiness;

        try
        {
            return Results.Ok(await graphStore.GetVisualSchemaAsync(id, ct));
        }
        catch (ServiceUnavailableException)
        {
            return GraphUnavailable(neo4jLifecycle);
        }
    }

    private static async Task<IResult> GetGraph(
        string id,
        int? limit,
        string? kinds,
        string? relations,
        IProjectRepository repo,
        IGraphStore graphStore,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        var readiness = await EnsureProjectGraphReadyAsync(
            id, repo, graphStore, neo4jLifecycle, ct);
        if (readiness is not null) return readiness;

        try
        {
            var graph = await graphStore.GetVisualGraphAsync(
                id,
                limit ?? 1000,
                SplitCsv(kinds),
                SplitCsv(relations),
                ct);
            return Results.Ok(graph);
        }
        catch (ArgumentException ex)
        {
            // NodeKind 與 relationship filter 都採固定白名單；不合法值是請求錯誤，
            // 不應被包成難以判讀的伺服器 500。
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ServiceUnavailableException)
        {
            return GraphUnavailable(neo4jLifecycle);
        }
    }

    /// <summary>
    /// Viewer Contract 初始圖入口。保留既有 GET /graph，不讓舊版桌面端或驗收腳本失效。
    /// </summary>
    private static async Task<IResult> GetGraphView(
        string id,
        GraphViewRequest request,
        IProjectRepository repo,
        IGraphStore graphStore,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        var readiness = await EnsureProjectGraphReadyAsync(
            id, repo, graphStore, neo4jLifecycle, ct);
        if (readiness is not null) return readiness;

        try
        {
            return Results.Ok(await graphStore.GetViewerGraphAsync(
                id,
                request.Limit ?? 1000,
                request.Filters,
                ct));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ServiceUnavailableException)
        {
            return GraphUnavailable(neo4jLifecycle);
        }
    }

    /// <summary>
    /// Viewer Contract 全域搜尋入口。搜尋只使用 active V4 full-text index，
    /// 不會把任意 Cypher 或未驗證 node id 交給 Neo4j。
    /// </summary>
    private static async Task<IResult> SearchGraph(
        string id,
        GraphSearchRequest request,
        IProjectRepository repo,
        IGraphStore graphStore,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Results.BadRequest(new { error = "搜尋文字不可為空白。" });

        var readiness = await EnsureProjectGraphReadyAsync(
            id, repo, graphStore, neo4jLifecycle, ct);
        if (readiness is not null) return readiness;

        try
        {
            return Results.Ok(await graphStore.SearchVisualGraphAsync(
                id,
                request.Query,
                request.Take ?? 20,
                request.Filters,
                ct));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ServiceUnavailableException)
        {
            return GraphUnavailable(neo4jLifecycle);
        }
    }

    private static async Task<IResult> QueryGraph(
        string id,
        GraphQueryRequest request,
        IProjectRepository repo,
        IGraphStore graphStore,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        var readiness = await EnsureProjectGraphReadyAsync(
            id, repo, graphStore, neo4jLifecycle, ct);
        if (readiness is not null) return readiness;

        try
        {
            return Results.Ok(await graphStore.QueryVisualGraphAsync(
                id,
                request.Cypher,
                request.Limit ?? 1000,
                ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ClientException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ServiceUnavailableException)
        {
            return GraphUnavailable(neo4jLifecycle);
        }
    }

    private static async Task<IResult> ExpandGraphNeighbors(
        string id,
        GraphNeighborsRequest request,
        IProjectRepository repo,
        IGraphStore graphStore,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        var readiness = await EnsureProjectGraphReadyAsync(
            id, repo, graphStore, neo4jLifecycle, ct);
        if (readiness is not null) return readiness;

        try
        {
            return Results.Ok(await graphStore.GetVisualNeighborsAsync(
                id,
                request.NodeKeys,
                request.Depth ?? 1,
                request.Limit ?? 1000,
                request.Mode ?? "all",
                ct));
        }
        catch (ArgumentException ex)
        {
            // 展開模式與節點參數由 API 契約限制，錯誤輸入明確回傳 400。
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ServiceUnavailableException)
        {
            return GraphUnavailable(neo4jLifecycle);
        }
    }

    private static async Task<bool> EnsureGraphAvailableAsync(
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct) =>
        await neo4jLifecycle.EnsureAvailableAsync(null, ct);

    private static async Task<IResult?> EnsureProjectGraphReadyAsync(
        string id,
        IProjectRepository repo,
        IGraphStore graphStore,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        var project = await repo.GetAsync(id, ct);
        if (project is null)
            return Results.NotFound();

        if (project.IndexManifestVersion is null)
            return Results.Json(
                new { error = "此專案尚未有可用的成功索引，請先完成索引後再查看知識圖譜。" },
                statusCode: StatusCodes.Status409Conflict);

        if (!await EnsureGraphAvailableAsync(neo4jLifecycle, ct))
            return GraphUnavailable(neo4jLifecycle);

        // SQLite 專案紀錄與 Neo4j active graph 必須指向同一個 manifest。
        // 若 Neo4j 曾被清空、資料遺失或只留下舊版 graph，不能回傳看似正常的空圖，
        // 而要明確要求重新索引，避免使用者誤以為索引仍可用。
        string? activeManifest;
        try
        {
            activeManifest = await graphStore.GetActiveManifestAsync(id, ct);
        }
        catch (ServiceUnavailableException)
        {
            // 健康檢查與實際讀取之間仍可能發生短暫斷線，統一回傳既有的
            // Neo4j 503 診斷，避免未處理例外變成不具體的 500。
            return GraphUnavailable(neo4jLifecycle);
        }
        if (!HasMatchingGraphManifest(project.IndexManifestVersion, activeManifest))
        {
            return Results.Json(
                new
                {
                    error = "專案索引紀錄與 Neo4j 知識圖譜不一致，請重新執行索引。",
                    errorCode = "graph_manifest_mismatch",
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        return null;
    }

    /// <summary>
    /// 判斷 SQLite 專案紀錄與 Neo4j active graph 是否屬於同一次成功索引。
    /// 保持成無副作用的小函式，讓缺少、相同與版本不一致三種情況都能直接測試。
    /// </summary>
    internal static bool HasMatchingGraphManifest(
        string? projectManifest,
        string? activeManifest) =>
        !string.IsNullOrWhiteSpace(projectManifest) &&
        string.Equals(projectManifest, activeManifest, StringComparison.Ordinal);

    private static IReadOnlyList<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static IResult GraphUnavailable(INeo4jRuntime neo4jLifecycle) =>
        Results.Json(
            new
            {
                error = neo4jLifecycle.LastError ??
                    "Neo4j 圖譜資料庫目前無法連線，專案解析查詢無法執行。請確認 Wingman 管理的 Neo4j 已啟動。",
                errorCode = neo4jLifecycle.Status switch
                {
                    "port-conflict" => "neo4j_port_conflict",
                    "start-failed" or "install-failed" => "neo4j_start_failed",
                    "invalid-configuration" => "neo4j_invalid_configuration",
                    _ => "neo4j_unreachable",
                },
                neo4jStatus = neo4jLifecycle.Status,
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
