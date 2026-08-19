using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Providers;

/// <summary>
/// IOptions 綁定用的組態 POCO。
/// 對應 appsettings.json 的 "AgentService" section。
/// </summary>
public sealed class AgentServiceOptions
{
    public const string SectionName = "AgentService";

    /// <summary>預設使用的 provider profile ID。</summary>
    public string ActiveProfileId { get; set; } = "copilot-default";

    /// <summary>所有已設定的 provider profiles。</summary>
    public List<ModelProviderProfileConfig> ModelProviders { get; set; } = [];
}

/// <summary>
/// appsettings.json 中單一 provider profile 的 JSON 結構。
/// 可擴充：新增供應商只需在 appsettings 加一個 entry，不改動 C# 程式。
/// </summary>
public sealed class ModelProviderProfileConfig
{
    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public ProviderKind Kind { get; set; } = ProviderKind.CopilotDefault;
    public string? ModelId { get; set; }

    // BYOK 欄位；Protocol 是執行時唯一協定來源。
    public ProviderProtocol Protocol { get; set; } = ProviderProtocol.OpenAI;
    public string? ProviderType { get; set; }
    public string? BaseUrl { get; set; }

    public string? AzureApiVersion { get; set; }

    /// <summary>舊版設定的 "completions" | "responses" 字串。</summary>
    public string? WireApi { get; set; }

    /// <summary>新設定使用的強型別 API 形態。</summary>
    public ModelWireApi WireApiMode { get; set; } = ModelWireApi.ChatCompletions;
}
