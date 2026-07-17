using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;

namespace AgentService.Infrastructure.Mcp;

public sealed class McpToolCatalog(IMcpServerRepository repository, IEnumerable<IPluginMcpServerSource> pluginSources, IMcpClientRuntime runtime, IManagedToolRegistry registry, IAgentPolicyEngine policy, IApprovalCoordinator approvals) : IMcpToolCatalog
{
    private readonly object _gate = new();
    private IReadOnlyList<McpServerHealth> _health = [];
    private IReadOnlyList<McpToolDefinition> _tools = [];
    public IReadOnlyList<McpServerHealth> Health { get { lock (_gate) return _health; } }
    public IReadOnlyList<McpToolDefinition> Tools { get { lock (_gate) return _tools; } }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var health = new List<McpServerHealth>(); var tools = new List<McpToolDefinition>();
        var servers = new List<McpServerDefinition>(await repository.ListEnabledAsync(ct));
        foreach (var source in pluginSources)
        {
            try { servers.AddRange(await source.ListEnabledAsync(ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { health.Add(new(0, "plugin-mcp", false, ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message, DateTimeOffset.UtcNow, 0)); }
        }
        var expectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers.GroupBy(item => item.Id).Select(group => group.First()))
        {
            var source = $"mcp:{server.Name}";
            expectedSources.Add(source);
            try
            {
                var discovered = await runtime.DiscoverToolsAsync(server, ct); tools.AddRange(discovered);
                registry.ReplaceSource(source, discovered.Select(tool => (IAgentTool)new McpAgentTool(server, tool, runtime, policy, approvals)).ToList());
                health.Add(new(server.Id, server.Name, true, null, DateTimeOffset.UtcNow, discovered.Count));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                registry.ReplaceSource(source, []);
                health.Add(new(server.Id, server.Name, false, ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message, DateTimeOffset.UtcNow, 0));
            }
        }
        foreach (var source in registry.ListTools().Select(tool => tool.Source).Where(source => source.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Where(source => !expectedSources.Contains(source)).ToList())
            registry.ReplaceSource(source, []);
        lock (_gate) { _health=health; _tools=tools; }
    }

    private sealed class McpAgentTool(McpServerDefinition server, McpToolDefinition tool, IMcpClientRuntime runtime, IAgentPolicyEngine policy, IApprovalCoordinator approvals) : PolicyEnforcedAgentTool(policy, approvals)
    {
        public override ToolDescriptor Descriptor { get; } = new($"mcp:{server.Name}:{tool.Name}", tool.Description ?? $"Call {tool.Name} on MCP server {server.Name}.", tool.ReadOnly ? AgentCapability.Read | AgentCapability.Network : AgentCapability.Network | AgentCapability.ExternalSideEffect, tool.ReadOnly ? AgentRiskLevel.Low : AgentRiskLevel.High, TimeSpan.FromMinutes(2),tool.InputSchema.GetRawText(),$"mcp:{server.Name}");
        protected override AgentPermissionRequest BuildPermissionRequest(ToolExecutionRequest request) => new(Descriptor.Name, Descriptor.Capabilities, Descriptor.RiskLevel, server.Name, request.Context.WorkspacePath, Descriptor.Description);
        protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct) { var result=await runtime.CallToolAsync(server,tool.Name,JsonSerializer.SerializeToElement(request.Arguments),ct); return new(result.Success,result.Success?UntrustedContent.Wrap($"mcp:{server.Name}/{tool.Name}",result.Output):result.Output,result.Error); }
    }
}
