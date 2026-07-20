using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.AgentFramework;
using Microsoft.Extensions.AI;

namespace AgentService.UnitTests;

public sealed class WingmanToolAdapterTests
{
    [Fact]
    public void CreateTools_ExposesOneFunctionPerDescriptorWithItsSchema()
    {
        var registry = new CapturingRegistry([
            new ToolDescriptor(
                "read_file",
                "Read one file.",
                AgentCapability.Read,
                AgentRiskLevel.Low,
                TimeSpan.FromSeconds(5),
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
            new ToolDescriptor(
                "search_files",
                "Search files.",
                AgentCapability.Read,
                AgentRiskLevel.Low,
                TimeSpan.FromSeconds(5)),
        ]);

        var tools = WingmanToolAdapter.CreateTools(registry, CreateContext());

        Assert.Equal(["read_file", "search_files"], tools.Select(tool => tool.Name));
        Assert.DoesNotContain(tools, tool => tool.Name == "call_wingman_tool");
        Assert.Equal("Read one file.", tools[0].Description);
        Assert.Equal("string", tools[0].JsonSchema
            .GetProperty("properties")
            .GetProperty("path")
            .GetProperty("type")
            .GetString());
    }

    [Fact]
    public async Task GeneratedFunction_InvokesOriginalRegistryToolWithRunContext()
    {
        var descriptor = new ToolDescriptor(
            "plugin:quality:scan",
            "Run quality scan.",
            AgentCapability.Execute,
            AgentRiskLevel.High,
            TimeSpan.FromSeconds(30),
            """{"type":"object","properties":{"target":{"type":"string"}}}""");
        var registry = new CapturingRegistry([descriptor]);
        var context = CreateContext();
        var function = Assert.Single(WingmanToolAdapter.CreateTools(registry, context));

        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", function.Name);
        Assert.NotEqual(descriptor.Name, function.Name);

        var result = await function.InvokeAsync(
            new AIFunctionArguments { ["target"] = "src" });

        Assert.Equal(JsonValueKind.Object, Assert.IsType<JsonElement>(result).ValueKind);
        var request = Assert.IsType<ToolExecutionRequest>(registry.LastRequest);
        Assert.Equal(descriptor.Name, request.ToolName);
        Assert.Equal("src", request.Arguments["target"]?.ToString());
        Assert.Equal("run-1", request.Context.RunId);
        Assert.Equal(AgentMode.Auto, request.Context.Mode);
        Assert.Equal("C:\\workspace", request.Context.WorkspacePath);
        Assert.Equal("project-1", request.Context.ProjectId);
    }

    [Fact]
    public void CreateTools_RejectsInvalidSchemaBeforeProviderInvocation()
    {
        var registry = new CapturingRegistry([
            new ToolDescriptor(
                "broken",
                "Broken schema.",
                AgentCapability.Read,
                AgentRiskLevel.Low,
                TimeSpan.FromSeconds(1),
                "not-json"),
        ]);

        var error = Assert.Throws<InvalidDataException>(() =>
            WingmanToolAdapter.CreateTools(registry, CreateContext()));

        Assert.Contains("broken", error.Message);
    }

    [Fact]
    public async Task GeneratedFunction_PropagatesCancellationToRegistry()
    {
        var registry = new CancellationRegistry();
        var function = Assert.Single(WingmanToolAdapter.CreateTools(registry, CreateContext()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await function.InvokeAsync([], cts.Token));

        Assert.True(registry.ReceivedCancellation);
    }

    private static AgentCreationContext CreateContext() => new()
    {
        Profile = new ModelProviderProfile
        {
            Id = "test",
            DisplayName = "Test",
            Kind = ProviderKind.CopilotByok,
        },
        Instructions = "test",
        Mode = AgentMode.Auto,
        WorkspacePath = "C:\\workspace",
        RunId = "run-1",
        ProjectId = "project-1",
    };

    private sealed class CapturingRegistry(IReadOnlyList<ToolDescriptor> descriptors) : IToolRegistry
    {
        public ToolExecutionRequest? LastRequest { get; private set; }

        public IReadOnlyList<ToolDescriptor> ListTools() => descriptors;

        public bool TryGet(string name, out IAgentTool? tool)
        {
            tool = null;
            return false;
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ToolExecutionResult(true, "ok"));
        }

        public void Register(IAgentTool tool) => throw new NotSupportedException();
    }

    private sealed class CancellationRegistry : IToolRegistry
    {
        private static readonly ToolDescriptor Descriptor = new(
            "cancel_test",
            "Cancellation test.",
            AgentCapability.Read,
            AgentRiskLevel.Low,
            TimeSpan.FromSeconds(1));

        public bool ReceivedCancellation { get; private set; }

        public IReadOnlyList<ToolDescriptor> ListTools() => [Descriptor];

        public bool TryGet(string name, out IAgentTool? tool)
        {
            tool = null;
            return false;
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ReceivedCancellation = ct.IsCancellationRequested;
            return Task.FromCanceled<ToolExecutionResult>(ct);
        }

        public void Register(IAgentTool tool) => throw new NotSupportedException();
    }
}
