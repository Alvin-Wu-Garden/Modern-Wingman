namespace AgentService.Domain.Models;

/// <summary>
/// 使用者在設定頁填入的 provider 設定（存入 wingman.db）。
///
/// 設計原則：
///  - ProfileId 對應 appsettings.json 中的 Id，不是外鍵，避免與靜態設定強耦合
///  - SortOrder 預設從 appsettings 順序讀入後存入 DB，之後由使用者拖拉排序覆蓋
///  - BaseUrl 可覆蓋 OpenAI-compatible / Azure / custom endpoint 的預設值
///  - ApiKey 使用者手動輸入的 Key（明文；桌面本機 DB，無多人共用風險）
///  - UpdatedAt 用於前端 cache busting
/// </summary>
public sealed class ProviderSettingEntity
{
    /// <summary>Primary key = ProfileId（例如 "openai-byok", "custom-byok"）。</summary>
    public required string ProfileId { get; set; }

    /// <summary>
    /// 使用者自訂的 BaseUrl。
    /// null = 沿用 appsettings.json 的預設值。
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// 使用者手動輸入的 API Key。
    /// 優先順序：環境變數 > 此欄位 > null（顯示警告）。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// 排序位置（越小越前），用於新對話下拉選單的供應商順序。
    /// 預設依 appsettings.json 順序初始化。
    /// </summary>
    public int SortOrder { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
