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

    // CopilotByok 欄位
    public string? ProviderType { get; set; }
    public string? BaseUrl { get; set; }

    /// <summary>
    /// 持有 API Key 的環境變數名稱。
    /// 例如 "OPENAI_API_KEY"、"ANTHROPIC_API_KEY"、"AZURE_OPENAI_KEY"。
    /// API Key 永不明文儲存於 config 檔案中。
    /// </summary>
    public string? ApiKeyEnvVar { get; set; }

    public string? AzureApiVersion { get; set; }

    /// <summary>"completions" | "responses"。null = Copilot CLI 預設。</summary>
    public string? WireApi { get; set; }
}
