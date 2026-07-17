using AgentService.Application.Contracts;
using AgentService.Application.Models;
using GitHub.Copilot;

namespace AgentService.Infrastructure.Orchestration;

/// <summary>
/// 將 CopilotSession 事件橋接為 RunStreamEvent（WS2：自 RunOrchestrator 抽出，SRP）。
///
/// 職責：SessionEvent → RunStreamEvent 的轉譯與發布，
/// 以及偵測 session 完成/錯誤，通知 TaskCompletionSource。
/// </summary>
public sealed class CopilotEventBridge(IRunEventBus eventBus)
{
    /// <summary>
    /// 處理單一 session 事件。
    /// 注意：事件回呼在 CLI 的內部執行緒觸發，使用 GetAwaiter().GetResult()
    /// 是安全的，因為 Channel 寫入不會阻塞（BoundedChannelFullMode.DropOldest）。
    /// </summary>
    public void Handle(string runId, SessionEvent evt, TaskCompletionSource sessionCompleteTcs)
    {
        switch (evt)
        {
            // 串流 token（逐字回應）
            case AssistantMessageDeltaEvent delta:
                eventBus.PublishAsync(
                    RunStreamEvent.Token(runId, delta.Data.DeltaContent ?? "")
                ).GetAwaiter().GetResult();
                break;

            // 完整助理訊息（streaming 完成後的最終版本）
            case AssistantMessageEvent msg:
                eventBus.PublishAsync(
                    RunStreamEvent.Message(runId, msg.Data.Content ?? "")
                ).GetAwaiter().GetResult();
                break;

            // 工具呼叫開始
            case ToolExecutionStartEvent toolStart:
                eventBus.PublishAsync(
                    RunStreamEvent.ToolCall(runId, toolStart.Data.ToolName, toolStart.Data.Arguments)
                ).GetAwaiter().GetResult();
                break;

            // 工具呼叫完成
            case ToolExecutionCompleteEvent toolComplete:
                eventBus.PublishAsync(
                    RunStreamEvent.ToolResult(
                        runId,
                        toolComplete.Data.ToolDescription?.Name ?? toolComplete.Data.ToolCallId,
                        toolComplete.Data.Result)
                ).GetAwaiter().GetResult();
                break;

            // Session 閒置 = 此次 run 的 agentic loop 完成
            case SessionIdleEvent:
                sessionCompleteTcs.TrySetResult();
                break;

            // Session 錯誤
            case SessionErrorEvent err:
                sessionCompleteTcs.TrySetException(
                    new InvalidOperationException($"Copilot session 錯誤：{err.Data.Message}"));
                break;
        }
    }
}
