namespace AgentService.Application.Contracts;

public sealed record SkillRiskAssessment(string Level, string? Notes);

public interface ISkillRiskProvider
{
    Task<SkillRiskAssessment?> GetAsync(string skillName, CancellationToken ct = default);
}
