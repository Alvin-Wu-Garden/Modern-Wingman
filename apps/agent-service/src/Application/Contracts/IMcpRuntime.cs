using System.Text.Json;
using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IMcpServerRepository
{
    Task<IReadOnlyList<McpServerDefinition>> ListEnabledAsync(CancellationToken ct = default);
    Task<McpServerDefinition?> GetAsync(long id, CancellationToken ct = default);
}

public interface IMcpClientRuntime
{
    Task<IReadOnlyList<McpToolDefinition>> DiscoverToolsAsync(McpServerDefinition server, CancellationToken ct = default);
    Task<McpCallResult> CallToolAsync(McpServerDefinition server, string toolName, JsonElement arguments, CancellationToken ct = default);
}

/// <summary>
/// Supplies enabled MCP definitions owned by a runtime extension. Definitions are
/// transient and are never written into the user's MCP server table.
/// </summary>
public interface IPluginMcpServerSource
{
    Task<IReadOnlyList<McpServerDefinition>> ListEnabledAsync(CancellationToken ct = default);
}

public interface IMcpToolCatalog
{
    IReadOnlyList<McpServerHealth> Health { get; }
    IReadOnlyList<McpToolDefinition> Tools { get; }
    Task RefreshAsync(CancellationToken ct = default);
}
