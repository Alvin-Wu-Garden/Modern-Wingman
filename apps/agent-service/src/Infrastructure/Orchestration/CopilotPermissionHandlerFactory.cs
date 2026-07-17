using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using WingmanAgentMode = AgentService.Domain.Models.AgentMode;

#pragma warning disable GHCP001 // SDK permission decision union is the supported custom-handler API.

namespace AgentService.Infrastructure.Orchestration;

public sealed class CopilotPermissionHandlerFactory(
    IAgentPolicyEngine policyEngine,
    IApprovalCoordinator approvalCoordinator,
    ILogger<CopilotPermissionHandlerFactory> logger)
{
    public Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> Create(
        WingmanAgentMode mode,
        string? workspacePath,
        string? runId = null)
    {
        var context = new AgentPolicyContext(mode, workspacePath);
        return async (sdkRequest, _) =>
        {
            var request = MapRequest(sdkRequest, workspacePath);
            var decision = policyEngine.Evaluate(context, request);
            logger.LogDebug(
                "Permission {Operation} evaluated as {Decision} in {Mode}: {Reason}",
                request.Operation,
                decision.Kind,
                mode,
                decision.Reason);

            return decision.Kind switch
            {
                PolicyDecisionKind.Allow => PermissionDecision.ApproveOnce(),
                PolicyDecisionKind.Deny => PermissionDecision.Reject(decision.Reason),
                PolicyDecisionKind.RequireApproval when !string.IsNullOrWhiteSpace(runId) =>
                    await ResolveApprovalAsync(runId, request),
                _ => PermissionDecision.UserNotAvailable(),
            };
        };
    }

    private async Task<PermissionDecision> ResolveApprovalAsync(
        string runId,
        AgentPermissionRequest request)
    {
        var outcome = await approvalCoordinator.RequestAsync(runId, request);
        return outcome.Approved
            ? PermissionDecision.ApproveOnce()
            : PermissionDecision.Reject(outcome.Comment ?? "The user rejected this operation.");
    }

    internal static AgentPermissionRequest MapRequest(
        PermissionRequest request,
        string? workspacePath) => request switch
    {
        PermissionRequestRead read => MapRead(read, workspacePath),

        PermissionRequestWrite write => new AgentPermissionRequest(
            "write_file",
            AgentCapability.Write,
            AgentRiskLevel.Medium,
            ResolveTarget(workspacePath, write.FileName),
            workspacePath,
            write.Intention),

        PermissionRequestShell shell => MapShell(shell, workspacePath),

        PermissionRequestUrl url => new AgentPermissionRequest(
            "fetch_url",
            AgentCapability.Read | AgentCapability.Network,
            AgentRiskLevel.Low,
            url.Url,
            workspacePath,
            url.Intention),

        PermissionRequestMcp mcp => new AgentPermissionRequest(
            $"mcp:{mcp.ServerName}/{mcp.ToolName}",
            mcp.ReadOnly
                ? AgentCapability.Read
                : AgentCapability.ExternalSideEffect,
            mcp.ReadOnly ? AgentRiskLevel.Low : AgentRiskLevel.High,
            mcp.ServerName,
            workspacePath,
            mcp.ToolTitle),

        PermissionRequestCustomTool tool => new AgentPermissionRequest(
            $"tool:{tool.ToolName}",
            AgentCapability.Execute | AgentCapability.ExternalSideEffect,
            AgentRiskLevel.High,
            tool.ToolName,
            workspacePath,
            tool.ToolDescription),

        _ => new AgentPermissionRequest(
            request.Kind,
            AgentCapability.ExternalSideEffect,
            AgentRiskLevel.High,
            WorkingDirectory: workspacePath,
            Summary: "Unknown SDK permission request type."),
    };

    private static AgentPermissionRequest MapShell(
        PermissionRequestShell shell,
        string? workspacePath)
    {
        var command = shell.FullCommandText ?? "";
        var (capabilities, risk) = CommandPolicyClassifier.ClassifyRaw(command);
        if (IsReadOnlyShell(shell))
        {
            capabilities = AgentCapability.Read | AgentCapability.Execute;
            risk = AgentRiskLevel.Low;
        }
        if (shell.HasWriteFileRedirection)
            capabilities |= AgentCapability.Write;
        if (shell.PossibleUrls is { Length: > 0 })
            capabilities |= AgentCapability.Network;

        return new AgentPermissionRequest(
            "run_command",
            capabilities,
            risk,
            command,
            workspacePath,
            shell.Warning ?? shell.Intention);
    }

    private static bool IsReadOnlyShell(PermissionRequestShell shell) =>
        shell.Commands is { Length: > 0 } && shell.Commands.All(command => command.ReadOnly);

    private static AgentPermissionRequest MapRead(PermissionRequestRead read, string? workspacePath)
    {
        var target = ResolveTarget(workspacePath, read.Path);
        try
        {
            if (!string.IsNullOrWhiteSpace(workspacePath) && !string.IsNullOrWhiteSpace(target))
                target = WorkspacePathGuard.ResolveReadable(workspacePath, target);
            return new AgentPermissionRequest(
                "read_file", AgentCapability.Read, AgentRiskLevel.Low,
                target, workspacePath, read.Intention);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new AgentPermissionRequest(
                "read_credential_file",
                AgentCapability.Read | AgentCapability.ExternalSideEffect,
                AgentRiskLevel.Critical,
                target,
                workspacePath,
                ex.Message);
        }
    }

    private static string? ResolveTarget(string? workspacePath, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return target;
        if (Path.IsPathFullyQualified(target) || string.IsNullOrWhiteSpace(workspacePath))
            return target;
        return Path.GetFullPath(Path.Combine(workspacePath, target));
    }

}

#pragma warning restore GHCP001
