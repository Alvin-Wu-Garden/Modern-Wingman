using System.Text;
using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.AgentRuntime.Factories;
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
    ByokAgentFactory byokAgentFactory,
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
        AIAgent agent;
        if (profile.Kind == ProviderKind.CopilotDefault)
        {
            // 只有 GitHub Copilot 登入路徑使用 Copilot SDK；BYOK 不再被包裝成
            // Copilot ProviderConfig，避免背景 Query Rewrite 與主對話走不同協定。
            var client = copilotClientService.GetClient();
            agent = client.AsAIAgent(new SessionConfig
            {
                Streaming = true,
                Model = modelId ?? profile.ModelId,
                OnPermissionRequest = permissionHandlerFactory.Create(),
            });
        }
        else
        {
            agent = byokAgentFactory.CreateAgent(new AgentCreationContext
            {
                Profile = profile,
                ModelOverride = modelId,
                Instructions = "請直接完成下列內部文字處理要求，僅回傳結果。",
            }) ?? throw new InvalidOperationException(
                $"Provider Profile「{profile.Id}」尚未設定有效 API Key。請至設定頁完成設定。");
        }

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
                "LLM 完成回覆（ProviderId={ProviderId}, ModelId={ModelId}, 長度={Length} 字元）",
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
