namespace AgentService.Infrastructure.Providers;

/// <summary>要求的 Provider Profile 不存在；不得靜默改用第一個設定。</summary>
public sealed class ProviderProfileNotFoundException(string profileId) : Exception
{
    /// <summary>找不到的 profile ID。</summary>
    public string ProfileId { get; } = profileId;
}
