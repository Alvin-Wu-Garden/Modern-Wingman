using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Providers;

/// <summary>
/// 從 IOptions&lt;AgentServiceOptions&gt; 讀取 BYOK 設定並實作 IModelProviderService。
///
/// BaseUrl 優先順序：DB (ProviderSettingStore) → appsettings.json
/// SortOrder 由 DB 管理；appsettings 順序僅用於首次種子資料。
/// </summary>
public sealed class ModelProviderService : IModelProviderService
{
    private readonly IOptionsMonitor<AgentServiceOptions> _optionsMonitor;
    private readonly IProviderSettingStore _settingStore;

    public ModelProviderService(
        IOptionsMonitor<AgentServiceOptions> optionsMonitor,
        IProviderSettingStore settingStore)
    {
        _optionsMonitor = optionsMonitor;
        _settingStore = settingStore;
    }

    public string ActiveProfileId => _optionsMonitor.CurrentValue.ActiveProfileId;

    public IReadOnlyList<ModelProviderProfile> ListProfiles()
    {
        // 同步讀取 DB 設定（用於非 async 呼叫路徑）
        var dbSettings = _settingStore.GetAllAsync().GetAwaiter().GetResult();
        var dbMap = dbSettings.ToDictionary(x => x.ProfileId);

        return _optionsMonitor.CurrentValue.ModelProviders
            .Select(c => MapToProfile(c, dbMap.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public async ValueTask<ModelProviderProfile> GetProfileAsync(
        string? profileId = null,
        CancellationToken ct = default)
    {
        var options = _optionsMonitor.CurrentValue;
        var targetId = string.IsNullOrWhiteSpace(profileId)
            ? options.ActiveProfileId
            : profileId;

        var config = options.ModelProviders.FirstOrDefault(p => p.Id == targetId)
            ?? options.ModelProviders.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"找不到 provider profile「{targetId}」，且 ModelProviders 清單為空。");

        var dbSetting = await _settingStore.GetAsync(config.Id, ct);
        return MapToProfile(config, dbSetting);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static ModelProviderProfile MapToProfile(
        ModelProviderProfileConfig c,
        Domain.Models.ProviderSettingEntity? dbSetting) =>
        new()
        {
            Id = c.Id,
            DisplayName = c.DisplayName,
            Kind = c.Kind,
            ModelId = c.ModelId,
            ProviderType = c.ProviderType,
            // DB BaseUrl 優先（只有 custom-byok 才會有 DB 值）
            BaseUrl = dbSetting?.BaseUrl ?? c.BaseUrl,
            ApiKeyEnvVar = c.ApiKeyEnvVar,
            AzureApiVersion = c.AzureApiVersion,
            WireApi = c.WireApi,
        };
}
