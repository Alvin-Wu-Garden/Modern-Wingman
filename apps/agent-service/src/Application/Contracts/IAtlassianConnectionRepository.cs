using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IAtlassianConnectionRepository
{
    Task<AtlassianConnection?> GetAsync(AtlassianServiceType serviceType, CancellationToken ct = default);

    /// <summary>
    /// 僅供伺服器內部即將發送 Atlassian 請求時使用的解密讀取。
    /// 一般設定與 REST 回應必須使用 <see cref="GetAsync"/>，不得取得明文密鑰。
    /// </summary>
    Task<AtlassianConnection?> GetForUseAsync(AtlassianServiceType serviceType, CancellationToken ct = default);

    /// <summary>
    /// 新增或更新連線設定。
    /// 若 <paramref name="connection"/>.SecretValue 為 null，保留 DB 中既有的 DPAPI 加密 PAT，
    /// 不清除或覆寫。
    /// </summary>
    Task SaveAsync(AtlassianConnection connection, CancellationToken ct = default);

    Task DeleteAsync(AtlassianServiceType serviceType, CancellationToken ct = default);
}
