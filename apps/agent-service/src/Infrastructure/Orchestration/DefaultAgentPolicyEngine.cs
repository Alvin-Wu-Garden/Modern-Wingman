using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Orchestration;

public sealed class DefaultAgentPolicyEngine(IAgentPolicyProfileProvider? profileProvider = null) : IAgentPolicyEngine
{
    public AgentPolicyDecision Evaluate(
        AgentPolicyContext context,
        AgentPermissionRequest request)
    {
        var profile = profileProvider?.Current;
        if (profile is not null)
        {
            if (!profile.AllowedModes.Contains(context.Mode))
                return AgentPolicyDecision.Deny($"Agent mode '{context.Mode}' is disabled by the administrator policy profile.");
            if ((request.Capabilities & profile.DeniedCapabilities) != 0)
                return AgentPolicyDecision.Deny("The requested capability is disabled by the administrator policy profile.");
            if (request.RiskLevel > profile.MaximumRiskLevel)
                return AgentPolicyDecision.Deny("The operation exceeds the administrator policy profile risk limit.");
        }

        if (request.RiskLevel == AgentRiskLevel.Critical)
            return AgentPolicyDecision.Deny("Critical-risk operations are blocked by policy.");

        if (WritesOutsideWorkspace(context.WorkspacePath, request))
            return AgentPolicyDecision.Deny("Writing outside the active workspace is not allowed.");

        return context.Mode switch
        {
            AgentMode.Ask => EvaluateReadOnlyMode(request, "Ask"),
            AgentMode.Plan => EvaluateReadOnlyMode(request, "Plan"),
            AgentMode.Auto => EvaluateAutoMode(context, request),
            AgentMode.FullAuto => EvaluateFullAutoMode(context, request),
            _ => AgentPolicyDecision.Deny("Unknown agent mode."),
        };
    }

    private static AgentPolicyDecision EvaluateReadOnlyMode(
        AgentPermissionRequest request,
        string mode)
    {
        var forbidden = AgentCapability.Write |
                        AgentCapability.ExternalSideEffect |
                        AgentCapability.Destructive;

        if ((request.Capabilities & forbidden) != 0)
            return AgentPolicyDecision.Deny($"{mode} mode is read-only.");

        if ((request.Capabilities & AgentCapability.Execute) != 0 &&
            request.RiskLevel > AgentRiskLevel.Low)
        {
            return AgentPolicyDecision.Deny($"{mode} mode only permits low-risk analysis commands.");
        }

        return AgentPolicyDecision.Allow($"Allowed by {mode} mode read-only policy.");
    }

    private static AgentPolicyDecision EvaluateAutoMode(
        AgentPolicyContext context,
        AgentPermissionRequest request)
    {
        if (context.IsProtectedRef ||
            (request.Capabilities & (AgentCapability.ExternalSideEffect |
                                     AgentCapability.Destructive)) != 0 ||
            request.RiskLevel >= AgentRiskLevel.High)
        {
            return AgentPolicyDecision.RequireApproval(
                "Auto mode requires approval for high-risk or external side effects.");
        }

        return AgentPolicyDecision.Allow("Allowed by Auto mode policy.");
    }

    private static AgentPolicyDecision EvaluateFullAutoMode(
        AgentPolicyContext context,
        AgentPermissionRequest request)
    {
        if (context.IsProtectedRef)
            return AgentPolicyDecision.RequireApproval("Protected refs always require approval.");

        if (request.RiskLevel == AgentRiskLevel.High &&
            (request.Capabilities & AgentCapability.Destructive) != 0)
        {
            return AgentPolicyDecision.RequireApproval(
                "Destructive high-risk operations require explicit approval.");
        }

        return AgentPolicyDecision.Allow("Allowed by Full Auto mode policy.");
    }

    private static bool WritesOutsideWorkspace(
        string? workspacePath,
        AgentPermissionRequest request)
    {
        if ((request.Capabilities & AgentCapability.Write) == 0 ||
            string.IsNullOrWhiteSpace(request.Target) ||
            string.IsNullOrWhiteSpace(workspacePath) ||
            !Path.IsPathFullyQualified(request.Target))
        {
            return false;
        }

        var workspace = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(request.Target);

        return !target.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(
                   target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   workspace.TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }
}
