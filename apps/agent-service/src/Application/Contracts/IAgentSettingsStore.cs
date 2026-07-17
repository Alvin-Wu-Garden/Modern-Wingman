namespace AgentService.Application.Contracts;

public interface IAgentSettingsStore
{
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
}
