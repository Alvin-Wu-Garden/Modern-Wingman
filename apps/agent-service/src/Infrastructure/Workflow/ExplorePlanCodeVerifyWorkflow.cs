using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Modules.GraphRAG;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using WingmanAgentMode = AgentService.Domain.Models.AgentMode;

namespace AgentService.Infrastructure.Workflow;

/// <summary>工作流執行參數。</summary>
public sealed record WorkflowRunRequest(
    string RunId,
    string Task,
    string WorkspacePath,
    string? ProjectId,
    bool PlanOnly,
    int MaxVerifyAttempts = 3,
    WingmanAgentMode Mode = WingmanAgentMode.Auto,
    string? TraceId = null);

/// <summary>
/// Explore -> Plan -> Impact -> Code -> Verify workflow implemented by the
/// stable Microsoft Agent Framework graph API.
/// </summary>
public sealed class ExplorePlanCodeVerifyWorkflow(
    IRunEventBus eventBus,
    GraphRetrievalService graphRagService,
    VerificationService verificationService,
    ILlmCompletionService llm,
    IWorkflowCodeExecutor codeExecutor,
    IChangeSetService changeSetService,
    IRunStepRepository runSteps,
    ILogger<ExplorePlanCodeVerifyWorkflow> logger)
{
    public async Task<string> RunAsync(
        WorkflowRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Task);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspacePath);
        if (request.MaxVerifyAttempts < 1)
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxVerifyAttempts),
                "MaxVerifyAttempts must be at least one.");
        cancellationToken.ThrowIfCancellationRequested();

        var workflow = BuildWorkflow();
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            request,
            sessionId: request.RunId,
            cancellationToken);

        string? terminalResult = null;
        await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken))
        {
            switch (workflowEvent)
            {
                case WorkflowOutputEvent { Data: string output }:
                    if (terminalResult is not null)
                        throw new InvalidOperationException("Workflow produced more than one terminal result.");
                    terminalResult = output;
                    break;

                case WorkflowErrorEvent error:
                    throw error.Exception ?? new InvalidOperationException("Workflow execution failed.");
            }
        }

        // MAF may complete an event stream without an error event when cancellation
        // wins before the first executor starts. Preserve the public cancellation
        // contract instead of reporting a misleading missing-output failure.
        cancellationToken.ThrowIfCancellationRequested();

        return terminalResult
            ?? throw new InvalidOperationException("Workflow completed without a terminal result.");
    }

    private Microsoft.Agents.AI.Workflows.Workflow BuildWorkflow()
    {
        // Executors are intentionally created per invocation. This is the default
        // isolation pattern recommended by MAF and avoids cross-run mutable state.
        var explore = new ExploreExecutor(eventBus, graphRagService, logger);
        var plan = new PlanExecutor(eventBus, llm);
        var impact = new ImpactExecutor(eventBus, graphRagService, logger);
        var code = new CodeExecutor(eventBus, codeExecutor, changeSetService, runSteps);
        var verify = new VerifyExecutor(eventBus, verificationService, logger);
        var result = new WorkflowResultExecutor();

        return new WorkflowBuilder(explore)
            .AddEdge(explore, plan)
            .AddEdge<PlanResult>(
                plan,
                result,
                condition: message => message is { Request.PlanOnly: true })
            .AddEdge<PlanResult>(
                plan,
                impact,
                condition: message => message is { Request.PlanOnly: false })
            .AddEdge(impact, code)
            .AddEdge(code, verify)
            .AddEdge<VerificationOutcome>(
                verify,
                code,
                condition: message => message is { ShouldRetry: true })
            .AddEdge<VerificationOutcome>(
                verify,
                result,
                condition: message => message is { ShouldRetry: false })
            .WithOutputFrom(result)
            .Build();
    }
}
