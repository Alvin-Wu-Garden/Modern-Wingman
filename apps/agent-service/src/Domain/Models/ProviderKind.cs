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
    /// Copilot SDK BYOK 模式：透過 Copilot CLI 轉發，
    /// 但使用自訂 provider 的 API Key（OpenAI / Azure OpenAI / Anthropic / Ollama / 自訂 endpoint）。
    /// 不消耗 Copilot Premium Requests；費用直接計入各供應商帳戶。
    /// </summary>
    CopilotByok,

    // 保留擴充空間 ─ Phase 3：直接走 MAF native provider，不透過 Copilot CLI
    // MafOpenAI,
    // MafAzureOpenAI,
}
