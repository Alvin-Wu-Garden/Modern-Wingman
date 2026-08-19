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
    string? ProjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<MessageDto>? Messages = null   // null = list view；非 null = detail view
);

/// <summary>建立一般或專案對話時使用的共用資料。</summary>
public sealed record CreateConversationRequest(string? ProviderProfileId = null);

/// <summary>送出單次對話訊息時使用的輸入資料。</summary>
/// <param name="UserMessage">使用者輸入的訊息。</param>
/// <param name="ProviderProfileId">本輪指定的模型供應商設定；未提供時沿用對話設定。</param>
/// <param name="ModelId">選擇性模型 ID 覆寫；未提供時使用供應商預設模型。</param>
/// <param name="Attachments">只在本輪使用、不寫入對話資料表的附件內容。</param>
/// <param name="TurnId">用戶端回合冪等鍵；重試同一問題時必須沿用。</param>
public sealed record SendMessageRequest(
    string UserMessage,
    string? ProviderProfileId = null,
    string? ModelId = null,
    IReadOnlyList<AttachmentReference>? Attachments = null,
    string? TurnId = null
);

/// <summary>
/// 只在單次對話請求中存在的附件。
/// 前端傳送使用者實際選取的內容，不傳本機路徑，避免開放 CORS 的 localhost API
/// 被其他網頁拿來讀取任意檔案。附件不會寫入對話資料表或 GraphRAG。
/// </summary>
public sealed record AttachmentReference(
    string Name,
    string ContentBase64,
    string? MediaType = null);

/// <summary>
/// Provider API Key + 設定 狀態（GET /api/providers/{id}/key-status）。
/// </summary>
/// <param name="ProfileId">供應商設定識別碼。</param>
/// <param name="DisplayName">前端顯示名稱。</param>
/// <param name="HasStoredKey">是否已保存加密憑證。</param>
/// <param name="StoredBaseUrl">自訂 BYOK 供應商目前保存的 BaseUrl。</param>
/// <param name="SortOrder">目前套用的排序位置。</param>
/// <param name="RuntimeStatus">Copilot PAT Runtime 狀態；其他供應商為 <see langword="null"/>。</param>
public sealed record ProviderKeyStatusDto(
    string ProfileId,
    string DisplayName,
    bool HasStoredKey,
    string? StoredBaseUrl = null,
    int SortOrder = 0,
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
/// Skills Library 用來驗證 GitHub PAT 的請求。
/// AI Provider 憑證改用 PUT /api/providers/{id}/key。
/// </summary>
public sealed record ValidateGithubAccessTokenRequest(string ApiKey);

public sealed record ValidateGithubAccessTokenResult(
    bool Valid,
    string? Error = null,
    string? Scopes = null);

/// <summary>
/// 單一模型資訊（GET /api/providers/{id}/models 回傳的陣列元素）。
/// </summary>
/// <param name="Id">供應商回傳的模型識別碼。</param>
/// <param name="DisplayName">前端顯示名稱。</param>
/// <param name="Group">分組標籤，例如 gpt-4 或 claude-3。</param>
public sealed record ProviderModelDto(
    string Id,
    string DisplayName,
    string Group
);

/// <summary>模型清單暫時或永久無法取得時的結構化錯誤。</summary>
/// <param name="Code">供前端判斷重試策略的穩定錯誤碼。</param>
/// <param name="Message">可安全顯示給使用者的繁體中文訊息。</param>
/// <param name="Retryable">是否可能在不修改設定的情況下重試成功。</param>
public sealed record ProviderModelsErrorDto(
    string Code,
    string Message,
    bool Retryable
);

