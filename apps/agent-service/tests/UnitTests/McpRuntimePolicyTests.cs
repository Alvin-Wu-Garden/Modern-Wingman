using System.Net;
using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Mcp;
using AgentService.Infrastructure.Orchestration;
using Microsoft.Extensions.Configuration;

namespace AgentService.UnitTests;

public sealed class McpRuntimePolicyTests
{
    [Fact]
    public async Task HttpServer_InitializesDiscoversAndCallsTool()
    {
        var handler = new McpHttpHandler();
        var runtime = new McpClientRuntime(new SingleHttpClientFactory(handler));
        var server = Server(McpTransport.Http);

        var definition = Assert.Single(await runtime.DiscoverToolsAsync(server));
        Assert.Equal("echo", definition.Name);
        Assert.True(definition.ReadOnly);

        using var arguments = JsonDocument.Parse("{\"value\":\"hello\"}");
        var result = await runtime.CallToolAsync(server, "echo", arguments.RootElement);

        Assert.True(result.Success, result.Error);
        Assert.Equal("hello", result.Output);
        Assert.Equal(6, handler.RequestCount);
    }

    [Fact]
    public async Task SideEffectTool_RequiresApprovalBeforeRuntimeCall()
    {
        var runtime = new StubMcpRuntime(readOnly: false);
        var tool = new CallMcpTool(
            new StubMcpRepository(Server(McpTransport.Http)),
            runtime,
            new DefaultAgentPolicyEngine(),
            new FixedApprovalCoordinator(false));

        var result = await tool.ExecuteAsync(Request(AgentMode.Auto));

        Assert.False(result.Success);
        Assert.True(result.ApprovalRequired);
        Assert.Equal("rejected", result.ApprovalResult);
        Assert.Equal(0, runtime.CallCount);
    }

    [Fact]
    public async Task ToolTimeout_IsReportedWithoutReplayingCall()
    {
        var runtime = new StubMcpRuntime(readOnly: true, waitForCancellation: true);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:ToolTimeoutSeconds"] = "1",
            })
            .Build();
        var tool = new CallMcpTool(
            new StubMcpRepository(Server(McpTransport.Http)),
            runtime,
            new DefaultAgentPolicyEngine(),
            new FixedApprovalCoordinator(true),
            configuration);

        var result = await tool.ExecuteAsync(Request(AgentMode.Auto));

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.Equal(1, runtime.CallCount);
    }

    private static McpServerDefinition Server(McpTransport transport) => new(
        1,
        "test",
        transport,
        null,
        [],
        "https://mcp.test/rpc",
        new Dictionary<string, string>(),
        true);

    private static ToolExecutionRequest Request(AgentMode mode) => new(
        "call_mcp_tool",
        new Dictionary<string, object?>
        {
            ["server"] = "test",
            ["tool"] = "echo",
            ["arguments"] = new Dictionary<string, object?> { ["value"] = "hello" },
        },
        new ToolExecutionContext("run-mcp", mode, Path.GetTempPath()));

    private sealed class McpHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var methodElement)
                ? methodElement.GetString()
                : null;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
            object payload = method switch
            {
                "initialize" => new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "test", version = "1" },
                    },
                },
                "tools/list" => new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        tools = new[]
                        {
                            new
                            {
                                name = "echo",
                                description = "Echo",
                                inputSchema = new { type = "object" },
                                annotations = new { readOnlyHint = true },
                            },
                        },
                    },
                },
                "tools/call" => new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        content = new[] { new { type = "text", text = "hello" } },
                        isError = false,
                    },
                },
                _ => new { },
            };
            var json = JsonSerializer.Serialize(payload);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (method == "initialize")
                response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-1");
            return response;
        }
    }

    private sealed class SingleHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubMcpRepository(McpServerDefinition server) : IMcpServerRepository
    {
        public Task<IReadOnlyList<McpServerDefinition>> ListEnabledAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpServerDefinition>>([server]);

        public Task<McpServerDefinition?> GetAsync(long id, CancellationToken ct = default) =>
            Task.FromResult<McpServerDefinition?>(id == server.Id ? server : null);
    }

    private sealed class StubMcpRuntime(bool readOnly, bool waitForCancellation = false) : IMcpClientRuntime
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<McpToolDefinition>> DiscoverToolsAsync(
            McpServerDefinition server,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpToolDefinition>>([
                new(server.Id, server.Name, "echo", "Echo", JsonSerializer.SerializeToElement(new { type = "object" }), readOnly),
            ]);

        public async Task<McpCallResult> CallToolAsync(
            McpServerDefinition server,
            string toolName,
            JsonElement arguments,
            CancellationToken ct = default)
        {
            CallCount++;
            if (waitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(true, "ok");
        }
    }

    private sealed class FixedApprovalCoordinator(bool approved) : IApprovalCoordinator
    {
        public Task<ApprovalOutcome> RequestAsync(
            string runId,
            AgentPermissionRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new ApprovalOutcome(approved, null, approved ? "approved" : "rejected"));

        public Task<bool> ResolveAsync(
            string approvalId,
            ResolveApprovalCommand command,
            CancellationToken ct = default) => Task.FromResult(false);
    }
}
