namespace AgentService.Application.Models;

public sealed record MessageDto(
    string Id,
    string Role,      // "user" | "assistant"
    string Content,
    DateTimeOffset CreatedAt
);

public sealed record ConversationDto(
    string Id,
    string Title,
    string? ProviderProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<MessageDto>? Messages = null   // null = list view；非 null = detail view
);

public sealed record SendMessageRequest(
    string UserMessage,
    string? ProviderProfileId = null,
    /// <summary>選擇性模型 ID 覆蓋，例如 "gpt-4o"、"claude-sonnet-4-5"。null = 使用 profile 預設值。</summary>
    string? ModelId = null,
    /// <summary>ask | plan | auto | full_auto；未指定時使用 plan。</summary>
    string? AgentMode = null,
    IReadOnlyList<AttachmentReference>? Attachments = null,
    string? ProjectId = null,
    bool IncludeUncommittedChanges = true
);

public sealed record AttachmentReference(string Path, string? Name = null, string? MediaType = null);

/// <summary>
/// Provider API Key + 設定 狀態（GET /api/providers/{id}/key-status）。
/// </summary>
public sealed record ProviderKeyStatusDto(
    string ProfileId,
    string DisplayName,
    bool HasEnvVar,
    bool HasStoredKey,
    /// <summary>DB 中的自訂 BaseUrl（僅 custom-byok 使用）。</summary>
    string? StoredBaseUrl = null,
    /// <summary>目前套用的排序位置。</summary>
    int SortOrder = 0,
    /// <summary>Copilot PAT runtime 狀態；其他 provider 為 null。</summary>
    CopilotRuntimeStatusDto? RuntimeStatus = null
);

/// <summary>不含任何憑證的 bundled Copilot runtime 狀態。</summary>
public sealed record CopilotRuntimeStatusDto(
    string State,
    bool IsAuthenticated,
    string? Login = null,
    string? AuthType = null,
    string? CopilotPlan = null,
    int? ModelCount = null,
    string? Error = null
);

public sealed record SetApiKeyRequest(string ApiKey, string? BaseUrl = null);

/// <summary>PUT /api/providers/reorder 的 request body — profileId 陣列，index 即為新順序。</summary>
public sealed record ReorderProvidersRequest(List<string> Order);

/// <summary>
/// Request used by the Skills library to validate its GitHub PAT.
/// AI provider credentials use PUT /api/providers/{id}/key instead.
/// </summary>
public sealed record ValidateGithubAccessTokenRequest(string ApiKey);

public sealed record ValidateGithubAccessTokenResult(
    bool Valid,
    string? Error = null,
    string? Scopes = null);

/// <summary>
/// 單一模型資訊（GET /api/providers/{id}/models 回傳的陣列元素）。
/// </summary>
public sealed record ProviderModelDto(
    string Id,
    string DisplayName,
    /// <summary>分組標籤，例如 "gpt-4"、"claude-3"。</summary>
    string Group
);

