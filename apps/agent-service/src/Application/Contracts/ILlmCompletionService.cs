using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// 簡單的一次性 LLM completion（非對話、非串流）。
/// 用途：GraphRAG 社群摘要、AGENTS.md 生成、查詢 map-reduce。
///
/// 抽象目的：目前實作走 Copilot SDK（企業唯一可用路徑）；
/// 未來可切換 BYOK/本地模型而不影響呼叫端（DIP）。
/// </summary>
public interface ILlmCompletionService
{
    /// <summary>執行一次 completion，回傳完整文字。</summary>
    Task<string> CompleteAsync(string prompt, CancellationToken ct = default);

    /// <summary>執行一次 completion，並附加企業觀測追蹤脈絡。</summary>
    Task<string> CompleteAsync(
        string prompt,
        LlmTelemetryContext? telemetryContext,
        CancellationToken ct = default);

    /// <summary>使用指定 provider / model 執行一次 completion，回傳完整文字。</summary>
    Task<string> CompleteAsync(
        string prompt,
        string? providerProfileId,
        string? modelId,
        CancellationToken ct = default);

    /// <summary>使用指定 provider / model 執行一次 completion，並附加企業觀測追蹤脈絡。</summary>
    Task<string> CompleteAsync(
        string prompt,
        string? providerProfileId,
        string? modelId,
        LlmTelemetryContext? telemetryContext,
        CancellationToken ct = default);
}
