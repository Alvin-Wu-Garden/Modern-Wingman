using System.Diagnostics;
using AgentService.Application.Contracts;
using AgentService.Modules.GraphRAG;

namespace AgentService.Host.RestEndpoints;

/// <summary>
/// 提供 FBL Graph 索引與檢索的唯讀診斷端點。
/// 回應只包含版本、數量、節點 key/type 與分數，不回傳原始碼、連線字串或 Evidence 內容。
/// </summary>
public static class ProjectIndexDiagnosticsEndpoints
{
    /// <summary>完整 Context Pack 效能量測輸入。</summary>
    public sealed record RetrievalDiagnosticsRequest(string Question);

    /// <summary>種子搜尋診斷輸入。</summary>
    public sealed record SeedDiagnosticsRequest(string Question, int? Limit = null);

    /// <summary>去敏感後的圖譜種子。</summary>
    public sealed record SeedDiagnosticsHit(
        int Rank,
        string Id,
        string Kind,
        string Role,
        double Score);

    /// <summary>註冊本機驗收所需的 FBL 診斷 API。</summary>
    public static IEndpointRouteBuilder MapProjectIndexDiagnosticsEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");
        group.MapGet("/{id}/index/manifest", GetDiagnostics);
        group.MapGet("/{id}/index/run", GetRun);
        group.MapPost("/{id}/index/catch-up", CatchUp);
        group.MapPost("/{id}/retrieval/diagnostics", MeasureRetrieval);
        group.MapPost("/{id}/retrieval/seed-diagnostics", MeasureSeeds);
        group.MapGet("/{id}/community/acceptance-diagnostics", GetCommunityDiagnostics);
        group.MapGet("/{id}/storage/acceptance-diagnostics", GetStorageDiagnostics);
        return app;
    }

    /// <summary>回傳目前索引 manifest、最近 attempt 與 pending 檔案。</summary>
    private static async Task<IResult> GetDiagnostics(
        string id,
        IProjectRepository projects,
        GraphIndexingService indexing,
        CancellationToken ct)
    {
        if (await projects.GetAsync(id, ct) is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(await indexing.GetDiagnosticsAsync(id, ct));
    }

    /// <summary>回傳最近一次 full 或 no-op 的去敏感執行統計。</summary>
    private static async Task<IResult> GetRun(
        string id,
        string? mode,
        IProjectRepository projects,
        GraphIndexingService indexing,
        CancellationToken ct)
    {
        if (await projects.GetAsync(id, ct) is null)
        {
            return Results.NotFound();
        }
        return indexing.GetLastRun(id, mode) is { } run
            ? Results.Ok(run)
            : Results.NotFound();
    }

    /// <summary>補做一次內容 hash 比對，但不直接修改原始碼或資料庫。</summary>
    private static async Task<IResult> CatchUp(
        string id,
        IProjectRepository projects,
        GraphIndexingService indexing,
        CancellationToken ct)
    {
        if (await projects.GetAsync(id, ct) is null)
        {
            return Results.NotFound();
        }
        var changed = await indexing.CatchUpAsync(id, ct);
        return Results.Ok(new
        {
            changed,
            diagnostics = await indexing.GetDiagnosticsAsync(id, ct),
        });
    }

    /// <summary>量測建立 FBL Context Pack 的時間與大小，不呼叫 LLM。</summary>
    private static async Task<IResult> MeasureRetrieval(
        string id,
        RetrievalDiagnosticsRequest request,
        IProjectRepository projects,
        GraphRetrievalService retrieval,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2_000)
        {
            return Results.BadRequest(new { error = "Question 必須介於 1 到 2000 個字元。" });
        }
        var project = await projects.GetAsync(id, ct);
        if (project is null)
        {
            return Results.NotFound();
        }
        var stopwatch = Stopwatch.StartNew();
        var prompt = await retrieval.BuildAnswerPromptAsync(
            id,
            project.RootPath,
            request.Question,
            ct,
            allowLlmQueryRewrite: false);
        stopwatch.Stop();
        return Results.Ok(new
        {
            elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            promptCharacters = prompt.Length,
        });
    }

    /// <summary>量測 active FBL Graph 的確定性多路 BM25 seed；不呼叫 LLM。</summary>
    private static async Task<IResult> MeasureSeeds(
        string id,
        SeedDiagnosticsRequest request,
        IProjectRepository projects,
        GraphRetrievalService retrieval,
        IGraphStore graphStore,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2_000)
        {
            return Results.BadRequest(new { error = "Question 必須介於 1 到 2000 個字元。" });
        }
        var project = await projects.GetAsync(id, ct);
        if (project is null)
        {
            return Results.NotFound();
        }
        var version = await graphStore.GetActiveManifestAsync(id, ct);
        if (string.IsNullOrWhiteSpace(version) || project.IndexManifestVersion != version)
        {
            return Results.Json(
                new { error = "目前沒有與專案 manifest 一致的 active FBL Graph。" },
                statusCode: StatusCodes.Status409Conflict);
        }
        var limit = Math.Clamp(request.Limit ?? 10, 1, 20);
        // 診斷端點使用與實際專案問答相同的確定性 Query Plan，
        // 但刻意不呼叫 LLM，讓結構驗收不受模型狀態與額外成本影響。
        var hits = await retrieval.SearchSeedCandidatesAsync(
            id,
            request.Question.Trim(),
            limit,
            ct);
        return Results.Ok(new
        {
            projectId = id,
            graphVersion = version,
            missingSeed = hits.Count == 0,
            hits = hits.Select((hit, index) => new SeedDiagnosticsHit(
                index + 1,
                hit.Node.Key,
                hit.Node.Kind.ToString(),
                StringProperty(hit.Node.Properties, "role"),
                hit.Score)),
        });
    }

    /// <summary>回傳 Community 模板與背景狀態的聚合，不下載成員內容。</summary>
    private static async Task<IResult> GetCommunityDiagnostics(
        string id,
        IProjectRepository projects,
        IGraphStore graphStore,
        GraphCommunityAiService communityAi,
        CancellationToken ct)
    {
        if (await projects.GetAsync(id, ct) is null)
        {
            return Results.NotFound();
        }
        var aggregate = await graphStore.GetCommunityAcceptanceDiagnosticsAsync(id, ct);
        return Results.Ok(new
        {
            aggregate,
            progress = communityAi.GetProgress(id),
        });
    }

    /// <summary>確認單一 Neo4j active 版本與 Project manifest 一致，不再檢查舊 SQLite Evidence。</summary>
    private static async Task<IResult> GetStorageDiagnostics(
        string id,
        IProjectRepository projects,
        IGraphStore graphStore,
        CancellationToken ct)
    {
        var project = await projects.GetAsync(id, ct);
        if (project is null)
        {
            return Results.NotFound();
        }
        var version = await graphStore.GetActiveManifestAsync(id, ct);
        var stats = string.IsNullOrWhiteSpace(version)
            ? (Nodes: 0, Edges: 0)
            : await graphStore.GetStatsAsync(id, ct);
        return Results.Ok(new
        {
            projectId = id,
            projectManifestVersion = project.IndexManifestVersion,
            activeGraphVersion = version,
            stats.Nodes,
            stats.Edges,
            storageStable = !string.IsNullOrWhiteSpace(version) &&
                            string.Equals(project.IndexManifestVersion, version, StringComparison.Ordinal),
            storageMode = "neo4j-authority-single-store",
        });
    }

    /// <summary>安全讀取 authority properties 的顯示字串。</summary>
    private static string StringProperty(
        IReadOnlyDictionary<string, object?> properties,
        string key) =>
        properties.TryGetValue(key, out var value) && value is not null
            ? value.ToString() ?? string.Empty
            : string.Empty;
}
