namespace AgentService.Infrastructure.Telemetry;

public sealed class LlmTelemetryOptions
{
    public const string SectionName = "LlmTelemetry";

    public int FirstTokenTimeoutSeconds { get; set; } = 60;
    public int IdleStreamTimeoutSeconds { get; set; } = 120;
    public int TotalRequestTimeoutSeconds { get; set; } = 600;
    public bool FallbackEnabled { get; set; }
    public int MaxFirstTokenRetriesPerTarget { get; set; } = 1;
    public bool AllowCrossProviderFallback { get; set; }
    public List<LlmFallbackTargetOptions> FallbackTargets { get; set; } = [];

    /// <summary>
    /// Enterprise-safe default: keep hashes and metadata, not prompt/response text.
    /// </summary>
    public bool StoreContentPreviews { get; set; } = false;

    public int ContentPreviewChars { get; set; } = 240;
}

public sealed class LlmFallbackTargetOptions
{
    public string ProviderProfileId { get; set; } = "";
    public string? ModelId { get; set; }
}
