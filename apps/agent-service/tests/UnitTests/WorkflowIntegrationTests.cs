using System.Diagnostics;
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
            null!,
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

    private sealed class FixedLlm : ILlmCompletionService
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken ct = default) => Task.FromResult("approved plan");
        public Task<string> CompleteAsync(string prompt, LlmTelemetryContext? telemetryContext, CancellationToken ct = default) => Task.FromResult("approved plan");
        public Task<string> CompleteAsync(string prompt, string? providerProfileId, string? modelId, CancellationToken ct = default) => Task.FromResult("approved plan");
        public Task<string> CompleteAsync(string prompt, string? providerProfileId, string? modelId, LlmTelemetryContext? telemetryContext, CancellationToken ct = default) => Task.FromResult("approved plan");
    }

    private sealed class CapturingEventBus : IRunEventBus
    {
        public List<RunStreamEvent> Events { get; } = [];
        public ChannelReader<RunStreamEvent> Subscribe(string runId) => Channel.CreateUnbounded<RunStreamEvent>().Reader;
        public ValueTask PublishAsync(RunStreamEvent evt, CancellationToken ct = default) { Events.Add(evt); return ValueTask.CompletedTask; }
        public void Complete(string runId) { }
    }

    private sealed class EmptyRunStepRepository : IRunStepRepository
    {
        public Task SaveAsync(RunStep step, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RunStep?> GetActiveAsync(string runId, CancellationToken ct = default) => Task.FromResult<RunStep?>(null);
        public Task<IReadOnlyList<RunStep>> ListAsync(string runId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RunStep>>([]);
    }
}
