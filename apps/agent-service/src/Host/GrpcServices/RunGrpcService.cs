using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace AgentService.Host.GrpcServices;

/// <summary>
/// Run gRPC 服務實作。
///
/// 通訊路徑：React UI → Tauri IPC invoke → Rust gRPC Client → RunGrpcService → RunOrchestrator
/// 事件路徑：RunOrchestrator → IRunEventBus → RunGrpcService.StreamRunEvents → Rust → Tauri emit → 前端 listen()
/// </summary>
public sealed class RunGrpcService : RunService.RunServiceBase
{
    private readonly IRunOrchestrator _orchestrator;
    private readonly IRunEventBus _eventBus;
    private readonly ILogger<RunGrpcService> _logger;

    public RunGrpcService(
        IRunOrchestrator orchestrator,
        IRunEventBus eventBus,
        ILogger<RunGrpcService> logger)
    {
        _orchestrator = orchestrator;
        _eventBus = eventBus;
        _logger = logger;
    }

    // ─── CreateRun ───────────────────────────────────────────────────────────

    public override async Task<CreateRunResponse> CreateRun(
        CreateRunRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation(
            "CreateRun: sessionId={SessionId}, profile={ProfileId}, workspace={Workspace}",
            request.SessionId, request.ProviderProfileId, request.WorkspacePath);

        var command = new CreateRunCommand(
            SessionId: request.SessionId,
            UserMessage: request.UserMessage,
            ProviderProfileId: string.IsNullOrWhiteSpace(request.ProviderProfileId)
                ? null : request.ProviderProfileId,
            WorkspacePath: string.IsNullOrWhiteSpace(request.WorkspacePath)
                ? null : request.WorkspacePath,
            ProjectId: string.IsNullOrWhiteSpace(request.ProjectId)
                ? null : request.ProjectId,
            Mode: ParseMode(request.AgentMode),
            WorkspaceStrategy: ParseWorkspaceStrategy(request.WorkspaceStrategy)
        );

        var run = await _orchestrator.StartRunAsync(command, context.CancellationToken);

        return new CreateRunResponse { RunId = run.Id };
    }

    // ─── CancelRun ──────────────────────────────────────────────────────────

    public override async Task<CancelRunResponse> CancelRun(
        CancelRunRequest request,
        ServerCallContext context)
    {
        await _orchestrator.CancelRunAsync(request.RunId, context.CancellationToken);
        return new CancelRunResponse { Success = true };
    }

    // ─── GetRun ─────────────────────────────────────────────────────────────

    public override Task<GetRunResponse> GetRun(
        GetRunRequest request,
        ServerCallContext context)
    {
        var run = _orchestrator.GetRun(request.RunId);

        if (run is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Run {request.RunId} 不存在"));
        }

        return Task.FromResult(new GetRunResponse
        {
            RunId = run.Id,
            Status = MapStatus(run.Status),
            Error = run.Error ?? "",
            CreatedAt = run.CreatedAt.ToString("O"),
            StartedAt = run.StartedAt?.ToString("O") ?? "",
            EndedAt = run.EndedAt?.ToString("O") ?? "",
            AgentMode = ToWireValue(run.Mode),
            WorkspaceStrategy = ToWireValue(run.WorkspaceStrategy),
            ProjectId = run.ProjectId ?? "",
        });
    }

    // ─── StreamRunEvents ────────────────────────────────────────────────────

    public override async Task StreamRunEvents(
        StreamRunEventsRequest request,
        IServerStreamWriter<RunEvent> responseStream,
        ServerCallContext context)
    {
        var runId = request.RunId;
        _logger.LogDebug("StreamRunEvents 訂閱 run {RunId}", runId);

        // 取得 ChannelReader（由 RunEventBus.Subscribe 在 StartRunAsync 時預先建立）
        var reader = _eventBus.Subscribe(runId);

        try
        {
            await foreach (var evt in reader.ReadAllAsync(context.CancellationToken))
            {
                var grpcEvent = new RunEvent
                {
                    RunId = evt.RunId,
                    EventType = evt.EventType,
                    Timestamp = evt.Timestamp.ToString("O"),
                    PayloadJson = evt.PayloadJson,
                };

                await responseStream.WriteAsync(grpcEvent, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // gRPC 客戶端中斷連線（正常情況）
            _logger.LogDebug("StreamRunEvents for run {RunId} 已取消", runId);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string MapStatus(RunStatus status) => status switch
    {
        RunStatus.Created => "created",
        RunStatus.Running => "running",
        RunStatus.WaitingApproval => "waiting_approval",
        RunStatus.Paused => "paused",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        _ => "unknown",
    };

    private static AgentMode ParseMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "ask" => AgentMode.Ask,
        "auto" => AgentMode.Auto,
        "full_auto" or "fullauto" => AgentMode.FullAuto,
        _ => AgentMode.Plan,
    };

    private static WorkspaceStrategy ParseWorkspaceStrategy(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "git_worktree" or "gitworktree" => WorkspaceStrategy.GitWorktree,
            "svn_shadow_git" or "svnshadowgit" => WorkspaceStrategy.SvnShadowGit,
            "snapshot" => WorkspaceStrategy.Snapshot,
            _ => WorkspaceStrategy.Direct,
        };

    private static string ToWireValue(AgentMode mode) => mode switch
    {
        AgentMode.Ask => "ask",
        AgentMode.Plan => "plan",
        AgentMode.Auto => "auto",
        AgentMode.FullAuto => "full_auto",
        _ => "plan",
    };

    private static string ToWireValue(WorkspaceStrategy strategy) => strategy switch
    {
        WorkspaceStrategy.GitWorktree => "git_worktree",
        WorkspaceStrategy.SvnShadowGit => "svn_shadow_git",
        WorkspaceStrategy.Snapshot => "snapshot",
        _ => "direct",
    };
}
