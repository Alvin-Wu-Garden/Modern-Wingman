using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentService.Application.Atlassian;
using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Atlassian;

/// <summary>
/// JIRA REST API v2 HTTP 客戶端。
/// Authorization header 不得出現在任何 Log 輸出；方法內僅記錄 status code 與 error code。
/// </summary>
public sealed class JiraHttpClient(
    IHttpClientFactory httpClientFactory,
    ILogger<JiraHttpClient> logger) : IJiraHttpClient
{
    private const int MaxCommentPages = 50;         // 最多 5000 筆留言
    private const int CommentsPerPage = 100;
    private const long MaxIssueBodyBytes = 2 * 1024 * 1024; // 2 MB

    // ── 連線驗證 ─────────────────────────────────────────────────────────────

    public async Task<AtlassianResult<string>> ValidateConnectionAsync(
        AtlassianConnection conn,
        CancellationToken ct = default)
    {
        using var client = BuildClient(conn);
        var url = NormalizeUrl(conn.BaseUrl, "/rest/api/2/myself");

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<string>(response.StatusCode, url,conn.ServiceType);
            }

            var body = await response.Content.ReadAsStringAsync(ct);

            if (IsHtmlResponse(response, body))
            {
                return AtlassianResult<string>.Fail(
                    AtlassianErrorCodes.AuthFailed,
                    "JIRA 傳回 HTML（可能是 SSO 登入頁），"
                    + "請確認驗證方式與 Token。");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);

                var root = doc.RootElement;

                var displayName =
                    root.TryGetProperty(
                        "displayName",
                        out var displayNameElement)
                    && displayNameElement.ValueKind
                        == JsonValueKind.String
                        ? displayNameElement.GetString()
                        : null;

                var active =
                    !root.TryGetProperty(
                        "active",
                        out var activeElement)
                    || activeElement.ValueKind != JsonValueKind.False;

                if (!active)
                {
                    return AtlassianResult<string>.Fail(
                        AtlassianErrorCodes.AuthFailed,
                        "JIRA 已接受驗證資訊，但帳號目前未啟用。");
                }

                return AtlassianResult<string>.Ok(
                    displayName ?? "（未知使用者）");
            }
            catch (JsonException)
            {
                logger.LogWarning(
                    "JIRA validation returned invalid JSON: "
                    + "serviceType={ServiceType}, "
                    + "statusCode={StatusCode}, "
                    + "contentType={ContentType}",
                    conn.ServiceType,
                    (int)response.StatusCode,
                    response.Content.Headers.ContentType?.MediaType);

                return AtlassianResult<string>.Fail(
                    AtlassianErrorCodes.JiraResponseInvalid,
                    "JIRA 已回傳成功狀態，但回應內容不是有效的 JSON。");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (IsTlsError(ex))
        {
            logger.LogWarning("JIRA TLS error: serviceType={S}", conn.ServiceType);
            return AtlassianResult<string>.Fail(AtlassianErrorCodes.TlsError);
        }
        catch (TaskCanceledException)
        {
            return AtlassianResult<string>.Fail(AtlassianErrorCodes.Timeout);
        }
        catch (HttpRequestException)
        {
            return AtlassianResult<string>.Fail(AtlassianErrorCodes.Timeout);
        }
    }

    // ── 議題預覽 ─────────────────────────────────────────────────────────────

    public async Task<AtlassianResult<JiraIssuePreview>> GetIssuePreviewAsync(
        AtlassianConnection conn,
        string fullIssueKey,
        CancellationToken ct = default)
    {
        using var client = BuildClient(conn);
        var encoded = Uri.EscapeDataString(fullIssueKey);
        var url = NormalizeUrl(conn.BaseUrl,
            $"/rest/api/2/issue/{encoded}?fields=summary,status,issuetype,priority,assignee,updated,project");

        try
        {
            using var response = await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<JiraIssuePreview>(
                    response.StatusCode,
                    url,
                    conn.ServiceType);
            }

            var body = await response.Content
                .ReadAsStringAsync(ct);

            if (IsHtmlResponse(response, body))
            {
                return AtlassianResult<JiraIssuePreview>.Fail(
                    AtlassianErrorCodes.AuthFailed,
                    "JIRA 傳回 HTML，可能是 SSO 登入頁面。");
            }

            return ParseIssuePreview(body, fullIssueKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (IsTlsError(ex))
        {
            return AtlassianResult<JiraIssuePreview>.Fail(AtlassianErrorCodes.TlsError);
        }
        catch (TaskCanceledException)
        {
            return AtlassianResult<JiraIssuePreview>.Fail(AtlassianErrorCodes.Timeout);
        }
        catch (HttpRequestException)
        {
            return AtlassianResult<JiraIssuePreview>.Fail(AtlassianErrorCodes.Timeout);
        }
    }

    // ── 完整議題（含分頁留言） ────────────────────────────────────────────────

    public async Task<AtlassianResult<NormalizedJiraIssue>> GetFullIssueAsync(
        AtlassianConnection conn,
        string fullIssueKey,
        CancellationToken ct = default)
    {
        using var client = BuildClient(conn);
        var encoded = Uri.EscapeDataString(fullIssueKey);

        // 1. 取得完整欄位（含欄位名稱對照）
        var issueUrl = NormalizeUrl(conn.BaseUrl,
            $"/rest/api/2/issue/{encoded}?expand=names,schema");

        try
        {
            using var issueResp = await client.GetAsync(
                issueUrl,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (!issueResp.IsSuccessStatusCode)
            {
                return MapHttpError<NormalizedJiraIssue>(
                    issueResp.StatusCode,
                    issueUrl,
                    conn.ServiceType);
            }

            var issueBody = await issueResp.Content
                .ReadAsStringAsync(ct);

            if (IsHtmlResponse(issueResp, issueBody))
            {
                return AtlassianResult<NormalizedJiraIssue>.Fail(AtlassianErrorCodes.AuthFailed, "JIRA 傳回 HTML，可能是 SSO 登入頁面。");
            }

            if (Encoding.UTF8.GetByteCount(issueBody) > MaxIssueBodyBytes)
                return AtlassianResult<NormalizedJiraIssue>.Fail(AtlassianErrorCodes.JiraContentTooLarge);

            // 2. 分頁取得所有留言
            var comments = await GetAllCommentsAsync(client, conn.BaseUrl, encoded, ct);
            if (comments is null)
                return AtlassianResult<NormalizedJiraIssue>.Fail(AtlassianErrorCodes.JiraResponseInvalid);

            var issue = JiraIssueNormalizer.Normalize(issueBody, comments, fullIssueKey);
            if (issue is null)
                return AtlassianResult<NormalizedJiraIssue>.Fail(AtlassianErrorCodes.JiraResponseInvalid);

            return AtlassianResult<NormalizedJiraIssue>.Ok(issue);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (IsTlsError(ex))
        {
            return AtlassianResult<NormalizedJiraIssue>.Fail(AtlassianErrorCodes.TlsError);
        }
        catch (TaskCanceledException)
        {
            return AtlassianResult<NormalizedJiraIssue>.Fail(AtlassianErrorCodes.Timeout);
        }
        catch (HttpRequestException)
        {
            return AtlassianResult<NormalizedJiraIssue>.Fail(AtlassianErrorCodes.Timeout);
        }
    }

    // ── 分頁留言 ─────────────────────────────────────────────────────────────

    private async Task<List<JiraCommentItem>?> GetAllCommentsAsync(
        HttpClient client,
        string baseUrl,
        string encodedKey,
        CancellationToken ct)
    {
        var all = new List<JiraCommentItem>();

        for (var page = 0; page < MaxCommentPages; page++)
        {
            var startAt = page * CommentsPerPage;
            var url = NormalizeUrl(baseUrl,
                $"/rest/api/2/issue/{encodedKey}/comment?maxResults={CommentsPerPage}&startAt={startAt}");

            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("JIRA comments page {Page} returned {Status}", page, (int)resp.StatusCode);
                return null;
            }

            var responseBody = await resp.Content.ReadAsStringAsync(ct);

            if (IsHtmlResponse(resp, responseBody))
            {
                logger.LogWarning(
                    "JIRA comments page returned HTML: "
                    + "page={Page}, statusCode={StatusCode}",
                    page,
                    (int)resp.StatusCode);

                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseBody);

                var root = doc.RootElement;
                // 後續留言解析

                if (!root.TryGetProperty("comments", out var commentsEl))
                    return all;
                foreach (var commentElement in commentsEl.EnumerateArray())
                {
                    all.Add(new JiraCommentItem
                    {
                        Id =
                            commentElement.TryGetProperty(
                                "id",
                                out var idElement)
                            && idElement.ValueKind == JsonValueKind.String
                                ? idElement.GetString() ?? ""
                                : "",

                        AuthorDisplayName =
                            commentElement.TryGetProperty(
                                "author",
                                out var authorElement)
                            && authorElement.ValueKind == JsonValueKind.Object
                            && authorElement.TryGetProperty(
                                "displayName",
                                out var displayNameElement)
                            && displayNameElement.ValueKind == JsonValueKind.String
                                ? displayNameElement.GetString() ?? "Unknown"
                                : "Unknown",

                        Body =
                            commentElement.TryGetProperty(
                                "body",
                                out var bodyElement)
                            && bodyElement.ValueKind == JsonValueKind.String
                                ? bodyElement.GetString() ?? ""
                                : "",

                        Created =
                            commentElement.TryGetProperty(
                                "created",
                                out var createdElement)
                            && createdElement.ValueKind == JsonValueKind.String
                                ? createdElement.GetString() ?? ""
                                : "",

                        Updated =
                            commentElement.TryGetProperty(
                                "updated",
                                out var updatedElement)
                            && updatedElement.ValueKind == JsonValueKind.String
                                ? updatedElement.GetString()
                                : null,
                    });
                }

                var total = root.TryGetProperty("total", out var tot) ? tot.GetInt32() : 0;
                if (all.Count >= total)
                    break;
        
            }
            catch (JsonException)
            {
                logger.LogWarning(
                    "JIRA comments page returned invalid JSON: "
                    + "page={Page}, statusCode={StatusCode}",
                    page,
                    (int)resp.StatusCode);

                return null;
            }
        }

        return all;
    }

    // ── 輔助方法 ─────────────────────────────────────────────────────────────

    private HttpClient BuildClient(AtlassianConnection conn)
    {
        var client = httpClientFactory.CreateClient("atlassian");
        // Authorization header 在此建立，不進入 Log
        if (conn.AuthType == AuthType.Bearer)
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", conn.SecretValue!);
        else
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{conn.Username}:{conn.SecretValue!}")));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static string NormalizeUrl(string baseUrl, string path)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed + path;
    }

    private static bool IsHtmlResponse(HttpResponseMessage response, string content)
    {
        var mediaType = response.Content
            .Headers
            .ContentType?
            .MediaType ?? "";

        if (mediaType.Contains(
            "html",
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var snippet = content.AsSpan().TrimStart();

        return snippet.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) 
            || snippet.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTlsError(HttpRequestException ex) =>
        ex.InnerException is System.Security.Authentication.AuthenticationException ||
        ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase);

    private static AtlassianResult<T> MapHttpError<T>(
        System.Net.HttpStatusCode status, string url, AtlassianServiceType serviceType)
    {
        var code = (int)status switch
        {
            401 => AtlassianErrorCodes.AuthFailed,
            403 => AtlassianErrorCodes.Forbidden,
            404 => AtlassianErrorCodes.JiraIssueNotFound,
            429 => AtlassianErrorCodes.JiraRateLimited,
            _ => AtlassianErrorCodes.JiraResponseInvalid,
        };
        return AtlassianResult<T>.Fail(code, $"HTTP {(int)status}");
    }

    private static AtlassianResult<JiraIssuePreview> ParseIssuePreview(
        string json, string requestedKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fields = root.TryGetProperty("fields", out var f) ? f : (JsonElement?)null;
            if (fields is null)
                return AtlassianResult<JiraIssuePreview>.Fail(AtlassianErrorCodes.JiraResponseInvalid);

            string Get(string key) =>
                fields.Value.TryGetProperty(key, out var v) ? (v.GetString() ?? "") : "";
            string? GetNested(string key, string nested) =>
                fields.Value.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null
                && v.TryGetProperty(nested, out var n) ? n.GetString() : null;

            var preview = new JiraIssuePreview
            {
                Key = root.TryGetProperty("key", out var k) ? (k.GetString() ?? requestedKey) : requestedKey,
                Summary = Get("summary"),
                Status = GetNested("status", "name") ?? "",
                IssueType = GetNested("issuetype", "name") ?? "",
                Priority = GetNested("priority", "name"),
                Assignee = GetNested("assignee", "displayName"),
                Updated = Get("updated"),
                ProjectKey = GetNested("project", "key") ?? "",
                ProjectName = GetNested("project", "name") ?? "",
            };
            return AtlassianResult<JiraIssuePreview>.Ok(preview);
        }
        catch
        {
            return AtlassianResult<JiraIssuePreview>.Fail(AtlassianErrorCodes.JiraResponseInvalid);
        }
    }
}

// ── 本地型別別名，避免 using 衝突 ────────────────────────────────────────────
file static class AuthType
{
    public const AgentService.Domain.Models.AtlassianAuthType Bearer =
        AgentService.Domain.Models.AtlassianAuthType.Bearer;
}
