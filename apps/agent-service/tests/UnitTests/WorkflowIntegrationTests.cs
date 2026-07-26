using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Changes;
using AgentService.Infrastructure.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class WorkflowIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wingman-workflow-" + Guid.NewGuid().ToString("N"));

    public WorkflowIntegrationTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task PlanApprovalAutoVerifyCommit_CompletesWithoutMutatingDuringPlan()
    {
        if (!await CommandSucceeds("git", "--version")) return;

        await File.WriteAllTextAsync(Path.Combine(root, "package.json"),
            "{\"scripts\":{\"build\":\"node -e \\\"process.exit(0)\\\"\",\"test\":\"node -e \\\"process.exit(0)\\\"\"}}");
        Assert.True(await CommandSucceeds("git", "init"));
        Assert.True(await CommandSucceeds("git", "config user.name Wingman Test"));
        Assert.True(await CommandSucceeds("git", "config user.email wingman@test.local"));
        Assert.True(await CommandSucceeds("git", "add --all"));
        Assert.True(await CommandSucceeds("git", "commit -m baseline"));

        var events = new CapturingEventBus();
        var executor = new WritingCodeExecutor();
        var workflow = new ExplorePlanCodeVerifyWorkflow(
            events,
            null!,
            new VerificationService(NullLogger<VerificationService>.Instance),
            new FixedLlm(),
            executor,
            new FileSystemChangeSetService(Path.Combine(root, ".checkpoints")),
            new EmptyRunStepRepository(),
            NullLogger<ExplorePlanCodeVerifyWorkflow>.Instance);

        var request = new WorkflowRunRequest("run-integration", "add result", root, null, true, 1, AgentMode.Plan);
        var plan = await workflow.RunAsync(request);

        Assert.Equal("approved plan", plan);
        Assert.Equal(0, executor.ExecutionCount);
        Assert.False(File.Exists(Path.Combine(root, "result.txt")));

        await workflow.RunAsync(request with { PlanOnly = false, Mode = AgentMode.Auto });

        Assert.Equal(1, executor.ExecutionCount);
        Assert.Equal("implemented", await File.ReadAllTextAsync(Path.Combine(root, "result.txt")));
        Assert.Contains(events.Events, evt => evt.EventType == "run:verify");
        Assert.True(await CommandSucceeds("git", "add --all"));
        Assert.True(await CommandSucceeds("git", "commit -m wingman-run"));
        Assert.Equal("wingman-run", (await CommandOutput("git", "log -1 --pretty=%s")).Trim());
    }

    [Fact]
    public async Task VerificationFailure_RoutesBackToCodeAndThenCompletes()
    {
        await WriteResultAwarePackageJsonAsync();
        var events = new CapturingEventBus();
        var executor = new RepairingCodeExecutor(alwaysFail: false);
        var workflow = CreateWorkflow(events, executor);

        var result = await workflow.RunAsync(new WorkflowRunRequest(
            "run-repair",
            "repair until verification passes",
            root,
            null,
            false,
            2,
            AgentMode.Auto));

        Assert.Equal("implemented", result);
        Assert.Equal(2, executor.ExecutionCount);
        Assert.Null(executor.Feedback[0]);
        Assert.Contains("npm run build", executor.Feedback[1]);
        Assert.Equal("implemented", await File.ReadAllTextAsync(Path.Combine(root, "result.txt")));
        // First attempt stops at the failed build. The successful retry publishes
        // both build and optional-test verification results.
        Assert.Equal(3, events.Events.Count(evt => evt.EventType == "run:verify"));
    }

    [Fact]
    public async Task VerificationFailureAtRetryLimit_ReturnsTerminalFailure()
    {
        await WriteResultAwarePackageJsonAsync();
        var events = new CapturingEventBus();
        var executor = new RepairingCodeExecutor(alwaysFail: true);
        var workflow = CreateWorkflow(events, executor);

        var result = await workflow.RunAsync(new WorkflowRunRequest(
            "run-limit",
            "stop after retry limit",
            root,
            null,
            false,
            2,
            AgentMode.Auto));

        Assert.Equal(2, executor.ExecutionCount);
        Assert.Contains("驗證未通過，已達迭代上限 2 次", result);
        Assert.Contains("npm run build", result);
        Assert.Equal(2, events.Events.Count(evt => evt.EventType == "run:verify"));
    }

    [Fact]
    public async Task ConcurrentRuns_OnSameWorkflowInstance_DoNotShareExecutionState()
    {
        var events = new CapturingEventBus();
        var executor = new RunAwareCodeExecutor();
        var workflow = CreateWorkflow(events, executor);
        var requests = Enumerable.Range(1, 4)
            .Select(index => new WorkflowRunRequest(
                $"run-{index}",
                $"task-{index}",
                root,
                null,
                false,
                1,
                AgentMode.Auto))
            .ToArray();

        var results = await Task.WhenAll(requests.Select(request => workflow.RunAsync(request)));

        Assert.Equal(requests.Select(request => request.Task).Order(), results.Order());
        Assert.Equal(
            requests.Select(request => request.RunId).Order(),
            executor.ExecutedRunIds.Order());
    }

    [Fact]
    public async Task CancelledRun_PropagatesCancellationThroughWorkflow()
    {
        var events = new CapturingEventBus();
        var workflow = CreateWorkflow(events, new WritingCodeExecutor());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.RunAsync(
            new WorkflowRunRequest(
                "run-cancel",
                "cancel this run",
                root,
                null,
                false,
                1,
                AgentMode.Auto),
            cts.Token));
    }

    private ExplorePlanCodeVerifyWorkflow CreateWorkflow(
        CapturingEventBus events,
        IWorkflowCodeExecutor executor) => new(
        events,
        null!,
        new VerificationService(NullLogger<VerificationService>.Instance),
        new FixedLlm(),
        executor,
        new FileSystemChangeSetService(Path.Combine(root, ".checkpoints")),
        new EmptyRunStepRepository(),
        NullLogger<ExplorePlanCodeVerifyWorkflow>.Instance);

    private async Task WriteResultAwarePackageJsonAsync()
    {
        var verifyScript = "node -e \"const fs=require('fs');const ok=fs.existsSync('result.txt')&&fs.readFileSync('result.txt','utf8')==='implemented';process.exit(ok?0:1)\"";
        var packageJson = JsonSerializer.Serialize(new
        {
            scripts = new { build = verifyScript },
        });
        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), packageJson);
    }

    private async Task<bool> CommandSucceeds(string fileName, string arguments) =>
        (await Run(fileName, arguments)).ExitCode == 0;

    private async Task<string> CommandOutput(string fileName, string arguments) =>
        (await Run(fileName, arguments)).Output;

    private async Task<(int ExitCode, string Output)> Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class WritingCodeExecutor : IWorkflowCodeExecutor
    {
        public int ExecutionCount { get; private set; }

        public async Task<string> ExecuteAsync(WorkflowRunRequest request, string plan, string context, string? feedback, CancellationToken ct = default)
        {
            ExecutionCount++;
            await File.WriteAllTextAsync(Path.Combine(request.WorkspacePath, "result.txt"), "implemented", ct);
            return "implemented";
        }
    }

    private sealed class RepairingCodeExecutor(bool alwaysFail) : IWorkflowCodeExecutor
    {
        public int ExecutionCount { get; private set; }
        public List<string?> Feedback { get; } = [];

        public async Task<string> ExecuteAsync(
            WorkflowRunRequest request,
            string plan,
            string context,
            string? feedback,
            CancellationToken ct = default)
        {
            ExecutionCount++;
            Feedback.Add(feedback);
            var output = alwaysFail || ExecutionCount == 1 ? "broken" : "implemented";
            await File.WriteAllTextAsync(Path.Combine(request.WorkspacePath, "result.txt"), output, ct);
            return output;
        }
    }

    private sealed class RunAwareCodeExecutor : IWorkflowCodeExecutor
    {
        private readonly ConcurrentBag<string> executedRunIds = [];

        public IReadOnlyCollection<string> ExecutedRunIds => executedRunIds;

        public Task<string> ExecuteAsync(
            WorkflowRunRequest request,
            string plan,
            string context,
            string? feedback,
            CancellationToken ct = default)
        {
            executedRunIds.Add(request.RunId);
            return Task.FromResult(request.Task);
        }
    }

    private sealed class FixedLlm : ILlmCompletionService
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken ct = default) => Task.FromResult("approved plan");
        public Task<string> CompleteAsync(string prompt, LlmTelemetryContext? telemetryContext, CancellationToken ct = default) => Task.FromResult("approved plan");
        public Task<string> CompleteAsync(string prompt, string? providerProfileId, string? modelId, CancellationToken ct = default) => Task.FromResult("approved plan");
        public Task<string> CompleteAsync(string prompt, string? providerProfileId, string? modelId, LlmTelemetryContext? telemetryContext, CancellationToken ct = default) => Task.FromResult("approved plan");
    }

    private sealed class CapturingEventBus : IRunEventBus
    {
        public ConcurrentQueue<RunStreamEvent> Events { get; } = [];
        public ChannelReader<RunStreamEvent> Subscribe(string runId) => Channel.CreateUnbounded<RunStreamEvent>().Reader;
        public ValueTask PublishAsync(RunStreamEvent evt, CancellationToken ct = default) { Events.Enqueue(evt); return ValueTask.CompletedTask; }
        public void Complete(string runId) { }
    }

    private sealed class EmptyRunStepRepository : IRunStepRepository
    {
        public Task SaveAsync(RunStep step, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RunStep?> GetActiveAsync(string runId, CancellationToken ct = default) => Task.FromResult<RunStep?>(null);
        public Task<IReadOnlyList<RunStep>> ListAsync(string runId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RunStep>>([]);
    }
}
