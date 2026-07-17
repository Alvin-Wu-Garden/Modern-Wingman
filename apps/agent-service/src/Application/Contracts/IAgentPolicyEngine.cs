using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IAgentPolicyEngine
{
    AgentPolicyDecision Evaluate(AgentPolicyContext context, AgentPermissionRequest request);
}
