namespace AgentService.Application.Models;

public sealed record ProjectImportProgress(
    string OperationId,
    string SourceType,
    string Status,
    string Message,
    bool IsError,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);
