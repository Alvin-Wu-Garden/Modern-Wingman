using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using AgentService.Infrastructure.Orchestration;

namespace AgentService.Infrastructure.Providers;

/// <summary>
/// 將 ModelProviderProfile（BYOK 設定）轉換為 copilot-sdk 的 SessionConfig。
/// BaseUrl 優先順序：DB 使用者設定 > appsettings.json 預設值。
/// </summary>
public sealed class ProviderConfigResolver
{
    private readonly IApiKeyStore _apiKeyStore;
    private readonly IProviderSettingStore _settingStore;
    private readonly ILogger<ProviderConfigResolver> _logger;
    private readonly CopilotPermissionHandlerFactory _permissionHandlerFactory;

    public ProviderConfigResolver(
        IApiKeyStore apiKeyStore,
        IProviderSettingStore settingStore,
        CopilotPermissionHandlerFactory permissionHandlerFactory,
        ILogger<ProviderConfigResolver> logger)
    {
        _apiKeyStore = apiKeyStore;
        _settingStore = settingStore;
        _permissionHandlerFactory = permissionHandlerFactory;
        _logger = logger;
    }

    /// <summary>
    /// 根據 profile 建構一次性模型呼叫所需的 SessionConfig。
    /// Modern Wingman 不把工作區路徑交給模型，專案內容一律由 GraphRAG 提供。
    /// </summary>
    public async Task<SessionConfig> BuildSessionConfigAsync(
        ModelProviderProfile profile,
        string? modelOverride = null,
        CancellationToken ct = default)
    {
        var config = new SessionConfig
        {
            Streaming = true,
            OnPermissionRequest = _permissionHandlerFactory.Create(),
        };

        var modelId = string.IsNullOrWhiteSpace(modelOverride) ? profile.ModelId : modelOverride;
        if (modelId is not null)
            config.Model = modelId;

        if (profile.Kind == ProviderKind.CopilotByok)
            config.Provider = await BuildProviderConfigAsync(profile, ct);

        return config;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ProviderConfig> BuildProviderConfigAsync(
        ModelProviderProfile profile,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ProviderType))
            throw new InvalidOperationException(
                $"Profile [{profile.Id}] 為 CopilotByok 但未設定 ProviderType。");

        // BaseUrl 優先順序：DB 使用者設定 > appsettings.json 預設值
        var dbSetting = await _settingStore.GetAsync(profile.Id, ct);
        var baseUrl = dbSetting?.BaseUrl ?? profile.BaseUrl;

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                $"Profile [{profile.Id}] 為 CopilotByok 但未設定 BaseUrl（appsettings 及 DB 皆無值）。");

        var apiKey = _apiKeyStore.Get(profile.Id);

        var providerConfig = new ProviderConfig
        {
            Type = profile.ProviderType,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
        };

        if (profile.ProviderType == "azure" && profile.AzureApiVersion is not null)
        {
            providerConfig.Azure = new AzureOptions
            {
                ApiVersion = profile.AzureApiVersion,
            };
        }

        if (profile.WireApi is not null)
            providerConfig.WireApi = profile.WireApi;

        _logger.LogDebug(
            "BYOK Provider 設定已建立。ProfileId={ProfileId}, ProviderType={ProviderType}, ModelId={ModelId}, BaseUrl={BaseUrl}",
            profile.Id, profile.ProviderType, profile.ModelId, baseUrl);

        return providerConfig;
    }
}
