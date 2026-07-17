namespace AgentService.Domain.Models;

/// <summary>
/// 單次 LLM 呼叫耗用的 token 統計。
/// </summary>
public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens);
