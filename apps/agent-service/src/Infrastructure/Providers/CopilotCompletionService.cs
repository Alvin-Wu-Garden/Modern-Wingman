using System.Text;
using AgentService.Application.Contracts;
using AgentService.Infrastructure.Orchestration;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.Providers;

/// <summary>
/// ILlmCompletionService 的 Copilot SDK 實作。
/// 走 CopilotClient → MAF AsAIAgent() 的一次性非串流呼叫。
/// </summary>
public sealed class CopilotCompletionService(
    CopilotClientService copilotClientService,
    IModelProviderService providerService,
    ProviderConfigResolver configResolver,
    CopilotPermissionHandlerFactory permissionHandlerFactory,
    ILogger<CopilotCompletionService> logger) : ILlmCompletionService
{
    public async Task<string> CompleteAsync(
        string prompt,
        CancellationToken ct = default)
    {
        // 未指定 Provider 時沿用 Copilot 預設設定，維持既有使用者行為。
        var profile = await providerService.GetProfileAsync("copilot-default", ct);
        var client = copilotClientService.GetClient();
        var agent = client.AsAIAgent(new SessionConfig
        {
            Streaming = true,
            OnPermissionRequest = permissionHandlerFactory.Create(),
        });

        return await RunCompletionAsync(prompt, agent, profile.Id, profile.ModelId, ct);
    }

    public async Task<string> CompleteAsync(
        string prompt,
        string? providerProfileId,
        string? modelId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerProfileId) && string.IsNullOrWhiteSpace(modelId))
            return await CompleteAsync(prompt, ct);

        var profile = await providerService.GetProfileAsync(providerProfileId, ct);
        var config = await configResolver.BuildSessionConfigAsync(
            profile,
            modelOverride: modelId,
            ct: ct);

        var client = copilotClientService.GetClient();
        var agent = client.AsAIAgent(config);

        return await RunCompletionAsync(
            prompt,
            agent,
            profile.Id,
            modelId ?? profile.ModelId,
            ct);
    }

    /// <summary>
    /// 收集 SDK 串流內容並回傳完整文字。只保留總逾時與取消，
    /// 不再為每個 token 寫入資料庫，降低 schema 與維護成本。
    /// </summary>
    private async Task<string> RunCompletionAsync(
        string prompt,
        AIAgent agent,
        string providerId,
        string? modelId,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var sb = new StringBuilder();
        try
        {
            await foreach (var update in agent.RunStreamingAsync(
                               prompt,
                               cancellationToken: timeout.Token))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    sb.Append(update.Text);
            }

            logger.LogDebug(
                "LLM completion 完成（provider={ProviderId}, model={ModelId}, {Length} 字元）",
                providerId,
                modelId ?? "(default)",
                sb.Length);
            return sb.ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"模型請求逾時：{providerId} / {modelId ?? "(default)"} 超過十分鐘仍未完成。");
        }
    }
}
