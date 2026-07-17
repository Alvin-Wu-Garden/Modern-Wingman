using System.Collections.Concurrent;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.Orchestration;

/// <summary>
/// Run 協調器（Orchestration Layer，Phase 1 實作）。
///
/// 架構設計：
///   每個 Run 對應一個 CopilotSession，由 copilot-sdk 內部的 Copilot CLI
///   執行完整的 agentic loop（規劃 → 工具呼叫 → 回應）。
///   此層負責：
///     1. 生命週期管理（建立 → 執行 → 完成/失敗/取消）
///     2. 將 CopilotSession 事件橋接為 RunStreamEvent 發布至 IRunEventBus
///     3. 支援 CancellationToken 驅動的取消機制
///
/// Phase 3 計畫：
///   此協調器的執行邏輯將被 MAF Workflow 包覆，
///   讓多步驟 orchestration（Planner → Executor → Verifier）可在 Workflow 層管理，
///   IRunOrchestrator 介面保持不變，前端/gRPC 層不需修改。
/// </summary>
public sealed class RunOrchestrator : IRunOrchestrator
{
    private readonly CopilotClientService _copilotClientService;
    private readonly ProviderConfigResolver _configResolver;
    private readonly IModelProviderService _providerService;
    private readonly IRunEventBus _eventBus;
    private readonly IRunRepository _runRepository;
    private readonly CopilotEventBridge _eventBridge;
    private readonly IChangeSetService _changeSetService;
    private readonly ILogger<RunOrchestrator> _logger;
    private readonly IRunExecutionQueue _executionQueue;
    private readonly IRunReplayGuard _replayGuard;
    private readonly IRunWorkspaceManager _workspaceManager;
    private readonly IAgentHookDispatcher _hooks;

    // in-memory 快取（活躍 run 的即時查詢；持久化由 IRunRepository 負責）
    private readonly ConcurrentDictionary<string, RunEntity> _runs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();

    public RunOrchestrator(
        CopilotClientService copilotClientService,
        ProviderConfigResolver configResolver,
        IModelProviderService providerService,
        IRunEventBus eventBus,
        IRunRepository runRepository,
        CopilotEventBridge eventBridge,
        IChangeSetService changeSetService,
        IRunExecutionQueue executionQueue,
        IRunReplayGuard replayGuard,
        IRunWorkspaceManager workspaceManager,
        IAgentHookDispatcher hooks,
        ILogger<RunOrchestrator> logger)
    {
        _copilotClientService = copilotClientService;
        _configResolver = configResolver;
        _providerService = providerService;
        _eventBus = eventBus;
        _runRepository = runRepository;
        _eventBridge = eventBridge;
        _changeSetService = changeSetService;
        _logger = logger;
        _executionQueue=executionQueue;
        _replayGuard=replayGuard;
        _workspaceManager=workspaceManager;
        _hooks=hooks;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public async Task<RunEntity> StartRunAsync(CreateRunCommand command, CancellationToken ct = default)
    {
        var run = new RunEntity
        {
            SessionId = command.SessionId,
            UserMessage = command.UserMessage,
            ProviderProfileId = command.ProviderProfileId,
            WorkspacePath = command.WorkspacePath,
            ProjectId = command.ProjectId,
            ParentRunId = command.ParentRunId,
            AgentRole = command.AgentRole,
            Mode = command.Mode,
            WorkspaceStrategy = command.WorkspaceStrategy,
            IncludeUncommittedChanges = command.IncludeUncommittedChanges,
        };

        _runs[run.Id] = run;
        await _runRepository.SaveAsync(run, ct);

        // 在 RunEventBus 預先建立 channel，確保早期事件不遺失
        _eventBus.Subscribe(run.Id);

        // 非同步執行（fire-and-forget with structured error handling）
        // 使用 Task.Run 讓 StartRunAsync 立刻回傳 runId 給 gRPC 呼叫者
        await _executionQueue.EnqueueAsync(workerCt => ExecuteRunAsync(run,command,workerCt,true),ct);

        return run;
    }

    public async Task CancelRunAsync(string runId, CancellationToken ct = default)
    {
        if (_activeCts.TryGetValue(runId, out var cts))
        {
            _logger.LogInformation("取消 run {RunId}", runId);
            cts.Cancel();
        }

        var run=await _runRepository.GetAsync(runId,ct);if(run is not null&&run.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)){run.Status=RunStatus.Cancelled;run.EndedAt=DateTimeOffset.UtcNow;await _runRepository.SaveAsync(run,ct);}
    }

