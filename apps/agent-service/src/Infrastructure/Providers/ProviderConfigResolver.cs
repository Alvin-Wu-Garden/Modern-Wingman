using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using AgentService.Infrastructure.Orchestration;
using WingmanAgentMode = AgentService.Domain.Models.AgentMode;

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
    /// 根據 profile 建構 SessionConfig，包含 BYOK ProviderConfig、
    /// 工作區 system prompt 注入、對話歷史注入以及 streaming 設定。
    /// </summary>
    public async Task<SessionConfig> BuildSessionConfigAsync(
        ModelProviderProfile profile,
        string? workspacePath,
        string? conversationHistoryText = null,
        string? modelOverride = null,
        WingmanAgentMode mode = WingmanAgentMode.Ask,
        string? runId = null,
        CancellationToken ct = default)
    {
        var config = new SessionConfig
        {
            Streaming = true,
            OnPermissionRequest = _permissionHandlerFactory.Create(mode, workspacePath, runId),
        };

        var modelId = string.IsNullOrWhiteSpace(modelOverride) ? profile.ModelId : modelOverride;
        if (modelId is not null)
            config.Model = modelId;

        var extraContent = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            extraContent.AppendLine($"""
                <workspace>
                  <path>{workspacePath}</path>
                </workspace>
                """);
        }

        if (!string.IsNullOrWhiteSpace(conversationHistoryText))
            extraContent.Append(conversationHistoryText);

        if (extraContent.Length > 0)
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = "\n" + extraContent.ToString(),
            };
        }

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
            "BYOK provider config built: profile={ProfileId}, type={ProviderType}, model={ModelId}, baseUrl={BaseUrl}",
            profile.Id, profile.ProviderType, profile.ModelId, baseUrl);

        return providerConfig;
    }
}
