using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.ChangeIntelligence;
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
/// POST   /api/projects/{id}/summaries        → 建立社群摘要（GraphRAG 索引期）
/// POST   /api/projects/{id}/query            → GraphRAG 問答（auto/global/local）
/// POST   /api/projects/{id}/impact           → Impact Analysis（不改A壞B）
/// GET    /api/projects/{id}/repomap          → Repo Map
/// POST   /api/projects/{id}/agents-md        → 生成 AGENTS.md
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
    public sealed record QueryRequest(
        string Question,
        string? Mode,
        string? ProviderProfileId = null,
        string? ModelId = null,
        string? AgentMode = null,
        IReadOnlyList<ChangeTarget>? Targets = null,
        string? AnalysisSessionId = null,
        IReadOnlyList<ClarificationAnswer>? ClarificationAnswers = null);
    public sealed record ImpactRequest(string Symbol, int? MaxDepth);
    public sealed record GraphQueryRequest(string Cypher, int? Limit);
    public sealed record GraphNeighborsRequest(
        IReadOnlyList<string> NodeKeys,
        int? Depth,
        int? Limit,
        string? Mode);
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
        group.MapPost("/{id}/summaries", BuildSummaries);
        group.MapGet("/{id}/summaries/progress", GetSummaryProgress);
        group.MapPost("/{id}/query", Query);
        group.MapPost("/{id}/impact", Impact);
        group.MapGet("/{id}/repomap", GetRepoMap);
        group.MapGet("/{id}/graph/schema", GetGraphSchema);
        group.MapGet("/{id}/graph", GetGraph);
        group.MapPost("/{id}/graph/query", QueryGraph);
        group.MapPost("/{id}/graph/neighbors", ExpandGraphNeighbors);
        group.MapPost("/{id}/agents-md", GenerateAgentsMd);

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
        CreateProjectRequest request, IProjectRepository repo, CancellationToken ct)
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
        return Results.NoContent();
    }

    private static async Task<IResult> StartIndex(
        string id,
        GraphIndexingService indexService,
        IRunExecutionQueue executionQueue,
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

    private static async Task<IResult> BuildSummaries(
        string id,
        GraphRetrievalService graphRag,
        INeo4jRuntime neo4jLifecycle,
        IRunExecutionQueue executionQueue,
        CancellationToken ct)
    {
        if (!await EnsureGraphAvailableAsync(neo4jLifecycle, ct))
            return GraphUnavailable(neo4jLifecycle);

        await executionQueue.EnqueueAsync(async workerCt =>
        {
            await graphRag.BuildCommunitySummariesAsync(id, null, workerCt);
        }, ct);
        return Results.Accepted($"/api/projects/{id}/summaries/progress");
    }

    private static IResult GetSummaryProgress(string id, GraphRetrievalService graphRag) =>
        Results.Ok(graphRag.GetEnrichmentStatus(id));

    private static async Task<IResult> Query(
        string id,
        QueryRequest request,
        IProjectRepository projects,
        IContextAssembler contextAssembler,
        IRunRepository runs,
        GraphRetrievalService graphRag,
        IChangeAnalysisSessionService sessionService,
        IChangeImplementationPlanBuilder implementationPlanBuilder,
        ProjectEvidencePlanner evidencePlanner,
        GraphIndexingService indexService,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        var project=await projects.GetAsync(id,ct);if(project is null)return Results.NotFound();
        if (!await EnsureGraphAvailableAsync(neo4jLifecycle, ct))
            return GraphUnavailable(neo4jLifecycle);
        if (await indexService.CatchUpAsync(id, ct))
            project = await indexService.IndexProjectAsync(id, ct);
        else
            project=await projects.GetAsync(id,ct) ?? project;
        if (project.IndexManifestVersion is null)
            return Results.Json(new { error = "此專案尚未有可用的成功索引，請先完成索引。" }, statusCode: StatusCodes.Status409Conflict);
        var run=new RunEntity{SessionId=$"project:{id}",UserMessage=request.Question,ProviderProfileId=request.ProviderProfileId,ResolvedModelId=request.ModelId,WorkspacePath=project.RootPath,ProjectId=id,Mode=ParseAgentMode(request.AgentMode),WorkspaceStrategy=WorkspaceStrategy.Direct,Status=RunStatus.Running,StartedAt=DateTimeOffset.UtcNow};await runs.SaveAsync(run,ct);
        try
        {
            var analysisSession = await sessionService.StartOrContinueAsync(
                id,
                request.Question,
                request.Targets,
                request.AnalysisSessionId,
                request.ClarificationAnswers,
                ct);
            var changeBrief = analysisSession.Brief;
            var clarificationQuestions = analysisSession.PendingQuestions;
            var evidencePack = await evidencePlanner.BuildAsync(project, changeBrief, ct);
            var implementationPlan = implementationPlanBuilder.Build(analysisSession, evidencePack);
            if (analysisSession.Status == ChangeAnalysisSessionStatus.AwaitingClarification)
            {
                var clarificationAnswer = BuildClarificationResponse(clarificationQuestions);
                run.Status=RunStatus.Completed;run.EndedAt=DateTimeOffset.UtcNow;await runs.SaveAsync(run,CancellationToken.None);
                return Results.Ok(new
                {
                    answer=clarificationAnswer,
                    runId=run.Id,
                    analysisSessionId=analysisSession.Id,
                    requiresClarification=true,
                    changeBrief,
                    clarificationQuestions,
                    evidencePack,
                    implementationPlan,
                });
            }
            var question=(await contextAssembler.AssembleAsync(request.Question,project.RootPath,ct,run.Id)).Prompt
                + ProjectEvidencePlanner.FormatForPrompt(evidencePack)
                + FormatPlanForPrompt(implementationPlan);
            var answer = request.Mode?.ToLowerInvariant() switch
            {
                "global" => await graphRag.AnswerGlobalAsync(
                    id,
                    question,
                    ct,
                    providerProfileId: request.ProviderProfileId,
                    modelId: request.ModelId),
                "local" => await graphRag.AnswerLocalAsync(
                    id,
                    question,
                    ct,
                    providerProfileId: request.ProviderProfileId,
                    modelId: request.ModelId),
                _ => await graphRag.AnswerAsync(
                    id,
                    question,
                    ct,
                    providerProfileId: request.ProviderProfileId,
                    modelId: request.ModelId),
            };
            await sessionService.CompleteAsync(analysisSession.Id, CancellationToken.None);
            run.Status=RunStatus.Completed;run.EndedAt=DateTimeOffset.UtcNow;await runs.SaveAsync(run,CancellationToken.None);return Results.Ok(new { answer,runId=run.Id,analysisSessionId=analysisSession.Id,requiresClarification=false,changeBrief,clarificationQuestions,evidencePack,implementationPlan });
        }
        catch (ServiceUnavailableException)
        {
            run.Status=RunStatus.Failed;run.Error="Knowledge graph is unavailable.";run.EndedAt=DateTimeOffset.UtcNow;await runs.SaveAsync(run,CancellationToken.None);
            return GraphUnavailable(neo4jLifecycle);
        }
        catch(Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            run.Status=RunStatus.Failed;run.Error=ex.Message;run.EndedAt=DateTimeOffset.UtcNow;await runs.SaveAsync(run,CancellationToken.None);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch(Exception ex)when(ex is not OperationCanceledException){run.Status=RunStatus.Failed;run.Error=ex.Message;run.EndedAt=DateTimeOffset.UtcNow;await runs.SaveAsync(run,CancellationToken.None);throw;}
    }

    private static string BuildClarificationResponse(IReadOnlyList<ClarificationQuestion> questions)
    {
        var blocking = questions.Where(question => question.IsBlocking).OrderBy(question => question.Priority).ToList();
        var lines = new List<string>
        {
            "目前資訊不足，先確認下列會改變設計或風險判斷的問題，再產出正式變更計畫：",
        };
        lines.AddRange(blocking.Select((question, index) => $"{index + 1}. {question.Question}\n   影響：{question.DecisionImpact}"));
        return string.Join("\n", lines);
    }

    private static string FormatPlanForPrompt(ChangeImplementationPlan plan)
    {
        var lines = new List<string>
        {
            "\n## Deterministic Change Plan",
            $"Status: {plan.Status}",
            "Answer in these sections: conclusion and change points; direct/indirect impact and risk; clickable evidence; implementation order; tests and acceptance; verified/inferred/unknown and freshness.",
            "Do not promote this plan beyond the confidence of its cited evidence.",
        };
        lines.AddRange(plan.ModificationSteps.Select(step => $"- Step {step.Order}: {step.Target} — {step.Action} [{step.Confidence}]"));
        lines.AddRange(plan.Risks.Select(risk => $"- Risk: {risk}"));
        lines.AddRange(plan.Tests.Select(test => $"- Test ({test.Kind}): {test.Description}"));
        lines.AddRange(plan.AcceptanceCriteria.Select(item => $"- Acceptance: {item}"));
        return string.Join("\n", lines);
    }

    private static AgentMode ParseAgentMode(string? value)=>value?.Trim().ToLowerInvariant() switch{"ask"=>AgentMode.Ask,"auto"=>AgentMode.Auto,"full_auto" or "fullauto"=>AgentMode.FullAuto,_=>AgentMode.Plan};

    private static async Task<IResult> Impact(
        string id,
        ImpactRequest request,
        GraphRetrievalService impactService,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        if (!await EnsureGraphAvailableAsync(neo4jLifecycle, ct))
            return GraphUnavailable(neo4jLifecycle);

        var result = await impactService.AnalyzeImpactAsync(id, request.Symbol, request.MaxDepth ?? 3, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetRepoMap(
        string id,
        GraphRetrievalService repoMapService,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        if (!await EnsureGraphAvailableAsync(neo4jLifecycle, ct))
            return GraphUnavailable(neo4jLifecycle);

        var map = await repoMapService.GenerateRepoMapAsync(id, 1024, ct);
        return Results.Ok(new { map });
    }

    private static async Task<IResult> GetGraphSchema(
        string id,
        IProjectRepository repo,
        IGraphStore graphStore,
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct)
    {
        var readiness = await EnsureProjectGraphReadyAsync(id, repo, neo4jLifecycle, ct);
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
        var readiness = await EnsureProjectGraphReadyAsync(id, repo, neo4jLifecycle, ct);
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
        var readiness = await EnsureProjectGraphReadyAsync(id, repo, neo4jLifecycle, ct);
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
        var readiness = await EnsureProjectGraphReadyAsync(id, repo, neo4jLifecycle, ct);
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
        catch (ServiceUnavailableException)
        {
            return GraphUnavailable(neo4jLifecycle);
        }
    }

    private static async Task<IResult> GenerateAgentsMd(
        string id, IProjectRepository repo, GraphRetrievalService generator, CancellationToken ct)
    {
        var project = await repo.GetAsync(id, ct);
        if (project is null)
            return Results.NotFound();
        var content = await generator.GenerateAgentsMdAsync(id, project.RootPath, ct);
        return Results.Ok(new { content });
    }

    private static async Task<bool> EnsureGraphAvailableAsync(
        INeo4jRuntime neo4jLifecycle,
        CancellationToken ct) =>
        await neo4jLifecycle.EnsureAvailableAsync(null, ct);

    private static async Task<IResult?> EnsureProjectGraphReadyAsync(
        string id,
        IProjectRepository repo,
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

        return await EnsureGraphAvailableAsync(neo4jLifecycle, ct)
            ? null
            : GraphUnavailable(neo4jLifecycle);
    }

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