    public async Task PauseRunAsync(string runId,CancellationToken ct=default)
    {
        var run=await _runRepository.GetAsync(runId,ct)??throw new KeyNotFoundException(runId);if(run.Status is not (RunStatus.Running or RunStatus.WaitingApproval or RunStatus.Created))throw new InvalidOperationException("Only active runs can be paused.");run.Status=RunStatus.Paused;run.Error="Paused by user.";await _runRepository.SaveAsync(run,ct);if(_activeCts.TryGetValue(runId,out var cts))cts.Cancel();
    }

    public async Task<RunEntity> ResumeRunAsync(string runId,CancellationToken ct=default)
    {
        var run=await _runRepository.GetAsync(runId,ct)??throw new KeyNotFoundException(runId);if(run.Status!=RunStatus.Paused)throw new InvalidOperationException("Only paused runs can be resumed.");return await RetryCoreAsync(run,null,ct);
    }

    public async Task<RunEntity> RetryRunAsync(string runId,string? providerProfileId=null,CancellationToken ct=default)
    {
        var run=await _runRepository.GetAsync(runId,ct)??throw new KeyNotFoundException(runId);if(run.Status is not (RunStatus.Failed or RunStatus.Paused))throw new InvalidOperationException("Only failed or paused runs can be retried.");return await RetryCoreAsync(run,providerProfileId,ct);
    }

    private async Task<RunEntity> RetryCoreAsync(RunEntity run,string? providerProfileId,CancellationToken ct)
    {
        await _replayGuard.EnsureReplayAllowedAsync(run.Id, ct);
        if(!string.IsNullOrWhiteSpace(providerProfileId))run.ProviderProfileId=providerProfileId;run.Status=RunStatus.Created;run.Error=null;run.EndedAt=null;_runs[run.Id]=run;await _runRepository.SaveAsync(run,ct);var command=new CreateRunCommand(run.SessionId,run.UserMessage,run.ProviderProfileId,run.WorkspacePath,run.ProjectId,run.Mode,run.WorkspaceStrategy,run.IncludeUncommittedChanges);await _executionQueue.EnqueueAsync(workerCt=>ExecuteRunAsync(run,command,workerCt,false),ct);return run;
    }

    public RunEntity? GetRun(string runId) =>
        _runs.TryGetValue(runId, out var run) ? run : null;

    // ─────────────────────────────────────────────────────────────────────────

    private async Task ExecuteRunAsync(RunEntity run, CreateRunCommand command, CancellationToken parentCt,bool createCheckpoint)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        _activeCts[run.Id] = cts;

