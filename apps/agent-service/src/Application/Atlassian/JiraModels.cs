namespace AgentService.Application.Atlassian;

/// <summary>
/// 所有 Atlassian / JIRA 操作的統一回傳結果容器。
/// ErrorCode 為 null 表示成功。
/// </summary>
public sealed record AtlassianResult<T>(
    T? Value,
    string? ErrorCode = null,
    string? ErrorDetail = null)
{
    public bool IsSuccess => ErrorCode is null;

    public static AtlassianResult<T> Ok(T value) => new(value);
    public static AtlassianResult<T> Fail(string errorCode, string? detail = null) =>
        new(default, errorCode, detail);
}

/// <summary>JIRA 議題基本資料預覽（呼叫 preview API 後顯示給使用者確認）。</summary>
public sealed record JiraIssuePreview
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string Status { get; init; }
    public required string IssueType { get; init; }
    public string? Priority { get; init; }
    public string? Assignee { get; init; }
    public string? Updated { get; init; }
    public required string ProjectKey { get; init; }
    public required string ProjectName { get; init; }
}

/// <summary>JIRA 留言（已含作者顯示名稱、建立時間）。</summary>
public sealed record JiraCommentItem
{
    public required string Id { get; init; }
    public required string AuthorDisplayName { get; init; }
    public required string Body { get; init; }
    public required string Created { get; init; }
    public string? Updated { get; init; }
}

/// <summary>關聯議題摘要。</summary>
public sealed record JiraLinkedIssue
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string LinkType { get; init; }
}

/// <summary>附件資訊（只記錄名稱與 MIME；第一版不下載附件內容）。</summary>
public sealed record JiraAttachmentInfo
{
    public required string Filename { get; init; }
    public required string MimeType { get; init; }
    public required long Size { get; init; }
}

/// <summary>
/// 完整正規化後的 JIRA 議題，供 Markdown 轉換與 LLM Prompt 組裝使用。
/// </summary>
public sealed record NormalizedJiraIssue
{
    public required JiraIssuePreview Preview { get; init; }
    public string? Resolution { get; init; }
    public IReadOnlyList<string> Components { get; init; } = [];
    public IReadOnlyList<string> Versions { get; init; } = [];
    public string? Reporter { get; init; }
    public string? DescriptionMarkdown { get; init; }

    /// <summary>白名單自訂欄位，以「欄位顯示名稱 → Markdown 文字」格式儲存。</summary>
    public IReadOnlyDictionary<string, string> ClassifiedFields { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<JiraLinkedIssue> LinkedIssues { get; init; } = [];
    public IReadOnlyList<JiraAttachmentInfo> Attachments { get; init; } = [];
    public IReadOnlyList<JiraCommentItem> Comments { get; init; } = [];
}
