using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Providers;

public sealed class ProviderCredentialService(
    IEnumerable<IProviderApiKeyValidator> validators,
    IProviderSettingStore settingStore,
    ICopilotCredentialRuntime copilotRuntime,
    ILogger<ProviderCredentialService> logger) : IProviderCredentialService
{
    public async Task<ApiKeyValidationResult> ValidateAndSaveAsync(
        ModelProviderProfile profile,
        string apiKey,
        string? baseUrl,
        CancellationToken ct = default)
    {
        var validator = validators.FirstOrDefault(candidate => candidate.CanValidate(profile));
        if (validator is null)
            return ApiKeyValidationResult.Invalid("不支援此 AI 供應商的 API Key 驗證。");

        ApiKeyValidationResult validation;
        try
        {
            validation = await validator.ValidateAsync(profile, apiKey, baseUrl, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Provider credential validation failed unexpectedly: profile={ProfileId}, errorType={ErrorType}",
                profile.Id,
                ex.GetType().Name);
            return ApiKeyValidationResult.Invalid("驗證 API Key 時發生錯誤。");
        }

        if (!validation.IsValid)
            return validation;

        var previous = await settingStore.GetAsync(profile.Id, ct);
        await settingStore.SetValidatedCredentialAsync(profile.Id, apiKey, baseUrl, ct);

        if (profile.Kind != ProviderKind.CopilotDefault)
            return ApiKeyValidationResult.Valid();

        try
        {
            await copilotRuntime.RestartWithTokenAsync(apiKey, ct);
            return ApiKeyValidationResult.Valid();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Validated Copilot PAT could not activate bundled runtime: errorType={ErrorType}",
                ex.GetType().Name);

            if (string.IsNullOrWhiteSpace(previous?.ApiKey))
                await settingStore.RemoveApiKeyAsync(profile.Id, CancellationToken.None);
            else
                await settingStore.SetValidatedCredentialAsync(
                    profile.Id,
                    previous.ApiKey,
                    previous.BaseUrl,
                    CancellationToken.None);

            try
            {
                await copilotRuntime.RestartWithTokenAsync(previous?.ApiKey, CancellationToken.None);
            }
            catch
            {
                // Runtime diagnostics are exposed separately; never persist the rejected candidate PAT.
            }

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            return ApiKeyValidationResult.Invalid(
                "PAT 已通過驗證，但 bundled Copilot runtime 啟用失敗，原設定未變更。");
        }
    }
}
