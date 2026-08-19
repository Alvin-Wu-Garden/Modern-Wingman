using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Providers;

/// <summary>
/// 從 IOptions&lt;AgentServiceOptions&gt; 讀取 BYOK 設定並實作 IModelProviderService。
///
/// BaseUrl 優先順序：DB (ProviderSettingStore) → appsettings.json。
/// SortOrder 由 DB 管理；appsettings 順序僅用於首次種子資料。
///
/// 列舉 profile 時只讀取目前設定快照，避免在 ASP.NET request thread 上以同步
/// 方式等待 SQLite。需要套用使用者自訂 BaseUrl 的執行路徑，改用 GetProfileAsync。
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
        // ProviderEndpoints 會在同一個非同步請求中一次讀取 DB 狀態；此方法
        // 只負責提供 appsettings 的 profile 定義，避免 async-over-sync 死結與阻塞。
        return _optionsMonitor.CurrentValue.ModelProviders
            .Select(c => MapToProfile(c, null))
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
            ?? throw new ProviderProfileNotFoundException(targetId);

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
            Protocol = c.Protocol,
            ProviderType = c.ProviderType,
            // DB BaseUrl 優先（只有 custom-byok 才會有 DB 值）
            BaseUrl = dbSetting?.BaseUrl ?? c.BaseUrl,
            AzureApiVersion = c.AzureApiVersion,
            WireApi = c.WireApi,
            WireApiMode = c.WireApiMode,
        };
}
