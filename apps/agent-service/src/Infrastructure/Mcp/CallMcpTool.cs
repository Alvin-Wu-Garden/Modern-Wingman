using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;

namespace AgentService.Infrastructure.Mcp;

public sealed class CallMcpTool(
    IMcpServerRepository repository,
    IMcpClientRuntime runtime,
    IAgentPolicyEngine policy,
    IApprovalCoordinator approvals,
    IConfiguration? configuration = null) : IAgentTool
{
    private readonly TimeSpan _toolTimeout = TimeSpan.FromSeconds(
        Math.Clamp(configuration?.GetValue("Mcp:ToolTimeoutSeconds", 120) ?? 120, 1, 900));

    public ToolDescriptor Descriptor { get; } = new(
        "call_mcp_tool",
        "Call a discovered tool on an enabled MCP server.",
        AgentCapability.Network | AgentCapability.ExternalSideEffect,
        AgentRiskLevel.High,
        TimeSpan.FromMinutes(2),
        """
        {
          "type": "object",
          "required": ["server", "tool", "arguments"],
          "properties": {
            "server": { "type": "string" },
            "tool": { "type": "string" },
            "arguments": { "type": "object" }
          },
          "additionalProperties": false
        }
        """,
        "mcp");

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var serverName = RequireString(request.Arguments, "server");
            var toolName = RequireString(request.Arguments, "tool");
            var server = (await repository.ListEnabledAsync(ct)).FirstOrDefault(candidate =>
                string.Equals(candidate.Name, serverName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Id.ToString(), serverName, StringComparison.Ordinal));
            if (server is null)
                return new ToolExecutionResult(false, "", $"Enabled MCP server not found: {serverName}");

            var definition = (await runtime.DiscoverToolsAsync(server, ct)).FirstOrDefault(candidate =>
                string.Equals(candidate.Name, toolName, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
                return new ToolExecutionResult(false, "", $"MCP tool not found: {server.Name}/{toolName}");

            var capabilities = definition.ReadOnly
                ? AgentCapability.Read | AgentCapability.Network
                : AgentCapability.Network | AgentCapability.ExternalSideEffect;
            var risk = definition.ReadOnly ? AgentRiskLevel.Low : AgentRiskLevel.High;
            var permission = new AgentPermissionRequest(
                Descriptor.Name,
                capabilities,
                risk,
                $"{server.Name}/{definition.Name}",
                request.Context.WorkspacePath,
                definition.Description ?? Descriptor.Description);
            var decision = policy.Evaluate(
                new AgentPolicyContext(request.Context.Mode, request.Context.WorkspacePath),
                permission);
            if (decision.Kind == PolicyDecisionKind.Deny)
                return new ToolExecutionResult(false, "", decision.Reason);

            var approvalRequired = decision.Kind == PolicyDecisionKind.RequireApproval;
            if (approvalRequired)
            {
                var approval = await approvals.RequestAsync(request.Context.RunId, permission, ct);
                if (!approval.Approved)
                {
                    return new ToolExecutionResult(
                        false,
                        "",
                        approval.Comment ?? "MCP tool call rejected.",
                        ApprovalRequired: true,
                        ApprovalResult: "rejected");
                }
            }

            var arguments = ReadArguments(request.Arguments);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_toolTimeout);
            var result = await runtime.CallToolAsync(server, definition.Name, arguments, timeout.Token);
            return new ToolExecutionResult(
                result.Success,
                result.Success ? UntrustedContent.Wrap($"mcp:{server.Name}/{definition.Name}", result.Output) : result.Output,
                result.Error,
                TimedOut: timeout.IsCancellationRequested && !ct.IsCancellationRequested,
                ApprovalRequired: approvalRequired,
                ApprovalResult: approvalRequired ? "approved" : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolExecutionResult(false, "", "MCP tool call timed out.", TimedOut: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolExecutionResult(false, "", ex.Message);
        }
    }

    private static JsonElement ReadArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("arguments", out var value) || value is null)
            return JsonSerializer.SerializeToElement(new { });
        return value is JsonElement element
            ? element.Clone()
            : JsonSerializer.SerializeToElement(value);
    }

    private static string RequireString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new ArgumentException($"Argument '{name}' is required.");
}
