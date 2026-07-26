using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace AgentService.Infrastructure.Orchestration;

#pragma warning disable GHCP001 // SDK 的權限決策 API 是拒絕內建工具所需的正式掛點。

/// <summary>
/// Modern Wingman 只提供文字對話與唯讀 GraphRAG 回答，因此拒絕 Copilot SDK
/// 發出的所有檔案、Shell、網路、MCP 與自訂工具權限請求。
/// </summary>
public sealed class CopilotPermissionHandlerFactory
{
    public Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> Create() =>
        (_, _) => Task.FromResult(
            PermissionDecision.Reject("Modern Wingman 對話模式不允許執行工具。"));
}

#pragma warning restore GHCP001
