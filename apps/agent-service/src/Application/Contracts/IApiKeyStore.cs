namespace AgentService.Application.Contracts;

/// <summary>
/// 已驗證 API Key 的唯讀存取介面。
///
/// API Key 只來自設定頁驗證後保存的本機資料庫值。
/// </summary>
public interface IApiKeyStore
{
    /// <summary>取得指定 Provider Profile 已保存的 API Key。</summary>
    string? Get(string profileId);
}
