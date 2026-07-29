namespace AgentService.Application.Atlassian;

/// <summary>
/// 穩定的 Atlassian / JIRA 錯誤識別字。
/// 前端依此 code 轉為繁體中文訊息，不依賴英文 error message 字串。
/// </summary>
public static class AtlassianErrorCodes
{
    // ── 連線層 ─────────────────────────────────────────────────────────────────
    public const string NotConfigured      = "ATLASSIAN_NOT_CONFIGURED";
    public const string SecretNotFound     = "ATLASSIAN_SECRET_NOT_FOUND";
    public const string InvalidUrl         = "ATLASSIAN_INVALID_URL";
    public const string AuthFailed         = "ATLASSIAN_AUTH_FAILED";
    public const string Forbidden          = "ATLASSIAN_FORBIDDEN";
    public const string TlsError           = "ATLASSIAN_TLS_ERROR";
    public const string Timeout            = "ATLASSIAN_TIMEOUT";

    // ── JIRA 層 ─────────────────────────────────────────────────────────────────
    public const string JiraKeyInvalid         = "JIRA_KEY_INVALID";
    public const string JiraProjectNotAllowed  = "JIRA_PROJECT_NOT_ALLOWED";
    public const string JiraIssueNotFound      = "JIRA_ISSUE_NOT_FOUND";
    public const string JiraResponseInvalid    = "JIRA_RESPONSE_INVALID";
    public const string JiraRateLimited        = "JIRA_RATE_LIMITED";
    public const string JiraContentTooLarge    = "JIRA_CONTENT_TOO_LARGE";
    public const string JiraImageDownloadFailed= "JIRA_IMAGE_DOWNLOAD_FAILED";

    // ── AI 層 ───────────────────────────────────────────────────────────────────
    public const string AiProviderNotConfigured = "AI_PROVIDER_NOT_CONFIGURED";
    public const string AiAnalysisFailed        = "AI_ANALYSIS_FAILED";
    public const string AnalysisCancelled       = "ANALYSIS_CANCELLED";
}
