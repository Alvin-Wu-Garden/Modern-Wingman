using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public sealed record AgentPolicyProfile(
    IReadOnlySet<AgentMode> AllowedModes,
    AgentCapability DeniedCapabilities,
    AgentRiskLevel MaximumRiskLevel);

public interface IAgentPolicyProfileProvider
{
    AgentPolicyProfile Current { get; }
}
