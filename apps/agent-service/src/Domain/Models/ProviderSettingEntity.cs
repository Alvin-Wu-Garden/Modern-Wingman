namespace AgentService.Domain.Models;

/// <summary>
/// 使用者在設定頁填入的 provider 設定（存入 wingman.db）。
///
/// 設計原則：
///  - ProfileId 對應 appsettings.json 中的 Id，不是外鍵，避免與靜態設定強耦合
///  - SortOrder 預設從 appsettings 順序讀入後存入 DB，之後由使用者拖拉排序覆蓋
///  - BaseUrl 可覆蓋 OpenAI-compatible / Azure / custom endpoint 的預設值
///  - Provider Key 使用 Windows DPAPI CurrentUser 密文保存，不以明文落地
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
    /// 使用者手動輸入 API Key 的 DPAPI Base64 密文。
    /// null 代表尚未設定；任何 API response 都不得回傳此欄位。
    /// </summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>密文格式版本，目前固定為 dpapi-current-user-v1。</summary>
    public string? EncryptionScheme { get; set; }

    /// <summary>
    /// 排序位置（越小越前），用於新對話下拉選單的供應商順序。
    /// 預設依 appsettings.json 順序初始化。
    /// </summary>
    public int SortOrder { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