        try
        {
            run.Status = RunStatus.Running;
            run.StartedAt = DateTimeOffset.UtcNow;
            await _runRepository.SaveAsync(run, cts.Token);

            _logger.LogInformation("Run {RunId} 開始執行，SessionId={SessionId}", run.Id, run.SessionId);
            await _eventBus.PublishAsync(RunStreamEvent.Started(run.Id), cts.Token);

            var prepared=await _workspaceManager.PrepareAsync(run,cts.Token);run.ExecutionWorkspacePath=prepared.Path;run.Branch=prepared.Branch;run.BaseRevision=prepared.BaseRevision;command=command with{WorkspacePath=prepared.Path};await _runRepository.SaveAsync(run,cts.Token);

            if (createCheckpoint && (command.Mode is AgentService.Domain.Models.AgentMode.Auto or
                                 AgentService.Domain.Models.AgentMode.FullAuto) &&
                !string.IsNullOrWhiteSpace(command.WorkspacePath) &&
                Directory.Exists(command.WorkspacePath))
            {
                run.CheckpointId = await _changeSetService.CreateCheckpointAsync(
                    run.Id,
                    command.WorkspacePath,
                    cts.Token);
                await _runRepository.SaveAsync(run, cts.Token);
            }

            // 取得 BYOK provider profile
            var profile = await _providerService.GetProfileAsync(command.ProviderProfileId, cts.Token);
            run.ProviderProfileId = profile.Id;
            run.ResolvedModelId = profile.ModelId;
            await _runRepository.SaveAsync(run, cts.Token);

            // 建構 SessionConfig（包含 BYOK ProviderConfig 與工作區 system prompt）
            var sessionConfig = await _configResolver.BuildSessionConfigAsync(
                profile,
                command.WorkspacePath,
                mode: command.Mode,
                runId: run.Id,
                ct: cts.Token);

            // 從單例 CopilotClientService 取得 client 並建立 session
            var client = _copilotClientService.GetClient();
            await using var session = await client.CreateSessionAsync(sessionConfig);

            _logger.LogDebug("Copilot session 建立完成，SessionId={CopilotSessionId}", session.SessionId);

            // ── 事件橋接 ──────────────────────────────────────────────────────
            var sessionCompleteTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var subscription = session.On<SessionEvent>(evt =>
                _eventBridge.Handle(run.Id, evt, sessionCompleteTcs));

            // 發送使用者訊息
            await session.SendAsync(new MessageOptions { Prompt = command.UserMessage });

            // 等待 session 閒置（SessionIdleEvent）或取消
            await sessionCompleteTcs.Task.WaitAsync(cts.Token);

            // 正常完成
            await _hooks.DispatchAsync(new(
                AgentHookStage.BeforeRunComplete,
                run.Id,
                WorkspacePath: run.ExecutionWorkspacePath ?? run.WorkspacePath), cts.Token);
            run.Status = RunStatus.Completed;
            run.EndedAt = DateTimeOffset.UtcNow;
            if (run.CheckpointId is not null)
            {
                var changeSet = await _changeSetService.GetChangeSetAsync(
                    run.CheckpointId,
                    cts.Token);
                await _eventBus.PublishAsync(
                    RunStreamEvent.ChangeSetAvailable(run.Id, changeSet),
                    cts.Token);
            }
            await _eventBus.PublishAsync(RunStreamEvent.Completed(run.Id));

            _logger.LogInformation("Run {RunId} 執行完成", run.Id);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            var persisted=await _runRepository.GetAsync(run.Id,CancellationToken.None);
            run.Status = persisted?.Status==RunStatus.Paused?RunStatus.Paused:RunStatus.Cancelled;
            run.EndedAt = DateTimeOffset.UtcNow;
            await _eventBus.PublishAsync(RunStreamEvent.Cancelled(run.Id));

            _logger.LogInformation("Run {RunId} 已取消", run.Id);
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.EndedAt = DateTimeOffset.UtcNow;
            run.Error = ex.Message;
            await _eventBus.PublishAsync(RunStreamEvent.Failed(run.Id, ex.Message));

            _logger.LogError(ex, "Run {RunId} 執行失敗", run.Id);
        }
        finally
        {
            // 終態持久化（審計 / 服務重啟後可追溯）
            try
            {
                await _runRepository.SaveAsync(run, CancellationToken.None);
            }
            catch (Exception persistEx)
            {
                _logger.LogWarning(persistEx, "Run {RunId} 終態持久化失敗", run.Id);
            }

            _activeCts.TryRemove(run.Id, out _);
            _eventBus.Complete(run.Id);
        }
    }
}
