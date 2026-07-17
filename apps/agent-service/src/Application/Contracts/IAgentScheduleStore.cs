using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public sealed class AgentSchedule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public required string Task { get; set; }
    public required string WorkspacePath { get; set; }
    public string? ProjectId { get; set; }
    public string? ProviderProfileId { get; set; }
    public AgentMode Mode { get; set; } = AgentMode.Plan;
    public int? IntervalMinutes { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    public bool Enabled { get; set; } = true;
    public string? LastRunId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public interface IAgentScheduleStore
{
    Task<IReadOnlyList<AgentSchedule>> ListAsync(CancellationToken ct = default);
    Task<AgentSchedule?> GetAsync(string id, CancellationToken ct = default);
    Task SaveAsync(AgentSchedule schedule, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<AgentSchedule>> ListDueAsync(DateTimeOffset now, CancellationToken ct = default);
}
