using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IAtlassianConnectionRepository
{
    Task<AtlassianConnection?> GetAsync(AtlassianServiceType serviceType, CancellationToken ct = default);

    /// <summary>
    /// 新增或更新連線設定。
    /// 若 <paramref name="connection"/>.SecretValue 為 null，保留 DB 中既有的 DPAPI 加密 PAT，
    /// 不清除或覆寫。
    /// </summary>
    Task SaveAsync(AtlassianConnection connection, CancellationToken ct = default);

    Task DeleteAsync(AtlassianServiceType serviceType, CancellationToken ct = default);
}
