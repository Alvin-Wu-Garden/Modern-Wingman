using System.Text.Json;
using AgentService.Application.Atlassian;

namespace AgentService.Infrastructure.Atlassian;

/// <summary>
/// 將 JIRA REST API v2 回傳的 JSON 正規化為 <see cref="NormalizedJiraIssue"/>。
/// 自訂欄位 ID 集中於此，避免散落在多個地方。
/// </summary>
public static class JiraIssueNormalizer
{
    /// <summary>
    /// 自訂欄位白名單：欄位 ID → 顯示名稱。
    /// 環境不存在的欄位會被跳過，不視為錯誤。
    /// </summary>
    private static readonly Dictionary<string, string> KnownCustomFields = new()
    {
        // 調整為目標 JIRA 環境的實際 custom field ID
        ["customfield_10100"] = "問題分析",
        ["customfield_10101"] = "測試案例",
        ["customfield_10102"] = "IT 負責人",
        ["customfield_10103"] = "預計交測日",
        ["customfield_10104"] = "完成率",
    };

    public static NormalizedJiraIssue? Normalize(
        string issueJson,
        List<JiraCommentItem> comments,
        string requestedKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(issueJson);
            var root = doc.RootElement;
            var fields = root.TryGetProperty("fields", out var f) ? f : (JsonElement?)null;
            if (fields is null) return null;

            // 欄位名稱對照表（JIRA expand=names 提供）
            var names = root.TryGetProperty("names", out var n) ? n : (JsonElement?)null;

            var preview = new JiraIssuePreview
            {
                Key = root.TryGetProperty("key", out var k) ? (k.GetString() ?? requestedKey) : requestedKey,
                Summary = GetString(fields.Value, "summary"),
                Status = GetNested(fields.Value, "status", "name"),
                IssueType = GetNested(fields.Value, "issuetype", "name"),
                Priority = GetNestedOrNull(fields.Value, "priority", "name"),
                Assignee = GetNestedOrNull(fields.Value, "assignee", "displayName"),
                Updated = GetStringOrNull(fields.Value, "updated"),
                ProjectKey = GetNested(fields.Value, "project", "key"),
                ProjectName = GetNested(fields.Value, "project", "name"),
            };

            // 主要描述
            var descriptionWiki = GetStringOrNull(fields.Value, "description");
            var descriptionMd = JiraMarkdownConverter.Convert(descriptionWiki);

            // 解析關聯議題
            var linkedIssues = ParseLinkedIssues(fields.Value);

            // 解析附件（只記錄 metadata）
            var attachments = ParseAttachments(fields.Value);

            // 自訂欄位（白名單）
            var classified = new Dictionary<string, string>();
            foreach (var (fieldId, displayName) in KnownCustomFields)
            {
                if (!fields.Value.TryGetProperty(fieldId, out var fv)) continue;
                if (fv.ValueKind == JsonValueKind.Null) continue;
                var displayValue = ExtractFieldValue(fv);
                if (!string.IsNullOrWhiteSpace(displayValue))
                    classified[displayName] = JiraMarkdownConverter.Convert(displayValue);
            }

            return new NormalizedJiraIssue
            {
                Preview = preview,
                Resolution = GetNestedOrNull(fields.Value, "resolution", "name"),
                Components = ParseStringArray(fields.Value, "components", "name"),
                Versions = ParseStringArray(fields.Value, "versions", "name"),
                Reporter = GetNestedOrNull(fields.Value, "reporter", "displayName"),
                DescriptionMarkdown = string.IsNullOrWhiteSpace(descriptionMd) ? null : descriptionMd,
                ClassifiedFields = classified,
                LinkedIssues = linkedIssues,
                Attachments = attachments,
                Comments = comments,
            };
        }
        catch
        {
            return null;
        }
    }

    // ── 輔助方法 ─────────────────────────────────────────────────────────────

    private static string GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? (v.GetString() ?? "") : "";

    private static string? GetStringOrNull(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.GetString() : null;

    private static string GetNested(JsonElement el, string key, string nested)
    {
        if (!el.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return "";
        return v.TryGetProperty(nested, out var n) ? (n.GetString() ?? "") : "";
    }

    private static string? GetNestedOrNull(JsonElement el, string key, string nested)
    {
        if (!el.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.TryGetProperty(nested, out var n) ? n.GetString() : null;
    }

    private static IReadOnlyList<string> ParseStringArray(
        JsonElement fields, string arrayKey, string valueKey)
    {
        if (!fields.TryGetProperty(arrayKey, out var arr) ||
            arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray()
            .Select(item => item.TryGetProperty(valueKey, out var v) ? (v.GetString() ?? "") : "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static IReadOnlyList<JiraLinkedIssue> ParseLinkedIssues(JsonElement fields)
    {
        if (!fields.TryGetProperty("issuelinks", out var links) ||
            links.ValueKind != JsonValueKind.Array) return [];
        var result = new List<JiraLinkedIssue>();
        foreach (var link in links.EnumerateArray())
        {
            var linkTypeName = "";
            if (link.TryGetProperty("type", out var lt))
                linkTypeName = lt.TryGetProperty("name", out var ltn) ? (ltn.GetString() ?? "") : "";

            JsonElement issue;
            string direction;
            if (link.TryGetProperty("inwardIssue", out issue))
                direction = "inward";
            else if (link.TryGetProperty("outwardIssue", out issue))
                direction = "outward";
            else continue;

            var issueKey = issue.TryGetProperty("key", out var ik) ? (ik.GetString() ?? "") : "";
            var summary = issue.TryGetProperty("fields", out var fi)
                && fi.TryGetProperty("summary", out var s) ? (s.GetString() ?? "") : "";
            result.Add(new JiraLinkedIssue
            {
                Key = issueKey,
                Summary = summary,
                LinkType = $"{linkTypeName} ({direction})",
            });
        }
        return result;
    }

    private static IReadOnlyList<JiraAttachmentInfo> ParseAttachments(JsonElement fields)
    {
        if (!fields.TryGetProperty("attachment", out var atts) ||
            atts.ValueKind != JsonValueKind.Array) return [];
        return atts.EnumerateArray()
            .Select(a => new JiraAttachmentInfo
            {
                Filename = a.TryGetProperty("filename", out var fn) ? (fn.GetString() ?? "") : "",
                MimeType = a.TryGetProperty("mimeType", out var mt) ? (mt.GetString() ?? "") : "",
                Size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
            })
            .Where(a => !string.IsNullOrWhiteSpace(a.Filename))
            .ToList();
    }

    private static string ExtractFieldValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "是",
        JsonValueKind.False => "否",
        JsonValueKind.Object =>
            el.TryGetProperty("value", out var v) ? (v.GetString() ?? "") :
            el.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "",
        JsonValueKind.Array => string.Join(", ", el.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? "") :
                x.TryGetProperty("value", out var v) ? (v.GetString() ?? "") :
                x.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "")
            .Where(s => !string.IsNullOrWhiteSpace(s))),
        _ => "",
    };
}
