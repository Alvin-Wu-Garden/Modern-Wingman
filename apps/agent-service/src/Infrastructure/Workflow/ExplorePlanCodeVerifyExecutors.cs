using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Modules.GraphRAG;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.Workflow;

internal sealed partial class ExploreExecutor(
    IRunEventBus eventBus,
    GraphRetrievalService graphRagService,
    ILogger logger) : Executor("Explore")
{
    [MessageHandler]
    private async ValueTask<ExplorationResult> HandleAsync(
        WorkflowRunRequest request,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        await eventBus.PublishAsync(
            RunStreamEvent.Phase(request.RunId, "explore", "收集專案背景"),
            cancellationToken);

        var promptContext = new StringBuilder();
        var agentsMdPath = Path.Combine(request.WorkspacePath, "AGENTS.md");
        if (File.Exists(agentsMdPath))
        {
            promptContext.AppendLine("# 專案指南（AGENTS.md）");
            promptContext.AppendLine(await File.ReadAllTextAsync(agentsMdPath, cancellationToken));
            promptContext.AppendLine();
        }

        if (request.ProjectId is not null)
        {
            try
            {
                promptContext.AppendLine(await graphRagService.GenerateRepoMapAsync(
                    request.ProjectId,
                    1024,
                    cancellationToken));
                promptContext.AppendLine();

                var knowHow = await graphRagService.AnswerAsync(
                    request.ProjectId,
                    $"與以下任務相關的模組、類別與注意事項：{request.Task}",
                    cancellationToken);
                promptContext.AppendLine("# 相關 Know-how（圖譜查詢）");
                promptContext.AppendLine(knowHow);
                promptContext.AppendLine();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Explore 階段圖譜查詢失敗，continue without");
            }
        }

        return new ExplorationResult(request, promptContext.ToString());
    }
}

internal sealed partial class PlanExecutor(
    IRunEventBus eventBus,
    ILlmCompletionService llm) : Executor("Plan")
{
    [MessageHandler]
    private async ValueTask<PlanResult> HandleAsync(
        ExplorationResult input,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var request = input.Request;
        await eventBus.PublishAsync(
            RunStreamEvent.Phase(request.RunId, "plan", "產出實作計畫"),
            cancellationToken);

        var prompt = $"""
            你是資深工程師。根據以下專案背景，為任務撰寫精簡的實作計畫（繁體中文）：
            - 列出需要修改的檔案與原因
            - 列出步驟（不超過 8 步）
            - 標明風險與需要驗證的點
            不要寫程式碼，只寫計畫。

            {input.Context}

            # 任務
            {request.Task}
            """;

        var plan = await llm.CompleteAsync(
            prompt,
            new LlmTelemetryContext(
                FeatureArea: "workflow_plan",
                ProjectId: request.ProjectId,
                RunId: request.RunId,
                TraceId: request.TraceId),
            cancellationToken);
        await eventBus.PublishAsync(
            RunStreamEvent.PlanReady(request.RunId, plan),
            cancellationToken);

        return new PlanResult(request, input.Context, plan);
    }
}

