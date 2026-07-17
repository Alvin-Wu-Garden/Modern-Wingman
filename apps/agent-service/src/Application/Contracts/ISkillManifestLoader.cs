using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface ISkillManifestLoader
{
    Task<SkillManifest?> LoadAsync(string skillRoot, CancellationToken ct = default);
}
