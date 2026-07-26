using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using AgentService.Infrastructure.Skills;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.AgentFramework;

#pragma warning disable GHCP001 // SDK 的權限決策 API 是拒絕內建工具所需的正式掛點。

/// <summary>
/// CopilotDefault 路徑：GitHub Copilot SDK → MAF AsAIAgent()。
/// 使用者以 GitHub PAT / 系統登入認證，走 Copilot 訂閱計費。
/// </summary>
public sealed class CopilotAgentFactory(
    CopilotClientService copilotClientService,
    ISkillProvider skillProvider,
    ILogger<CopilotAgentFactory> logger) : IAgentFactory
{
    public ProviderKind Kind => ProviderKind.CopilotDefault;

    public AIAgent? CreateAgent(AgentCreationContext context)
    {
        var sessionConfig = new SessionConfig
        {
            Streaming = true,
            Model = context.ModelOverride ?? context.Profile.ModelId,
            // Modern Wingman 的一般對話不執行檔案、Shell、MCP 或其他外部動作。
            OnPermissionRequest = (_, _) => Task.FromResult(
                PermissionDecision.Reject("Modern Wingman 對話模式不允許執行工具。")),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                // Skills 清單以 progressive disclosure 附加於指示之後
                Content = "\n" + context.Instructions + context.SkillsPrompt,
            },
        };

        var client = copilotClientService.GetClient();
        var agent = client.AsAIAgent(sessionConfig);

        logger.LogDebug(
            "CopilotAgentFactory 建立 Agent（model={Model}, skills={SkillCount}）",
            context.ModelOverride ?? context.Profile.ModelId,
            skillProvider.ListSkills().Count);

        return agent;
    }
}

#pragma warning restore GHCP001
