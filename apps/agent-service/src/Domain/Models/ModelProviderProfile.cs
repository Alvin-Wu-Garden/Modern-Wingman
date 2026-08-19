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

    /// <summary>供應商執行路徑；CopilotDefault 使用 Copilot SDK，其餘使用直接 BYOK。</summary>
    public ProviderKind Kind { get; init; } = ProviderKind.CopilotDefault;

    /// <summary>
    /// 要求供應商使用的模型 ID，例如 "gpt-5"、"claude-sonnet-4-5"。
    /// null = 由供應商使用預設模型。
    /// </summary>
    public string? ModelId { get; init; }

    // ── BYOK 端點欄位 ────────────────────────────────────────────────────────
    // 只有 Kind == CopilotByok 時以下欄位才有意義。

    /// <summary>BYOK 端點的實際通訊協定。</summary>
    public ProviderProtocol Protocol { get; init; } = ProviderProtocol.OpenAI;

    /// <summary>
    /// 舊版設定留下的顯示欄位；不得用來決定 BYOK 的通訊協定。
    /// 執行時一律使用 <see cref="Protocol"/>。
    /// </summary>
    public string? ProviderType { get; init; }

    /// <summary>
    /// API endpoint 的基底 URL。
    /// - OpenAI 範例："https://api.openai.com/v1"
    /// - Anthropic 範例："https://api.anthropic.com"
    /// - Azure OpenAI: "https://&lt;resource&gt;.openai.azure.com"（不含 /openai/v1）
    /// - Ollama 範例："http://localhost:11434/v1"
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Azure OpenAI API 版本，例如 "2024-10-21"。
    /// 只有 Protocol == AzureOpenAI 時才需設定。
    /// </summary>
    public string? AzureApiVersion { get; init; }

    /// <summary>
    /// 舊版 JSON 的 wireApi 字串；新程式會轉成 <see cref="WireApiMode"/>。
    /// </summary>
    public string? WireApi { get; init; }

    /// <summary>強型別的 API 形態；未設定時預設 Chat Completions。</summary>
    public ModelWireApi WireApiMode { get; init; } = ModelWireApi.ChatCompletions;

    /// <summary>
    /// 取得實際執行協定。這是唯一的協定來源；不能再從模糊的 ProviderType
    /// 推測，避免同一個 profile 被送到錯誤的 SDK 或 HTTP endpoint。
    /// </summary>
    public ProviderProtocol EffectiveProtocol => Protocol;
}
