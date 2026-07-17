using System.Text.Json;
using System.Text.Json.Serialization;
using AgentService.Domain.Models;

namespace AgentService.Application.Models;

/// <summary>
/// 透過 gRPC server-side streaming 傳送給 Rust 層的事件 DTO。
/// EventType 對應 contracts 套件中的 RunEventType 字串。
/// </summary>
public sealed class RunStreamEvent
{
    public string RunId { get; init; } = "";
    public string EventType { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>事件專屬資料的 JSON 字串；接收端反序列化為對應型別。</summary>
    public string PayloadJson { get; init; } = "{}";

    // ── Factory 方法 ──────────────────────────────────────────────────────────

    public static RunStreamEvent Started(string runId) =>
        Build(runId, "run:started", new { });

    public static RunStreamEvent Token(string runId, string token) =>
        Build(runId, "run:token", new { token });

    public static RunStreamEvent ToolCall(string runId, string toolName, object? toolInput) =>
        Build(runId, "run:tool-call", new { toolName, toolInput });

    public static RunStreamEvent ToolResult(string runId, string toolName, object? result) =>
        Build(runId, "run:tool-result", new { toolName, result });

    public static RunStreamEvent ToolOutput(string runId, string toolName, ProcessOutputLine line) =>
        Build(runId, "run:tool-output", new { toolName, stream = line.IsError ? "stderr" : "stdout", line.Text });

    public static RunStreamEvent Message(string runId, string content) =>
        Build(runId, "run:message", new { content });

    public static RunStreamEvent Completed(string runId) =>
        Build(runId, "run:completed", new { });

    public static RunStreamEvent Failed(string runId, string error) =>
        Build(runId, "run:failed", new { error });

    public static RunStreamEvent Cancelled(string runId) =>
        Build(runId, "run:cancelled", new { });

    public static RunStreamEvent Usage(string runId, TokenUsage usage) =>
        Build(runId, "run:usage", new
        {
            inputTokens = usage.InputTokens,
            outputTokens = usage.OutputTokens,
            totalTokens = usage.TotalTokens,
        });

    /// <summary>WS4：工作流階段轉換（explore / plan / code / verify）。</summary>
    public static RunStreamEvent Phase(string runId, string phase, string detail = "") =>
        Build(runId, "run:phase", new { phase, detail });

    /// <summary>WS4：Plan mode 產出的計畫，等待使用者核准。</summary>
    public static RunStreamEvent PlanReady(string runId, string plan) =>
        Build(runId, "run:plan", new { plan });

    /// <summary>WS4：驗證迴圈結果。</summary>
    public static RunStreamEvent VerifyResult(string runId, bool success, int attempt, string output) =>
        Build(runId, "run:verify", new { success, attempt, output });

    public static RunStreamEvent ApprovalRequested(string runId, AgentApproval approval) =>
        Build(runId, "run:approval-requested", new
        {
            approvalId = approval.Id,
            approval.Operation,
            approval.Target,
            approval.Summary,
            capabilities = approval.Capabilities.ToString(),
            riskLevel = approval.RiskLevel.ToString().ToLowerInvariant(),
            createdAt = approval.CreatedAt,
        });

    public static RunStreamEvent ApprovalResolved(string runId, AgentApproval approval) =>
        Build(runId, "run:approval-resolved", new
        {
            approvalId = approval.Id,
            status = approval.Status.ToString().ToLowerInvariant(),
            scope = approval.Scope?.ToString().ToLowerInvariant(),
            approval.DecisionComment,
            approval.ResolvedAt,
        });

    public static RunStreamEvent ChangeSetAvailable(string runId, ChangeSet changeSet) =>
        Build(runId, "run:changeset", new
        {
            changeSet.CheckpointId,
            fileCount = changeSet.Files.Count,
            files = changeSet.Files.Select(file => new
            {
                file.RelativePath,
                kind = file.Kind.ToString().ToLowerInvariant(),
                file.Binary,
                file.UnifiedDiff,
            }),
        });

    // ─────────────────────────────────────────────────────────────────────────

    private static RunStreamEvent Build(string runId, string type, object payload) =>
        new()
        {
            RunId = runId,
            EventType = type,
            PayloadJson = JsonSerializer.Serialize(payload, SerializerOptions),
        };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
