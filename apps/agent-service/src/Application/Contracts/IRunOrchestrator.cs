using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// Run 生命週期協調器（Orchestration entry point）。
///
/// Phase 1：由 CopilotRunAdapter 實作，直接驅動 copilot-sdk 工作階段。
/// Phase 3：底層替換為 MAF Workflow，每個 Run 對應一個 Workflow 實例，
///          orchestration 邏輯（planner → executor → verifier）在 Workflow 層管理，
///          本介面保持不變。
/// </summary>
public interface IRunOrchestrator
{
    /// <summary>
    /// 建立 RunEntity 並非同步啟動執行。
    /// 呼叫者可立刻向 gRPC client 回傳 runId，
    /// 事件透過 IRunEventBus 發佈，由 StreamRunEvents 轉送給 Rust 層。
    /// </summary>
    Task<RunEntity> StartRunAsync(CreateRunCommand command, CancellationToken ct = default);

    /// <summary>取消進行中的 Run。</summary>
    Task CancelRunAsync(string runId, CancellationToken ct = default);
    Task PauseRunAsync(string runId,CancellationToken ct=default);
    Task<RunEntity> ResumeRunAsync(string runId,CancellationToken ct=default);
    Task<RunEntity> RetryRunAsync(string runId,string? providerProfileId=null,CancellationToken ct=default);

    /// <summary>取得 RunEntity（null = 不存在）。</summary>
    RunEntity? GetRun(string runId);
}
