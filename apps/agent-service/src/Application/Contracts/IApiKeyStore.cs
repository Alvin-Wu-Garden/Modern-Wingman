namespace AgentService.Application.Contracts;

/// <summary>
/// 已驗證 API Key 的唯讀存取介面。
///
/// 優先順序：環境變數 → 使用者儲存的 Key → 空值（顯示警告）。
/// 使用者輸入的 Key 只有在後端完成真實供應商驗證後才會寫入本機 SQLite。
/// </summary>
public interface IApiKeyStore
{
    /// <summary>取得指定 Provider Profile 的 API Key。優先讀取環境變數；其次讀取使用者儲存值。</summary>
    string? Get(string profileId);

    /// <summary>傳回是否存在環境變數（True = 前端顯示 disabled masked input）。</summary>
    bool HasEnvVar(string profileId);
}