internal sealed partial class ImpactExecutor(
    IRunEventBus eventBus,
    GraphRetrievalService impactAnalysisService,
    ILogger logger) : Executor("Impact")
{
    [MessageHandler]
    private async ValueTask<ImpactResult> HandleAsync(
        PlanResult input,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var request = input.Request;
        if (request.ProjectId is null)
            return new ImpactResult(request, input.Context, input.Plan);

        await eventBus.PublishAsync(
            RunStreamEvent.Phase(request.RunId, "impact", "分析影響範圍"),
            cancellationToken);

        var promptContext = new StringBuilder(input.Context);
        try
        {
            var impact = await impactAnalysisService.AnalyzeImpactAsync(
                request.ProjectId,
                request.Task,
                3,
                cancellationToken);
            if (impact.Target is not null && impact.AffectedNodes.Count > 0)
            {
                promptContext.AppendLine("# ⚠ 影響分析（修改時務必確認不破壞以下呼叫者）");
                foreach (var affected in impact.AffectedNodes.Take(15))
                {
                    promptContext.AppendLine(
                        $"- {affected.Node.Role}: {affected.Node.Name}（{affected.Node.FilePath}:{affected.Node.StartLine}）");
                }
                promptContext.AppendLine();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Impact analysis 失敗，continue without");
        }

        return new ImpactResult(request, promptContext.ToString(), input.Plan);
    }
}

internal sealed partial class CodeExecutor(
    IRunEventBus eventBus,
    IWorkflowCodeExecutor codeExecutor,
    IChangeSetService changeSetService,
    IRunStepRepository runSteps) : Executor("Code")
{
    [MessageHandler]
    private ValueTask<CodeResult> HandleInitialAsync(
        ImpactResult input,
        IWorkflowContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            input.Request,
            input.Plan,
            input.Context,
            feedback: null,
            verifyAttempt: 1,
            cancellationToken);

    [MessageHandler]
    private ValueTask<CodeResult> HandleRepairAsync(
        VerificationOutcome input,
        IWorkflowContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            input.Request,
            input.Plan,
            input.Context,
            input.Failure,
            input.VerifyAttempt + 1,
            cancellationToken);

    private async ValueTask<CodeResult> ExecuteAsync(
        WorkflowRunRequest request,
        string plan,
        string promptContext,
        string? feedback,
        int verifyAttempt,
        CancellationToken cancellationToken)
    {
        var message = feedback is null
            ? "執行實作"
            : $"修復驗證錯誤（第 {verifyAttempt - 1} 次）";
        await eventBus.PublishAsync(
            RunStreamEvent.Phase(request.RunId, "code", message),
            cancellationToken);
        await AttachCheckpointAsync(request, cancellationToken);

        var output = await codeExecutor.ExecuteAsync(
            request,
            plan,
            promptContext,
            feedback,
            cancellationToken);
        return new CodeResult(request, promptContext, plan, output, verifyAttempt);
    }

    private async Task AttachCheckpointAsync(
        WorkflowRunRequest request,
        CancellationToken cancellationToken)
    {
        var checkpoint = await changeSetService.CreateCheckpointAsync(
            request.RunId,
            request.WorkspacePath,
            cancellationToken);
        var step = await runSteps.GetActiveAsync(request.RunId, cancellationToken);
        if (step is null)
            return;

        step.CheckpointId = checkpoint;
        await runSteps.SaveAsync(step, cancellationToken);
    }
}

internal sealed partial class VerifyExecutor(
    IRunEventBus eventBus,
    VerificationService verificationService,
    ILogger logger) : Executor("Verify")
{
    [MessageHandler]
    private async ValueTask<VerificationOutcome> HandleAsync(
        CodeResult input,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var request = input.Request;
        var commands = VerificationService.DetectVerifyCommands(request.WorkspacePath);
        if (commands.Count == 0)
        {
            await eventBus.PublishAsync(
                RunStreamEvent.Phase(request.RunId, "done", "無可用驗證指令，完成"),
                cancellationToken);
            return Terminal(input, input.Output);
        }

        await eventBus.PublishAsync(
            RunStreamEvent.Phase(
                request.RunId,
                "verify",
                $"驗證中（第 {input.VerifyAttempt} 次）"),
            cancellationToken);

        string? failure = null;
        foreach (var command in commands)
        {
            var result = await verificationService.RunAsync(
                request.WorkspacePath,
                command,
                cancellationToken);
            await eventBus.PublishAsync(
                RunStreamEvent.VerifyResult(
                    request.RunId,
                    result.Success,
                    input.VerifyAttempt,
                    result.Output),
                cancellationToken);
            if (result.Success)
                continue;

            failure = $"指令 `{result.Command}` 失敗:\n{result.Output}";
            break;
        }

        if (failure is null)
        {
            await eventBus.PublishAsync(
                RunStreamEvent.Phase(request.RunId, "done", "驗證通過"),
                cancellationToken);
            logger.LogInformation(
                "Run {RunId}: 驗證通過（第 {Attempt} 次）",
                request.RunId,
                input.VerifyAttempt);
            return Terminal(input, input.Output);
        }

        if (input.VerifyAttempt >= request.MaxVerifyAttempts)
        {
            await eventBus.PublishAsync(
                RunStreamEvent.Phase(
                    request.RunId,
                    "done",
                    $"驗證失敗（已達 {input.VerifyAttempt} 次上限）"),
                cancellationToken);
            return Terminal(
                input,
                input.Output +
                $"\n\n[驗證未通過，已達迭代上限 {input.VerifyAttempt} 次]\n{failure}");
        }

        return new VerificationOutcome(
            request,
            input.Context,
            input.Plan,
            input.Output,
            input.VerifyAttempt,
            ShouldRetry: true,
            Failure: failure,
            TerminalText: string.Empty);
    }

    private static VerificationOutcome Terminal(CodeResult input, string terminalText) => new(
        input.Request,
        input.Context,
        input.Plan,
        input.Output,
        input.VerifyAttempt,
        ShouldRetry: false,
        Failure: null,
        TerminalText: terminalText);
}

internal sealed partial class WorkflowResultExecutor() : Executor("Result")
{
    [MessageHandler]
    private string HandlePlan(PlanResult input, IWorkflowContext context) => input.Plan;

    [MessageHandler]
    private string HandleVerification(VerificationOutcome input, IWorkflowContext context) =>
        input.TerminalText;
}
