using System.Text.Json;

namespace AgentService.Application.Models;

public enum McpTransport { Stdio, Sse, Http }

public sealed record McpServerDefinition(
    long Id,
    string Name,
    McpTransport Transport,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? Url,
    IReadOnlyDictionary<string, string> Environment,
    bool Enabled);

public sealed record McpToolDefinition(
    long ServerId,
    string ServerName,
    string Name,
    string? Description,
    JsonElement InputSchema,
    bool ReadOnly);

public sealed record McpServerHealth(
    long ServerId,
    string ServerName,
    bool Healthy,
    string? Error,
    DateTimeOffset CheckedAt,
    int ToolCount);

public sealed record McpCallResult(bool Success, string Output, string? Error = null);
