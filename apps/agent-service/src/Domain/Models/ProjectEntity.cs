namespace AgentService.Domain.Models;

public enum ProjectIndexStatus
{
    NotIndexed,
    Indexing,
    Indexed,
    Partial,
    Stale,
    Failed,
    Canceled,
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

    /// <summary>專案圖譜解析涵蓋的語言與檔案類型，以逗號分隔。</summary>
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

    /// <summary>根目錄包含多個方案時，由使用者明確選擇的方案絕對路徑。</summary>
    public string? SelectedSolutionPath { get; set; }

    /// <summary>是否已執行 ParallelExtractor v2 的舊格式清理。</summary>
    public bool GraphStorageMigrated { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
