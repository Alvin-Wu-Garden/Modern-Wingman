using AgentService.Domain.Models;

namespace AgentService.Application.Models;

public sealed record ToolDescriptor(
    string Name,
    string Description,
    AgentCapability Capabilities,
    AgentRiskLevel RiskLevel,
    TimeSpan DefaultTimeout,
    string InputSchemaJson = "{\"type\":\"object\"}",
    string Source = "builtin");

public sealed record ToolExecutionContext(
    string RunId,
    AgentMode Mode,
    string WorkspacePath,
    string? ProjectId = null);

public sealed record ToolExecutionRequest(
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    ToolExecutionContext Context);

public sealed record ToolExecutionResult(
    bool Success,
    string Output,
    string? Error = null,
    int? ExitCode = null,
    bool TimedOut = false,
    long DurationMs = 0,
    bool ApprovalRequired = false,
    string? ApprovalResult = null,
    string? MetadataJson = null);

public sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string?>? Environment = null,
    int MaxOutputCharacters = 1_000_000,
    string? StandardInput = null,
    Func<ProcessOutputLine, CancellationToken, ValueTask>? OnOutput = null);

public sealed record ProcessOutputLine(bool IsError, string Text, DateTimeOffset Timestamp);

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    long DurationMs);
