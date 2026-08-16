using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.AgentRuntime.Factories;

#pragma warning disable GHCP001 // SDK 的權限決策 API 是拒絕內建工具所需的正式掛點。

/// <summary>
/// CopilotDefault 路徑：GitHub Copilot SDK → MAF AsAIAgent()。
/// 使用者以 GitHub PAT / 系統登入認證，走 Copilot 訂閱計費。
/// </summary>
public sealed class CopilotAgentFactory(
    CopilotClientService copilotClientService,
    ILogger<CopilotAgentFactory> logger) : IAgentFactory
{
    public ProviderKind Kind => ProviderKind.CopilotDefault;

    public AIAgent? CreateAgent(AgentCreationContext context)
    {
        var sessionConfig = BuildSessionConfig(context);

        var client = copilotClientService.GetClient();
        var agent = client.AsAIAgent(sessionConfig);

        logger.LogDebug(
            "CopilotAgentFactory 建立 Agent。Model={Model}, ToolCount={ToolCount}",
            context.ModelOverride ?? context.Profile.ModelId,
            context.Tools.Count);

        return agent;
    }

    /// <summary>
    /// 將共用 Agent context 轉為 Copilot SessionConfig。
    /// 這個測試接縫用來確認只有本輪明確提供的自訂唯讀工具會進入 session。
    /// </summary>
    internal static SessionConfig BuildSessionConfig(AgentCreationContext context)
    {
        // Copilot SDK 會在執行「自訂 Function Tool」前同樣觸發權限回呼。
        // 因此不能無條件拒絕所有要求，否則模型雖看得到工具，實際呼叫仍會失敗。
        // 白名單只取自本輪已綁定 projectId/rootPath 的 AIFunction；Shell、寫檔、
        // MCP、URL 及任何未列名工具仍一律拒絕，避免擴張專案解析 Agent 的權限。
        var allowedToolNames = context.Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        return new SessionConfig
        {
            Streaming = true,
            Model = context.ModelOverride ?? context.Profile.ModelId,
            // 僅將呼叫端明確提供的自訂 Function Tool 加入 allowlist；
            // 不開放 Copilot 內建 Shell、檔案修改、MCP 或其他環境工具。
            Tools = context.Tools.Count == 0
                ? null
                : context.Tools.Cast<AIFunctionDeclaration>().ToList(),
            // 明確指定空集合停用所有內建工具，避免 SDK 預設啟用內建工具後
            // 被 OnPermissionRequest 拒絕，導致模型靜默返回空回應。
            AvailableTools = context.Tools.Count == 0
                ? new ToolSet()
                : new ToolSet().AddCustom("*"),
            // 自訂 Function Tool 仍會觸發 SDK 權限要求；只核准本輪明確傳入的名稱。
            OnPermissionRequest = (request, _) => Task.FromResult(
                request is PermissionRequestCustomTool customTool &&
                allowedToolNames.Contains(customTool.ToolName)
                    ? PermissionDecision.ApproveOnce()
                    : PermissionDecision.Reject(
                        "Modern Wingman 只允許執行本輪已核准的唯讀專案工具。")),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = "\n" + context.Instructions + context.SkillsPrompt,
            },
        };
    }
}

#pragma warning restore GHCP001
