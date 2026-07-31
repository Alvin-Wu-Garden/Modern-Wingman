namespace AgentService.Domain.Models;

public enum AtlassianServiceType { Jira, Wiki }
public enum AtlassianAuthType { Bearer, Basic }

/// <summary>
/// Atlassian 連線設定的 Domain Model。
/// SecretValue 僅在驗證流程的記憶體中存放明文；Repository 讀取時只設定 HasSecret，不回傳明文 PAT。
/// </summary>
public sealed class AtlassianConnection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required AtlassianServiceType ServiceType { get; init; }
    public required string BaseUrl { get; set; }
    public required AtlassianAuthType AuthType { get; set; }
    public string? Username { get; set; }

    /// <summary>明文 PAT，只用於驗證流程，絕不持久化至 DB 或 Log。</summary>
    public string? SecretValue { get; set; }

    /// <summary>DB 中已有 DPAPI 加密 PAT 時為 true；不回傳明文。</summary>
    public bool HasSecret { get; set; }

    public string? ApiVersion { get; set; }
    public bool IsVerified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? VerifiedDisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
