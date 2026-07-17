using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Tools;

public abstract class PolicyEnforcedAgentTool(
    IAgentPolicyEngine policyEngine,
    IApprovalCoordinator approvalCoordinator) : IAgentTool
{
    public abstract ToolDescriptor Descriptor { get; }

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
    {
        var permission = BuildPermissionRequest(request);
        var decision = policyEngine.Evaluate(
            new AgentPolicyContext(request.Context.Mode, request.Context.WorkspacePath),
            permission);

        if (decision.Kind == PolicyDecisionKind.Deny)
            return new ToolExecutionResult(false, "", decision.Reason);

        var approvalRequired = decision.Kind == PolicyDecisionKind.RequireApproval;
        if (approvalRequired)
        {
            var approval = await approvalCoordinator.RequestAsync(
                request.Context.RunId,
                permission,
                ct);
            if (!approval.Approved)
            {
                return new ToolExecutionResult(
                    false,
                    "",
                    approval.Comment ?? "Operation rejected by the user.",
                    ApprovalRequired: true,
                    ApprovalResult: "rejected");
            }
        }

        try
        {
            var result = await ExecuteCoreAsync(request, ct);
            return result with
            {
                ApprovalRequired = approvalRequired,
                ApprovalResult = approvalRequired ? "approved" : null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolExecutionResult(false, "", ex.Message);
        }
    }

    protected virtual AgentPermissionRequest BuildPermissionRequest(
        ToolExecutionRequest request) => new(
        Descriptor.Name,
        Descriptor.Capabilities,
        Descriptor.RiskLevel,
        WorkingDirectory: request.Context.WorkspacePath,
        Summary: Descriptor.Description);

    protected abstract Task<ToolExecutionResult> ExecuteCoreAsync(
        ToolExecutionRequest request,
        CancellationToken ct);

    protected static string RequireString(
        IReadOnlyDictionary<string, object?> arguments,
        string name)
    {
        if (!arguments.TryGetValue(name, out var value) ||
            value is not string text ||
            string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException($"Argument '{name}' is required.");
        }
        return text;
    }
}
