using System.Collections.Concurrent;
using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.VersionControl;

public sealed class ProjectImportProgressStore : IProjectImportProgressStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, ProjectImportProgress> entries = new(StringComparer.Ordinal);

    public ProjectImportProgress Begin(string operationId, string sourceType)
    {
        Cleanup();
        var now = DateTimeOffset.UtcNow;
        var progress = new ProjectImportProgress(
            operationId,
            sourceType,
            "running",
            sourceType.Equals("svn", StringComparison.OrdinalIgnoreCase) ? "正在準備 SVN checkout..." : "正在準備 Git clone...",
            false,
            now,
            now);
        entries[operationId] = progress;
        return progress;
    }

    public ProjectImportProgress? Get(string operationId) =>
        entries.TryGetValue(operationId, out var progress) ? progress : null;

    public void Report(string operationId, bool isError, string message) =>
        Update(operationId, current => current with
        {
            Message = Limit(message),
            IsError = isError,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    public void Complete(string operationId, string message) => SetTerminal(operationId, "completed", message, false);

    public void Fail(string operationId, string message) => SetTerminal(operationId, "failed", message, true);

    public void Cancel(string operationId) => SetTerminal(operationId, "cancelled", "已取消取得專案。", false);

    private void SetTerminal(string operationId, string status, string message, bool isError) =>
        Update(operationId, current => current with
        {
            Status = status,
            Message = Limit(message),
            IsError = isError,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    private void Update(string operationId, Func<ProjectImportProgress, ProjectImportProgress> update)
    {
        while (entries.TryGetValue(operationId, out var current))
        {
            if (entries.TryUpdate(operationId, update(current), current)) return;
        }
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var entry in entries)
            if (entry.Value.UpdatedAt < cutoff) entries.TryRemove(entry.Key, out _);
    }

    private static string Limit(string value) =>
        string.IsNullOrWhiteSpace(value) ? "處理中..." : value.Trim()[..Math.Min(value.Trim().Length, 2_000)];
}
