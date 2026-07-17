using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// 供應商設定檔服務介面。
/// 負責讀取、列舉 BYOK 設定，並解析 active profile。
/// </summary>
public interface IModelProviderService
{
    /// <summary>
    /// 取得指定的 profile；若 profileId 為 null，則回傳目前的 active default profile。
    /// </summary>
    ValueTask<ModelProviderProfile> GetProfileAsync(
        string? profileId = null,
        CancellationToken ct = default);

    /// <summary>列出所有已設定的 provider profiles。</summary>
    IReadOnlyList<ModelProviderProfile> ListProfiles();

    /// <summary>目前 active profile 的 ID。</summary>
    string ActiveProfileId { get; }
}
