using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IProviderApiKeyValidator
{
    bool CanValidate(ModelProviderProfile profile);

    Task<ApiKeyValidationResult> ValidateAsync(
        ModelProviderProfile profile,
        string apiKey,
        string? baseUrl,
        CancellationToken ct = default);
}

public interface IProviderCredentialService
{
    Task<ApiKeyValidationResult> ValidateAndSaveAsync(
        ModelProviderProfile profile,
        string apiKey,
        string? baseUrl,
        CancellationToken ct = default);
}

/// <summary>
/// 提供驗證與持久化協調流程所需的最小 Copilot 憑證操作介面。
/// </summary>
public interface ICopilotCredentialRuntime
{
    Task<ApiKeyValidationResult> ValidateAsync(string githubToken, CancellationToken ct = default);

    Task RestartWithTokenAsync(string? githubToken, CancellationToken ct = default);
}
