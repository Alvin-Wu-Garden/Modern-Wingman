namespace AgentService.Application.Contracts;

public sealed record WingmanPluginManifest(
    string Id, string Name, string Version, int SchemaVersion,
    IReadOnlyList<string> Skills, IReadOnlyList<string> McpServers,
    IReadOnlyList<string> Hooks, IReadOnlyList<string> Assets, string RootPath);

public interface IPluginCatalog
{
    Task<IReadOnlyList<WingmanPluginManifest>> ListAsync(CancellationToken ct = default);
    Task<WingmanPluginManifest> ValidateAsync(string pluginRoot, CancellationToken ct = default);
}

public sealed record AgentEvalSummary(
    int TotalRuns, int CompletedRuns, double RunSuccessRate,
    int ToolCalls, double ToolSuccessRate,
    int VerificationAttempts, double VerificationPassRate,
    int RecoveredRuns, DateTimeOffset From, DateTimeOffset To);

public interface IAgentEvalService
{
    Task<AgentEvalSummary> GetSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
