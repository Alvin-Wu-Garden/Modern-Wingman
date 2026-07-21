namespace AgentService.Application.Models;

/// <summary>
/// Result of validating a provider API key against the real provider endpoint.
/// Only valid credentials are eligible for persistence.
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
