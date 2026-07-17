using AgentService.Domain.Models;

namespace AgentService.Application.Models;

public static class LlmTelemetryStatus
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Cancelled = "cancelled";
}

public static class LlmTimeoutKind
{
    public const string FirstToken = "first_token";
    public const string IdleStream = "idle_stream";
    public const string TotalRequest = "total_request";
}

public sealed record LlmTelemetryContext(
    string FeatureArea,
    string? ConversationId = null,
    string? MessageId = null,
    string? ProjectId = null,
    string? RunId = null,
    string? ParentRequestId = null,
    string? TraceId = null,
    string? MetadataJson = null);

public sealed record LlmTelemetryRequestStart(
    LlmTelemetryContext Context,
    ModelProviderProfile Profile,
    string? RequestedModelId,
    bool IsStreaming,
    string? Prompt,
    string? MetadataJson = null);

public sealed record LlmTelemetryRequestHandle(
    string RequestLogId,
    string AttemptId,
    string TraceId,
    DateTimeOffset StartedAt);

public sealed record LlmTelemetryCompletion(
    string? Response,
    TokenUsage? Usage,
    DateTimeOffset CompletedAt,
    long? DurationMs,
    long? TimeToLastByteMs,
    long? AvgInterTokenMs,
    double? TokensPerSecond,
    string? ResolvedModelId = null);

public sealed record LlmTelemetryFailure(
    string Status,
    DateTimeOffset CompletedAt,
    long? DurationMs,
    string? TimeoutKind,
    string? ErrorType,
    string? ErrorCode,
    int? HttpStatus,
    string? ErrorMessage,
    string? RetryReason = null);

public sealed record AuditEventWrite(
    string EventType,
    string TargetType,
    string? TargetId,
    string Action,
    string Result = "success",
    string ActorType = "user",
    string? ActorId = null,
    string? TraceId = null,
    string? DetailsJson = null,
    string? BeforeHash = null,
    string? AfterHash = null);
