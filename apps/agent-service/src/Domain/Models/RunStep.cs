namespace AgentService.Domain.Models;

public sealed class RunStep
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string RunId { get; init; }
    public required string Phase { get; init; }
    public int Attempt { get; init; } = 1;
    public string Status { get; set; } = "running";
    public string? CheckpointId { get; set; }
    public string? ErrorSanitized { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
}
