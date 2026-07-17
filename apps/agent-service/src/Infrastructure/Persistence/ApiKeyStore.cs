using AgentService.Application.Contracts;
using AgentService.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// IApiKeyStore 實作 — 委派給 IProviderSettingStore（wingman.db）。
///
/// 保留此類別讓既有注入 IApiKeyStore 的程式碼（ProviderConfigResolver 等）
/// 無需修改；內部改為讀寫 DB 而非 JSON 檔案。
/// </summary>
public sealed class ApiKeyStore : IApiKeyStore
{
    private readonly IProviderSettingStore _settingStore;
    private readonly AgentServiceOptions _options;

    public ApiKeyStore(
        IProviderSettingStore settingStore,
        IOptions<AgentServiceOptions> options)
    {
        _settingStore = settingStore;
        _options = options.Value;
    }

    public bool HasEnvVar(string profileId) => _settingStore.HasEnvVar(profileId);

    public string? Get(string profileId) => _settingStore.GetApiKey(profileId);

    public Task SetAsync(string profileId, string apiKey, CancellationToken ct = default)
        => _settingStore.SetApiKeyAsync(profileId, apiKey, ct);

    public Task RemoveAsync(string profileId, CancellationToken ct = default)
        => _settingStore.RemoveApiKeyAsync(profileId, ct);
}
