namespace AgentService.Application.Contracts;

/// <summary>
/// 使用者手動輸入的 API Key 儲存介面。
///
/// 優先順序：環境變數 → 使用者儲存的 Key → 空值（顯示警告）。
/// Key 儲存在本機 JSON 檔案（非 appsettings，非 gRPC payload）。
/// </summary>
public interface IApiKeyStore
{
    /// <summary>取得指定 Provider Profile 的 API Key。優先讀取環境變數；其次讀取使用者儲存值。</summary>
    string? Get(string profileId);

    /// <summary>儲存使用者輸入的 API Key（覆蓋舊值）。</summary>
    Task SetAsync(string profileId, string apiKey, CancellationToken ct = default);

    /// <summary>移除使用者儲存的 API Key。</summary>
    Task RemoveAsync(string profileId, CancellationToken ct = default);

    /// <summary>傳回是否存在環境變數（True = 前端顯示 disabled masked input）。</summary>
    bool HasEnvVar(string profileId);
}
