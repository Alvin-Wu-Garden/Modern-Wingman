using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.CodeGraph;
using AgentService.Infrastructure.Providers;
using AgentService.Domain.Models;
using WingmanAgentMode = AgentService.Domain.Models.AgentMode;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.Workflow;

/// <summary>工作流執行參數。</summary>
public sealed record WorkflowRunRequest(
    string RunId,
    string Task,
    string WorkspacePath,
    /// <summary>對應 projects 表的 ID；null = 未索引專案（跳過圖譜輔助）。</summary>
    string? ProjectId,
    /// <summary>true = Plan mode：產出計畫後暫停等待核准。</summary>
    bool PlanOnly,
    /// <summary>驗證迴圈最大迭代次數。</summary>
    int MaxVerifyAttempts = 3,
    WingmanAgentMode Mode = WingmanAgentMode.Auto,
    string? TraceId = null);

/// <summary>
/// Explore → Plan → Code → Verify 工作流（WS4）。
///
/// 已驗證方法論整合（Claude Code / Codex / Aider）：
///   Explore：唯讀調查（Repo Map + GraphRAG 查詢注入 context）
///   Plan   ：產出實作計畫（Plan mode 時暫停等待使用者核准）
///   Code   ：Copilot session agentic loop 執行修改（前置 Impact Analysis）
///   Verify ：建置/測試為停止條件，失敗把錯誤回饋給 Agent 迭代（上限 N 次）
///
/// 每階段以 RunStreamEvent.Phase 發布事件，前端 timeline 可觀測。
/// </summary>
public sealed class ExplorePlanCodeVerifyWorkflow(
    IRunEventBus eventBus,
    RepoMapService repoMapService,
    GraphRagService graphRagService,
    ImpactAnalysisService impactAnalysisService,
    VerificationService verificationService,
    ILlmCompletionService llm,
    IWorkflowCodeExecutor codeExecutor,
    IChangeSetService changeSetService,
    IRunStepRepository runSteps,
    ILogger<ExplorePlanCodeVerifyWorkflow> logger)
{
    /// <summary>
    /// 執行完整工作流。PlanOnly=true 時回傳計畫文字並停止。
    /// </summary>
    public async Task<string> RunAsync(WorkflowRunRequest request, CancellationToken ct = default)
    {
        var runId = request.RunId;

        // ═══ Phase 1: Explore ═══════════════════════════════════════════════
        await eventBus.PublishAsync(RunStreamEvent.Phase(runId, "explore", "收集專案背景"), ct);

        var context = new StringBuilder();

        // AGENTS.md（若存在）
        var agentsMdPath = Path.Combine(request.WorkspacePath, "AGENTS.md");
        if (File.Exists(agentsMdPath))
        {
            context.AppendLine("# 專案指南（AGENTS.md）");
            context.AppendLine(await File.ReadAllTextAsync(agentsMdPath, ct));
            context.AppendLine();
        }

        // Repo Map + GraphRAG（已索引專案）
        if (request.ProjectId is not null)
        {
            try
            {
                var repoMap = await repoMapService.GenerateAsync(request.ProjectId, 1024, ct);
                context.AppendLine(repoMap);
                context.AppendLine();

                var knowHow = await graphRagService.QueryAsync(
                    request.ProjectId,
                    $"與以下任務相關的模組、類別與注意事項：{request.Task}",
                    ct);
                context.AppendLine("# 相關 Know-how（圖譜查詢）");
                context.AppendLine(knowHow);
                context.AppendLine();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Explore 階段圖譜查詢失敗，continue without");
            }
        }

        // ═══ Phase 2: Plan ══════════════════════════════════════════════════
        await eventBus.PublishAsync(RunStreamEvent.Phase(runId, "plan", "產出實作計畫"), ct);

        var planPrompt = $"""
            你是資深工程師。根據以下專案背景，為任務撰寫精簡的實作計畫（繁體中文）：
            - 列出需要修改的檔案與原因
            - 列出步驟（不超過 8 步）
            - 標明風險與需要驗證的點
            不要寫程式碼，只寫計畫。

            {context}

            # 任務
            {request.Task}
            """;

        var plan = await llm.CompleteAsync(
            planPrompt,
            new LlmTelemetryContext(
                FeatureArea: "workflow_plan",
                ProjectId: request.ProjectId,
                RunId: runId,
                TraceId: request.TraceId),
            ct);
        await eventBus.PublishAsync(RunStreamEvent.PlanReady(runId, plan), ct);

        if (request.PlanOnly)
        {
            logger.LogInformation("Run {RunId}: Plan mode，等待使用者核准", runId);
            return plan;
        }

        // ═══ Phase 2.5: Impact Analysis（不改 A 壞 B 前置檢查）═══════════════
        if (request.ProjectId is not null)
        {
            await eventBus.PublishAsync(RunStreamEvent.Phase(runId, "impact", "分析影響範圍"), ct);
            try
            {
                // 從計畫抽出可能的修改目標（簡化：直接用任務描述搜尋）
                var impact = await impactAnalysisService.AnalyzeAsync(
                    request.ProjectId, request.Task, 3, ct);
                if (impact.Target is not null && impact.AffectedMethods.Count > 0)
                {
                    context.AppendLine("# ⚠ 影響分析（修改時務必確認不破壞以下呼叫者）");
                    foreach (var m in impact.AffectedMethods.Take(15))
                        context.AppendLine($"- {m.Signature ?? m.Name}（{m.FilePath}:{m.StartLine}）");
                    context.AppendLine();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Impact analysis 失敗，continue without");
            }
        }

        // ═══ Phase 3: Code ══════════════════════════════════════════════════
        await eventBus.PublishAsync(RunStreamEvent.Phase(runId, "code", "執行實作"), ct);
        await AttachCheckpointAsync(request, ct);

        var codeResult = await codeExecutor.ExecuteAsync(
            request, plan, context.ToString(), feedback: null, ct);

        // ═══ Phase 4: Verify（迭代迴圈）═════════════════════════════════════
        var verifyCommands = VerificationService.DetectVerifyCommands(request.WorkspacePath);
        if (verifyCommands.Count == 0)
        {
            await eventBus.PublishAsync(
                RunStreamEvent.Phase(runId, "done", "無可用驗證指令，完成"), ct);
            return codeResult;
        }

        for (var attempt = 1; attempt <= request.MaxVerifyAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await eventBus.PublishAsync(
                RunStreamEvent.Phase(runId, "verify", $"驗證中（第 {attempt} 次）"), ct);

            var allPassed = true;
            var failOutput = "";
            foreach (var command in verifyCommands)
            {
                var result = await verificationService.RunAsync(request.WorkspacePath, command, ct);
                await eventBus.PublishAsync(
                    RunStreamEvent.VerifyResult(runId, result.Success, attempt, result.Output), ct);
                if (!result.Success)
                {
                    allPassed = false;
                    failOutput = $"指令 `{result.Command}` 失敗:\n{result.Output}";
                    break;
                }
            }

            if (allPassed)
            {
                await eventBus.PublishAsync(RunStreamEvent.Phase(runId, "done", "驗證通過"), ct);
                logger.LogInformation("Run {RunId}: 驗證通過（第 {Attempt} 次）", runId, attempt);
                return codeResult;
            }

            if (attempt == request.MaxVerifyAttempts)
            {
                await eventBus.PublishAsync(
                    RunStreamEvent.Phase(runId, "done", $"驗證失敗（已達 {attempt} 次上限）"), ct);
                return codeResult + $"\n\n[驗證未通過，已達迭代上限 {attempt} 次]\n{failOutput}";
            }

            // 失敗回饋給 Agent 修復
            await eventBus.PublishAsync(
                RunStreamEvent.Phase(runId, "code", $"修復驗證錯誤（第 {attempt} 次）"), ct);
            await AttachCheckpointAsync(request, ct);
            codeResult = await codeExecutor.ExecuteAsync(request, plan, context.ToString(), failOutput, ct);
        }

        return codeResult;
    }

    private async Task AttachCheckpointAsync(WorkflowRunRequest request,CancellationToken ct)
    {
        var checkpoint=await changeSetService.CreateCheckpointAsync(request.RunId,request.WorkspacePath,ct);var step=await runSteps.GetActiveAsync(request.RunId,ct);if(step is not null){step.CheckpointId=checkpoint;await runSteps.SaveAsync(step,ct);}
    }

}
