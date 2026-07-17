namespace AgentService.Application.Models;

public sealed record IndexRunTelemetry(
    string ProjectId,
    string RunId,
    string Mode,
    string Phase,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long ElapsedMilliseconds,
    IReadOnlyDictionary<string, long> StageDurationsMilliseconds,
    int NodeCount = 0,
    int EdgeCount = 0,
    string GraphSchemaVersion = GraphSchemaV2.Version,
    string? AnalysisSnapshotHash = null,
    string? ScopeEscalationReason = null,
    string? ErrorCategory = null,
    string? Error = null);
