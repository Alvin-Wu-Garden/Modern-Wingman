namespace AgentService.Application.Contracts;

/// <summary>
/// 移除程序輸出中的 token、密碼與網址憑證，避免匯入進度把敏感資料送到前端。
/// </summary>
public interface ISensitiveDataRedactor
{
    string Redact(string value);
}
