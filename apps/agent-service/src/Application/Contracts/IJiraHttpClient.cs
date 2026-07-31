using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// JIRA REST API v2 HTTP 客戶端介面。
/// 所有方法接收已解密的 <see cref="AtlassianConnection"/> （含明文 SecretValue）；
/// 呼叫前必須確保 SecretValue 不為 null。
/// </summary>
public interface IJiraHttpClient
{
    /// <summary>
    /// 以最小 API 呼叫測試 JIRA 連線，並取得登入者顯示名稱。
    /// 成功時回傳 (true, displayName)；失敗時回傳 (false, null) 且 ErrorCode 已填入。
    /// </summary>
    Task<Application.Atlassian.AtlassianResult<string>> ValidateConnectionAsync(
        AtlassianConnection conn,
        CancellationToken ct = default);

    /// <summary>
    /// 取得議題基本資料（用於使用者確認）。
    /// 僅要求 summary、status、issuetype、priority、assignee、updated、project。
    /// </summary>
    Task<Application.Atlassian.AtlassianResult<Application.Atlassian.JiraIssuePreview>> GetIssuePreviewAsync(
        AtlassianConnection conn,
        string fullIssueKey,
        CancellationToken ct = default);

    /// <summary>
    /// 取得議題完整欄位（含 names/schema expand）與所有分頁留言，
    /// 回傳正規化後的 <see cref="Application.Atlassian.NormalizedJiraIssue"/>。
    /// </summary>
    Task<Application.Atlassian.AtlassianResult<Application.Atlassian.NormalizedJiraIssue>> GetFullIssueAsync(
        AtlassianConnection conn,
        string fullIssueKey,
        CancellationToken ct = default);
}
