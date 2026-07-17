namespace AgentService.Domain.Models;

/// <summary>
/// Normalized provider profile snapshot used by AI observability records.
/// API keys are intentionally never stored here.
/// </summary>
public sealed class AiProviderProfileRecord
{
    public string ProfileId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? ProviderType { get; set; }
    public string? BaseUrlHost { get; set; }
    public string? WireApi { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<AiModelRecord> Models { get; set; } = [];
}

/// <summary>Normalized model record per provider profile.</summary>
public sealed class AiModelRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProviderProfileId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ModelFamily { get; set; }
    public bool? SupportsStreaming { get; set; }
    public int? ContextWindow { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AiProviderProfileRecord? ProviderProfile { get; set; }
}

/// <summary>
/// One logical AI request. This is provider-agnostic and covers chat, GraphRAG,
/// title generation, workflow planning, and future agent/tool flows.
/// </summary>
public sealed class AiRequestLogRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TraceId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentRequestId { get; set; }
    public string FeatureArea { get; set; } = "";
    public string? ConversationId { get; set; }
    public string? MessageId { get; set; }
    public string? ProjectId { get; set; }
    public string? RunId { get; set; }

    public string ProviderProfileId { get; set; } = "";
    public string? RequestedModelRecordId { get; set; }
    public string? ResolvedModelRecordId { get; set; }
    public bool IsStreaming { get; set; }

    public string Status { get; set; } = "running";
    public string? TimeoutKind { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FirstTokenAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public long? TimeToFirstTokenMs { get; set; }
    public long? TimeToLastByteMs { get; set; }
    public long? AvgInterTokenMs { get; set; }
    public double? TokensPerSecond { get; set; }

    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    public double? EstimatedCostUsd { get; set; }

    public string? PromptHash { get; set; }
    public string? ResponseHash { get; set; }
    public string? PromptPreviewRedacted { get; set; }
    public string? ResponsePreviewRedacted { get; set; }
    public bool ContentStored { get; set; }

    public string? ErrorType { get; set; }
    public string? ErrorCode { get; set; }
    public int? HttpStatus { get; set; }
    public string? ErrorMessageSanitized { get; set; }

    public string? ProviderSnapshotJson { get; set; }
    public string? ModelSnapshotJson { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AiProviderProfileRecord? ProviderProfile { get; set; }
    public AiModelRecord? RequestedModel { get; set; }
    public AiModelRecord? ResolvedModel { get; set; }
    public List<AiRequestAttemptRecord> Attempts { get; set; } = [];
}

/// <summary>One physical attempt for an AI request. Future retries/fallbacks add more rows.</summary>
public sealed class AiRequestAttemptRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RequestLogId { get; set; } = "";
    public int AttemptNo { get; set; } = 1;

    public string ProviderProfileId { get; set; } = "";
    public string? RequestedModelRecordId { get; set; }
    public string? ResolvedModelRecordId { get; set; }
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FirstTokenAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? DurationMs { get; set; }
    public long? TimeToFirstTokenMs { get; set; }
    public int? HttpStatus { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorType { get; set; }
    public string? TimeoutKind { get; set; }
    public string? ErrorMessageSanitized { get; set; }
    public string? RetryReason { get; set; }
    public string? ProviderSnapshotJson { get; set; }
    public string? ModelSnapshotJson { get; set; }
    public string? MetadataJson { get; set; }

    public AiRequestLogRecord? RequestLog { get; set; }
    public AiProviderProfileRecord? ProviderProfile { get; set; }
    public AiModelRecord? RequestedModel { get; set; }
    public AiModelRecord? ResolvedModel { get; set; }
}

/// <summary>Tool, MCP, skill, and internal action call audit attached to an AI request.</summary>
public sealed class AiToolCallLogRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RequestLogId { get; set; } = "";
    public string? ToolCallId { get; set; }
    public string ToolName { get; set; } = "";
    public string ToolType { get; set; } = "";
    public string? McpServerId { get; set; }
    public string? SkillId { get; set; }
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? InputHash { get; set; }
    public string? OutputHash { get; set; }
    public string? InputPreviewRedacted { get; set; }
    public string? OutputPreviewRedacted { get; set; }
    public bool ApprovalRequired { get; set; }
    public string? ApprovalResult { get; set; }
    public string? ErrorMessageSanitized { get; set; }
    public string? MetadataJson { get; set; }

    public AiRequestLogRecord? RequestLog { get; set; }
}

/// <summary>General enterprise audit event table for settings and security-relevant actions.</summary>
public sealed class AuditEventRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? TraceId { get; set; }
    public string ActorType { get; set; } = "system";
    public string? ActorId { get; set; }
    public string EventType { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string? TargetId { get; set; }
    public string Action { get; set; } = "";
    public string Result { get; set; } = "success";
    public string? IpAddress { get; set; }
    public string? MachineName { get; set; }
    public string? AppVersion { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public string? DetailsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
