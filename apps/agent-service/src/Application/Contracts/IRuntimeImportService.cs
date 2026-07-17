using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public sealed record RuntimeImportResult(
    string Kind,
    string DestinationPath,
    string? ExecutablePath,
    int FileCount);

public interface IRuntimeImportService
{
    Task<RuntimeImportResult> ImportRuntimeAsync(
        SkillRuntimeKind kind,
        string sourcePath,
        CancellationToken ct = default);

    Task<RuntimeImportResult> ImportPackageCacheAsync(
        SkillRuntimeKind kind,
        string sourcePath,
        CancellationToken ct = default);
}
