namespace AgentService.Domain.Models;

/// <summary>
/// 模型供應商設定檔（BYOK 核心資料模型）。
/// 可擴充：每個新供應商或新端點只需新增一個 profile，不改動既有邏輯。
/// </summary>
public sealed class ModelProviderProfile
{
    /// <summary>唯一識別碼，例如 "copilot-default"、"openai-gpt4"、"azure-gpt4"。</summary>
    public required string Id { get; init; }

    /// <summary>UI 顯示名稱。</summary>
    public required string DisplayName { get; init; }

    /// <summary>供應商種類，決定 Infrastructure 層如何建構 SessionConfig。</summary>
    public ProviderKind Kind { get; init; } = ProviderKind.CopilotDefault;

    /// <summary>
    /// 要求 Copilot CLI 使用的模型 ID，例如 "gpt-5"、"claude-sonnet-4-5"。
    /// null = 由 CLI 使用預設模型。
    /// </summary>
    public string? ModelId { get; init; }

    // ── CopilotByok 專用欄位 ─────────────────────────────────────────────────
    // 只有 Kind == CopilotByok 時以下欄位才有意義。

    /// <summary>
    /// Copilot SDK ProviderConfig.Type：
    /// "openai" | "azure" | "anthropic"
    /// （OpenAI-compatible endpoint 一律填 "openai"）
    /// </summary>
    public string? ProviderType { get; init; }

    /// <summary>
    /// API endpoint base URL。
    /// - OpenAI:       "https://api.openai.com/v1"
    /// - Anthropic:    "https://api.anthropic.com"
    /// - Azure native: "https://&lt;resource&gt;.openai.azure.com"（不含 /openai/v1）
    /// - Azure Foundry:"https://&lt;resource&gt;.openai.azure.com/openai/v1/"
    /// - Ollama:       "http://localhost:11434/v1"
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// 持有 API Key 的環境變數名稱，例如 "OPENAI_API_KEY"。
    /// Key 永遠不直接寫入 config 檔，由執行環境注入（OS 環境變數 / Tauri keychain bridge）。
    /// </summary>
    public string? ApiKeyEnvVar { get; init; }

    /// <summary>
    /// Azure OpenAI API 版本，例如 "2024-10-21"。
    /// 只有 ProviderType == "azure" 時才需設定。
    /// </summary>
    public string? AzureApiVersion { get; init; }

    /// <summary>
    /// Copilot SDK wireApi 選項："completions" | "responses"。
    /// GPT-5 系列建議 "responses"；舊模型使用 "completions"。
    /// null = 由 CLI 決定預設值。
    /// </summary>
    public string? WireApi { get; init; }
}
