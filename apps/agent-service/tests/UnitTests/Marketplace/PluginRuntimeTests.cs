using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.AgentFramework.Plugins;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.UnitTests.Marketplace;

public sealed class PluginRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wingman-plugin-runtime-" + Guid.NewGuid().ToString("N"));
    public PluginRuntimeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RuntimeManifest_LoadsOnlyStructuredFunctionAndHookComponents()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "wingman.json"), """
        {
          "functions":[{"id":"review","description":"Review","executable":"git","arguments":["status","{{input.path}}"],"inputSchema":{"type":"object"}}],
          "hooks":[{"id":"after-change","event":"afterFileChange","command":"git","arguments":["status"],"workingDirectory":"."}]
        }
        """);

        var manifest = new PluginRuntimeManifestLoader().Load(Capabilities(_root));

        Assert.Equal("review", Assert.Single(manifest.Functions).Id);
        Assert.Equal(AgentHookStage.AfterFileChange, Assert.Single(manifest.Hooks).Stage);
    }

    [Fact]
    public async Task RuntimeFunction_UsesResolvedBundledRuntimeAndEntrypoint()
    {
        var scripts = Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        var entrypoint = Path.Combine(scripts.FullName, "uppercase.py");
        await File.WriteAllTextAsync(entrypoint, "print('test')");
        await File.WriteAllTextAsync(Path.Combine(_root, "wingman.json"), """
        {
          "functions":[{
            "id":"uppercase",
            "runtime":{"kind":"python","version":">=3.12"},
            "entrypoint":"scripts/uppercase.py",
            "arguments":["{{input.text}}"],
            "workingDirectory":".",
            "inputSchema":{"type":"object"}
          }]
        }
        """);

        var component = Assert.Single(new PluginRuntimeManifestLoader().Load(Capabilities(_root)).Functions);
        var runner = new CapturingProcessRunner();
        var resolver = new CapturingRuntimeResolver();
        var tool = new PluginFunctionTool(
            Capabilities(_root), component, "plugin:demo", new AllowPolicy(), new UnusedApprovals(),
            runner, resolver, new PassthroughRedactor(), new NullRunEventBus());

        var result = await tool.ExecuteAsync(new ToolExecutionRequest(
            "plugin:demo:uppercase",
            new Dictionary<string, object?> { ["text"] = "hello" },
            new ToolExecutionContext("run", AgentMode.FullAuto, _root)));

        Assert.True(result.Success);
        Assert.NotNull(resolver.Request);
        Assert.Equal(SkillRuntimeKind.Python, resolver.Request!.Kind);
        Assert.Equal(">=3.12", resolver.Request.VersionConstraint);
        Assert.NotNull(runner.Invocation);
        Assert.Equal(@"C:\Wingman\tools\runtimes\python\3.12.10\python.exe", runner.Invocation!.FileName);
        Assert.Equal(["-X", "utf8", entrypoint, "hello"], runner.Invocation.Arguments);
        Assert.Contains("bundled", result.MetadataJson);
    }

    [Fact]
    public async Task RuntimeManifest_RejectsShellOperatorsAndEscapingWorkingDirectory()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "wingman.json"), """
        { "functions":[{"id":"bad","executable":"cmd && whoami","workingDirectory":"../"}] }
        """);

        await Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(() => new PluginRuntimeManifestLoader().Load(Capabilities(_root))));
    }

    [Fact]
    public async Task PluginMcpSource_ProvidesTransientDefinitionsAndSkipsPlaceholderSecrets()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".mcp.json"), """
        { "mcpServers": {
          "ready":{"command":"node","args":["server.js"]},
          "needs-key":{"command":"node","env":{"API_KEY":"REPLACE_WITH_YOUR_API_KEY"}}
        }}
        """);
        var source = new PluginMcpServerSource(new CapabilitySource(Capabilities(_root, [".mcp.json"])), new PluginConfigurationStore(), new StubRuntimeResolver());

        var servers = await source.ListEnabledAsync();

        var server = Assert.Single(servers);
        Assert.Equal("plugin:demo:ready", server.Name);
        Assert.True(server.Id < 0);
        Assert.Equal(McpTransport.Stdio, server.Transport);
    }

    [Fact]
    public async Task PluginMcp_UsesBundledNodeAndInjectsConfiguredValues()
    {
        var entrypointDirectory = Directory.CreateDirectory(Path.Combine(_root, "vendor", "mcp-jira-server", "build"));
        var entrypoint = Path.Combine(entrypointDirectory.FullName, "index.js");
        await File.WriteAllTextAsync(entrypoint, "// MCP entrypoint");
        await File.WriteAllTextAsync(Path.Combine(_root, ".mcp.json"), """
        { "mcpServers": {
          "jira": {
            "runtime":{"kind":"node","version":">=18"},
            "entrypoint":"vendor/mcp-jira-server/build/index.js",
            "env":{"JIRA_BASE_URL":"REPLACE_WITH_YOUR_JIRA_BASE_URL","JIRA_PAT":"REPLACE_WITH_YOUR_API_KEY"}
          }
        }}
        """);
        var source = new PluginMcpServerSource(
            new CapabilitySource(Capabilities(_root, [".mcp.json"])),
            new PluginConfigurationStore(new Dictionary<string, string> { ["JIRA_BASE_URL"] = "https://jira.example", ["JIRA_PAT"] = "secret" }),
            new StubRuntimeResolver(SkillRuntimeKind.Node));

        var server = Assert.Single(await source.ListEnabledAsync());

        Assert.Equal(@"C:\Wingman\tools\runtimes\node\24.18.0\node.exe", server.Command);
        Assert.Equal([entrypoint], server.Arguments);
        Assert.Equal("https://jira.example", server.Environment["JIRA_BASE_URL"]);
        Assert.Equal("secret", server.Environment["JIRA_PAT"]);
    }

    [Fact]
    public void FunctionArgumentBinding_DoesNotPermitPartialTemplateExpansion()
    {
        var input = new Dictionary<string, object?> { ["path"] = "src" };

        Assert.Equal("src", Assert.Single(PluginFunctionTool.BindArguments(["{{input.path}}"], input)));
        Assert.Throws<ArgumentException>(() => PluginFunctionTool.BindArguments(["--path={{input.path}}"], input));
    }

    private static EnabledPluginCapabilities Capabilities(string root, IReadOnlyList<string>? mcp = null) => new("demo", "1.0.0", [], mcp ?? [], [], [], root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class CapabilitySource(EnabledPluginCapabilities capability) : IEnabledPluginCapabilitySource
    {
        public Task<IReadOnlyList<EnabledPluginCapabilities>> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EnabledPluginCapabilities>>([capability]);
    }

    private sealed class AllowPolicy : IAgentPolicyEngine
    {
        public AgentPolicyDecision Evaluate(AgentPolicyContext context, AgentPermissionRequest request) => AgentPolicyDecision.Allow("test");
    }

    private sealed class UnusedApprovals : IApprovalCoordinator
    {
        public Task<ApprovalOutcome> RequestAsync(string runId, AgentPermissionRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Approval should not be requested.");
        public Task<bool> ResolveAsync(string approvalId, ResolveApprovalCommand command, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class CapturingRuntimeResolver : IRuntimeResolver
    {
        public RuntimeResolutionRequest? Request { get; private set; }
        public Task<ResolvedRuntime?> ResolveAsync(RuntimeResolutionRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult<ResolvedRuntime?>(new(
                SkillRuntimeKind.Python,
                @"C:\Wingman\tools\runtimes\python\3.12.10\python.exe",
                new Version(3, 12, 10),
                "bundled",
                ["-X", "utf8"]));
        }
    }

    private sealed class StubRuntimeResolver(SkillRuntimeKind kind = SkillRuntimeKind.Python) : IRuntimeResolver
    {
        public Task<ResolvedRuntime?> ResolveAsync(RuntimeResolutionRequest request, CancellationToken ct = default) => Task.FromResult<ResolvedRuntime?>(new(
            kind,
            kind == SkillRuntimeKind.Node ? @"C:\Wingman\tools\runtimes\node\24.18.0\node.exe" : @"C:\Wingman\tools\runtimes\python\3.12.10\python.exe",
            kind == SkillRuntimeKind.Node ? new Version(24, 18, 0) : new Version(3, 12, 10),
            "bundled"));
    }

    private sealed class PluginConfigurationStore(IReadOnlyDictionary<string, string>? values = null) : IMarketplacePluginConfigurationStore
    {
        private readonly IReadOnlyDictionary<string, string> _values = values ?? new Dictionary<string, string>();
        public Task<IReadOnlyDictionary<string, string>> GetValuesAsync(string pluginId, CancellationToken cancellationToken = default) => Task.FromResult(_values);
        public Task SaveValuesAsync(string pluginId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CapturingProcessRunner : IProcessRunner
    {
        public ProcessInvocation? Invocation { get; private set; }
        public Task<ProcessExecutionResult> RunAsync(ProcessInvocation invocation, CancellationToken ct = default)
        {
            Invocation = invocation;
            return Task.FromResult(new ProcessExecutionResult(0, "ok", "", false, 1));
        }
    }

    private sealed class PassthroughRedactor : ISensitiveDataRedactor { public string Redact(string value) => value; }

    private sealed class NullRunEventBus : IRunEventBus
    {
        public System.Threading.Channels.ChannelReader<RunStreamEvent> Subscribe(string runId) => System.Threading.Channels.Channel.CreateUnbounded<RunStreamEvent>().Reader;
        public ValueTask PublishAsync(RunStreamEvent evt, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Complete(string runId) { }
    }
}
