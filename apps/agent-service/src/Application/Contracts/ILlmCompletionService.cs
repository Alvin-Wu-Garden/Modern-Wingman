namespace AgentService.Application.Contracts;

/// <summary>
/// 提供 GraphRAG 摘要與對話標題所需的一次性文字生成。
/// 此介面只保留實際使用的兩種呼叫方式，避免把已移除的遙測模型洩漏到業務層。
/// </summary>
public interface ILlmCompletionService
{
    /// <summary>使用預設 Provider 與模型產生完整文字。</summary>
    Task<string> CompleteAsync(string prompt, CancellationToken ct = default);

    /// <summary>使用使用者指定的 Provider 與模型產生完整文字。</summary>
    Task<string> CompleteAsync(
        string prompt,
        string? providerProfileId,
        string? modelId,
        CancellationToken ct = default);
}
