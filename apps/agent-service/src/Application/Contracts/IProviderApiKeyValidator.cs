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
/// Minimal Copilot credential surface used by validation and persistence orchestration.
/// </summary>
public interface ICopilotCredentialRuntime
{
    Task<ApiKeyValidationResult> ValidateAsync(string githubToken, CancellationToken ct = default);

    Task RestartWithTokenAsync(string? githubToken, CancellationToken ct = default);
}
