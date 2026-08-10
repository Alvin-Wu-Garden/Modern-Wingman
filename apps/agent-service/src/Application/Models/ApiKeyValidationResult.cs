namespace AgentService.Application.Models;

/// <summary>
/// 向實際 Provider Endpoint 驗證 API Key 的結果。
/// 只有驗證成功的憑證可以保存至本機資料庫。
/// </summary>
public sealed record ApiKeyValidationResult(string Status, string? Message = null)
{
    public const string ValidStatus = "valid";
    public const string InvalidStatus = "invalid";

    public bool IsValid => string.Equals(Status, ValidStatus, StringComparison.Ordinal);

    public static ApiKeyValidationResult Valid() => new(ValidStatus);

    public static ApiKeyValidationResult Invalid(string? message = null) =>
        new(InvalidStatus, string.IsNullOrWhiteSpace(message) ? "API Key 驗證失敗。" : message);
}
