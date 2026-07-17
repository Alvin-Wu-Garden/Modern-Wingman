using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Orchestration;
using AgentService.Infrastructure.Skills;

namespace AgentService.UnitTests;

public sealed class SkillRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wingman-skill-runtime-" + Guid.NewGuid().ToString("N"));

    public SkillRuntimeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ManifestLoader_ParsesDeclaredEntrypoint()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "wingman.yaml"), """
            version: 1
            runtime:
              type: python
              version: ">=3.10 <3.13"
              dependencyFile: requirements.txt
              entrypoints:
                main:
                  path: scripts/main.py
                  timeoutSeconds: 30
                  network: false
            """);

        var manifest = await new YamlSkillManifestLoader().LoadAsync(_root);

        Assert.NotNull(manifest?.Runtime);
        Assert.Equal("python", manifest.Runtime.Type);
        Assert.Equal("scripts/main.py", manifest.Runtime.Entrypoints["main"].Path);
    }

    [Fact]
    public async Task ManifestLoader_RejectsEntrypointTraversal()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "wingman.yaml"), """
            version: 1
            runtime:
              type: node
              entrypoints:
                main:
                  path: ../outside.js
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new YamlSkillManifestLoader().LoadAsync(_root));
    }

    [Theory]
    [InlineData("3.12.2", ">=3.10 <3.13", true)]
    [InlineData("3.9.9", ">=3.10 <3.13", false)]
    [InlineData("20.11.0", "20", true)]
    [InlineData("21.0.0", "20", false)]
    public void VersionConstraint_EvaluatesRanges(string version, string constraint, bool expected)
    {
        Assert.Equal(
            expected,
            RuntimeVersionConstraint.IsSatisfied(Version.Parse(version), constraint));
    }

    [Fact]
    public async Task RunSkillScript_ExecutesOnlyDeclaredEntrypoint()
    {
        var scripts = Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        var scriptPath = Path.Combine(scripts.FullName, "main.py");
        await File.WriteAllTextAsync(Path.Combine(_root, "SKILL.md"), "# Test");
        await File.WriteAllTextAsync(scriptPath, "print('ok')");
        await File.WriteAllTextAsync(Path.Combine(_root, "wingman.yaml"), """
            version: 1
            runtime:
              type: python
              version: ">=3.10"
              entrypoints:
                main:
                  path: scripts/main.py
                  timeoutSeconds: 10
                  parameters:
                    value:
                      type: string
                      required: true
                      flag: --value
            """);

        var process = new CapturingProcessRunner();
        var tool = new RunSkillScriptTool(new SkillScriptRunner(
            new StubSkillProvider(_root),
            new YamlSkillManifestLoader(),
            new StubRuntimeResolver(),
            process,
            new DefaultAgentPolicyEngine(),
            new RejectingApprovalCoordinator()));
        var result = await tool.ExecuteAsync(new ToolExecutionRequest(
            "run_skill_script",
            new Dictionary<string, object?>
            {
                ["skillName"] = "test-skill",
                ["entrypoint"] = "main",
                ["parameters"] = new Dictionary<string, object?> { ["value"] = "hello world" },
            },
            new ToolExecutionContext("run-1", AgentMode.FullAuto, _root)));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(process.LastInvocation);
        Assert.Equal(scriptPath, process.LastInvocation.Arguments[0]);
        Assert.Equal("--value", process.LastInvocation.Arguments[^2]);
        Assert.Equal("hello world", process.LastInvocation.Arguments[^1]);
    }

    [Fact]
    public async Task PythonSkill_ReturnsActionableErrorWhenRuntimeIsMissing()
    {
        await WriteSkillAsync("python", "scripts/main.py", ">=3.10");
        var result = await ExecuteSkillAsync(
            new StubRuntimeResolver(null, useDefault: false),
            new CapturingProcessRunner());

        Assert.False(result.Success);
        Assert.Contains("No compatible Python runtime", result.Error);
    }

    [Fact]
    public async Task PythonSkill_ReturnsConstraintWhenInstalledVersionIsIncompatible()
    {
        await WriteSkillAsync("python", "scripts/main.py", ">=3.13");
        var result = await ExecuteSkillAsync(
            new StubRuntimeResolver(null, useDefault: false),
            new CapturingProcessRunner());

        Assert.False(result.Success);
        Assert.Contains(">=3.13", result.Error);
    }

    [Fact]
    public async Task PythonSkill_ReportsTimeout()
    {
        await WriteSkillAsync("python", "scripts/main.py", ">=3.10");
        var result = await ExecuteSkillAsync(
            new StubRuntimeResolver(),
            new CapturingProcessRunner(new ProcessExecutionResult(-1, "partial", "timed out", true, 10_000)));

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task NodeSkill_ReportsNonZeroExitCode()
    {
        await WriteSkillAsync("node", "scripts/main.js", ">=20");
        var runtime = new ResolvedRuntime(
            SkillRuntimeKind.Node,
            @"C:\runtime\node.exe",
            new Version(24, 0, 0),
            "test");
        var result = await ExecuteSkillAsync(
            new StubRuntimeResolver(runtime),
            new CapturingProcessRunner(new ProcessExecutionResult(2, "", "module failed", false, 15)));

        Assert.False(result.Success);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("module failed", result.Error);
    }

    private async Task WriteSkillAsync(string runtime, string script, string version)
    {
        var scriptPath = Path.Combine(_root, script.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(Path.Combine(_root, "SKILL.md"), "# Test");
        await File.WriteAllTextAsync(scriptPath, runtime == "node" ? "console.log('ok')" : "print('ok')");
        await File.WriteAllTextAsync(Path.Combine(_root, "wingman.yaml"), $$"""
            version: 1
            runtime:
              type: {{runtime}}
              version: "{{version}}"
              entrypoints:
                main:
                  path: {{script.Replace('\\', '/')}}
                  timeoutSeconds: 10
            """);
    }

    private async Task<ToolExecutionResult> ExecuteSkillAsync(
        IRuntimeResolver runtimeResolver,
        IProcessRunner processRunner)
    {
        var runner = new SkillScriptRunner(
            new StubSkillProvider(_root),
            new YamlSkillManifestLoader(),
            runtimeResolver,
            processRunner,
            new DefaultAgentPolicyEngine(),
            new RejectingApprovalCoordinator());
        return await runner.ExecuteAsync(new ToolExecutionRequest(
            "run_skill_script",
            new Dictionary<string, object?>
            {
                ["skillName"] = "test-skill",
                ["entrypoint"] = "main",
            },
            new ToolExecutionContext("run-test", AgentMode.FullAuto, _root)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class StubSkillProvider(string root) : ISkillProvider
    {
        public IReadOnlyList<SkillDefinition> ListSkills() =>
        [
            new SkillDefinition
            {
                Name = "test-skill",
                Description = "test",
                SkillFilePath = Path.Combine(root, "SKILL.md"),
            },
        ];

        public Task<string?> ReadSkillContentAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public void Refresh() { }
    }

    private sealed class StubRuntimeResolver : IRuntimeResolver
    {
        private readonly ResolvedRuntime? _runtime;

        public StubRuntimeResolver(ResolvedRuntime? runtime = null, bool useDefault = true)
        {
            _runtime = useDefault && runtime is null
                ? new ResolvedRuntime(
                    SkillRuntimeKind.Python,
                    @"C:\runtime\python.exe",
                    new Version(3, 12, 0),
                    "test")
                : runtime;
        }

        public Task<ResolvedRuntime?> ResolveAsync(
            RuntimeResolutionRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(_runtime);
    }

    private sealed class CapturingProcessRunner(
        ProcessExecutionResult? result = null) : IProcessRunner
    {
        public ProcessInvocation? LastInvocation { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken ct = default)
        {
            LastInvocation = invocation;
            return Task.FromResult(result ?? new ProcessExecutionResult(0, "ok", "", false, 1));
        }
    }

    private sealed class RejectingApprovalCoordinator : IApprovalCoordinator
    {
        public Task<ApprovalOutcome> RequestAsync(
            string runId,
            AgentPermissionRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new ApprovalOutcome(false, null, "unexpected approval"));

        public Task<bool> ResolveAsync(
            string approvalId,
            ResolveApprovalCommand command,
            CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
