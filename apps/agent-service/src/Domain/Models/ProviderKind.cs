namespace AgentService.Domain.Models;

/// <summary>
/// 模型供應商種類。
/// 設計原則：新增供應商只需在此 enum 加值並在 Infrastructure 層實作對應 resolver，
/// Application 與 Domain 層不受影響（開放封閉原則）。
/// </summary>
public enum ProviderKind
{
    /// <summary>
    /// GitHub Copilot 官方驗證（已登入的使用者 / GitHub token）。
    /// 不需要 API Key，走 Copilot 訂閱計費。
    /// </summary>
    CopilotDefault,

    /// <summary>
    /// 使用使用者在設定頁保存的 API Key，直接連線至指定模型供應商。
    /// 實際通訊協定由 <see cref="ProviderProtocol"/> 決定，不再透過 Copilot CLI 轉發。
    /// </summary>
    CopilotByok,
}

/// <summary>
/// BYOK 模型端點所採用的通訊協定。
/// ProviderKind 用來決定「認證/執行路徑」，ProviderProtocol 用來決定「HTTP wire protocol」；
/// 兩者分離後，自訂 OpenAI-compatible endpoint 不會被誤判成 Azure 或 Anthropic。
/// </summary>
public enum ProviderProtocol
{
    /// <summary>OpenAI 官方 API。</summary>
    OpenAI,

    /// <summary>任何相容 OpenAI Chat Completions 的自訂端點。</summary>
    OpenAICompatible,

    /// <summary>Anthropic Messages API。</summary>
    Anthropic,

    /// <summary>Azure OpenAI 原生端點。</summary>
    AzureOpenAI,
}

/// <summary>模型端點使用的 API 形態。</summary>
public enum ModelWireApi
{
    /// <summary>OpenAI Chat Completions；自訂端點固定使用此模式。</summary>
    ChatCompletions,

    /// <summary>OpenAI Responses API。</summary>
    Responses,
}
