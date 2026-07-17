using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Orchestration;

public sealed class ConfigurationPolicyProfileProvider : IAgentPolicyProfileProvider
{
    public ConfigurationPolicyProfileProvider(IConfiguration configuration)
    {
        var configuredModes = configuration["AgentPolicy:AllowedModes"]?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<AgentMode>(value, true, out var mode) ? mode : (AgentMode?)null)
            .Where(mode => mode is not null)
            .Select(mode => mode!.Value)
            .ToHashSet();
        var modes = configuredModes is { Count: > 0 }
            ? configuredModes
            : Enum.GetValues<AgentMode>().ToHashSet();
        var denied = AgentCapability.None;
        if (configuration.GetValue("AgentPolicy:DisableNetwork", false))
            denied |= AgentCapability.Network;
        if (configuration.GetValue("AgentPolicy:DisableExternalSideEffects", false))
            denied |= AgentCapability.ExternalSideEffect;
        var maximumRisk = Enum.TryParse<AgentRiskLevel>(
            configuration["AgentPolicy:MaximumRiskLevel"],
            true,
            out var parsedRisk)
            ? parsedRisk
            : AgentRiskLevel.Critical;
        Current = new AgentPolicyProfile(modes, denied, maximumRisk);
    }

    public AgentPolicyProfile Current { get; }
}
