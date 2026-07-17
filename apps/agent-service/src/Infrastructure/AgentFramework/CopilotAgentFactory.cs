using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using AgentService.Infrastructure.Skills;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using AgentService.Infrastructure.Orchestration;
using AgentService.Infrastructure.AgentFramework.Plugins;

namespace AgentService.Infrastructure.AgentFramework;

/// <summary>
/// CopilotDefault 路徑：GitHub Copilot SDK → MAF AsAIAgent()。
/// 使用者以 GitHub PAT / 系統登入認證，走 Copilot 訂閱計費。
/// </summary>
public sealed class CopilotAgentFactory(
    CopilotClientService copilotClientService,
    ISkillProvider skillProvider,
    IToolRegistry toolRegistry,
    MafPluginRuntimeAdapter pluginRuntime,
    CopilotPermissionHandlerFactory permissionHandlerFactory,
    ILogger<CopilotAgentFactory> logger) : IAgentFactory
{
    public ProviderKind Kind => ProviderKind.CopilotDefault;

    public AIAgent? CreateAgent(AgentCreationContext context)
    {
        var sessionConfig = new SessionConfig
        {
            Streaming = true,
            Model = context.ModelOverride ?? context.Profile.ModelId,
            OnPermissionRequest = permissionHandlerFactory.Create(
                context.Mode,
                context.WorkspacePath,
                context.RunId),
            Tools = [WingmanToolAdapter.Create(toolRegistry,context)],
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                // Skills 清單以 progressive disclosure 附加於指示之後
                Content = "\n" + context.Instructions + context.SkillsPrompt + pluginRuntime.BuildContextPrompt() + WingmanToolAdapter.BuildPrompt(toolRegistry),
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
