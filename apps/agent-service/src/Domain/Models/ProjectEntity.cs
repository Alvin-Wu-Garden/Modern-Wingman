namespace AgentService.Domain.Models;

public enum ProjectIndexStatus
{
    NotIndexed,
    PendingChanges,
    Indexing,
    Indexed,
    Partial,
    Stale,
    Failed,
}

/// <summary>
/// 一個被 Wingman 管理的企業程式碼專案（WS3.1，像 Codex 左側選單的專案）。
/// </summary>
public sealed class ProjectEntity
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string Name { get; set; }

    /// <summary>專案根目錄絕對路徑。</summary>
    public required string RootPath { get; set; }

    /// <summary>偵測到的語言（"csharp" / "java"，逗號分隔）。</summary>
    public string Languages { get; set; } = "";

    public ProjectIndexStatus IndexStatus { get; set; } = ProjectIndexStatus.NotIndexed;

    /// <summary>最後一次索引完成時間。</summary>
    public DateTimeOffset? IndexedAt { get; set; }

    /// <summary>最後一次索引的錯誤訊息。</summary>
    public string? IndexError { get; set; }

    /// <summary>索引統計：節點數。</summary>
    public int NodeCount { get; set; }

    /// <summary>索引統計：關係數。</summary>
    public int EdgeCount { get; set; }

    /// <summary>目前可供查詢的已成功圖譜版本。</summary>
    public string? IndexManifestVersion { get; set; }

    /// <summary>等待 debounce／catch-up 的檔案數。</summary>
    public int PendingFileCount { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
