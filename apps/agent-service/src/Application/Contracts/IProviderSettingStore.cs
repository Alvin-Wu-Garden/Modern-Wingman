using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// 使用者在設定頁填入的 provider 設定（API Key、BaseUrl、SortOrder）的持久化介面。
/// 資料存入 wingman.db 的 ProviderSettings 資料表。
///
/// 優先順序（API Key）：環境變數 → DB 儲存值 → null
/// </summary>
public interface IProviderSettingStore
{
    /// <summary>取得所有 provider 設定，依 SortOrder 排序。</summary>
    Task<IReadOnlyList<ProviderSettingEntity>> GetAllAsync(CancellationToken ct = default);

    /// <summary>取得單一 provider 設定；不存在時回傳 null。</summary>
    Task<ProviderSettingEntity?> GetAsync(string profileId, CancellationToken ct = default);

    /// <summary>
    /// 取得 API Key（環境變數優先）。
    /// 回傳 null 表示尚未設定。
    /// </summary>
    string? GetApiKey(string profileId);

    /// <summary>傳回是否存在環境變數（true = 前端顯示 disabled masked input）。</summary>
    bool HasEnvVar(string profileId);

    /// <summary>
    /// 原子儲存已經由真實供應商驗證成功的 API Key 與其 Base URL。
    /// 驗證流程不得以此方法暫存候選 Key。
    /// </summary>
    Task SetValidatedCredentialAsync(
        string profileId,
        string apiKey,
        string? baseUrl,
        CancellationToken ct = default);

    /// <summary>移除 DB 中儲存的 API Key。</summary>
    Task RemoveApiKeyAsync(string profileId, CancellationToken ct = default);

    /// <summary>批次更新排序（傳入 profileId → sortOrder 的對應）。</summary>
    Task ReorderAsync(IReadOnlyList<(string ProfileId, int SortOrder)> order, CancellationToken ct = default);

    /// <summary>
    /// 確保所有 appsettings.json 中的 profile 在 DB 都有一筆設定記錄（首次啟動初始化）。
    /// 已存在的記錄不覆蓋。
    /// </summary>
    Task EnsureSeedAsync(IReadOnlyList<string> profileIds, CancellationToken ct = default);
}
