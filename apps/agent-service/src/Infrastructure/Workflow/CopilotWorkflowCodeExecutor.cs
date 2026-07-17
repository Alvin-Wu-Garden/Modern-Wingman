using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.Orchestration;
using AgentService.Infrastructure.Providers;
using GitHub.Copilot;

namespace AgentService.Infrastructure.Workflow;

public interface IWorkflowCodeExecutor
{
    Task<string> ExecuteAsync(
        WorkflowRunRequest request,
        string plan,
        string context,
        string? feedback,
        CancellationToken ct = default);
}

public sealed class CopilotWorkflowCodeExecutor(
    CopilotClientService copilotClientService,
    CopilotPermissionHandlerFactory permissionHandlerFactory,
    IRunEventBus eventBus) : IWorkflowCodeExecutor
{
    public async Task<string> ExecuteAsync(
        WorkflowRunRequest request,
        string plan,
        string context,
        string? feedback,
        CancellationToken ct = default)
    {
        var client = copilotClientService.GetClient();
        var systemContent = $"""
            <workspace>
              <path>{request.WorkspacePath}</path>
            </workspace>

            {context}
            """;

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Streaming = true,
            OnPermissionRequest = permissionHandlerFactory.Create(
                request.PlanOnly ? Domain.Models.AgentMode.Plan : request.Mode,
                request.WorkspacePath,
                request.RunId),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemContent,
            },
        });

        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastMessage = new StringBuilder();
        using var subscription = session.On<SessionEvent>(evt => HandleEvent(evt, request.RunId, lastMessage, complete));

        var prompt = feedback is null
            ? $"""
              依照以下計畫執行實作。修改檔案後不需自行跑完整測試（外層工作流會驗證）。

              # 計畫
              {plan}

              # 任務
              {request.Task}
              """
            : $"""
              上次實作的驗證失敗了，請根據錯誤訊息修復（找根因，不要壓制錯誤）：

              # 驗證錯誤
              {feedback}
              """;

        await session.SendAsync(new MessageOptions { Prompt = prompt });
        await complete.Task.WaitAsync(ct);
        return lastMessage.ToString();
    }

    private void HandleEvent(
        SessionEvent evt,
        string runId,
        StringBuilder lastMessage,
        TaskCompletionSource complete)
    {
        switch (evt)
        {
            case AssistantMessageDeltaEvent delta:
                Publish(RunStreamEvent.Token(runId, delta.Data.DeltaContent ?? ""));
                break;
            case AssistantMessageEvent message:
                lastMessage.Clear();
                lastMessage.Append(message.Data.Content ?? "");
                Publish(RunStreamEvent.Message(runId, message.Data.Content ?? ""));
                break;
            case ToolExecutionStartEvent toolStart:
                Publish(RunStreamEvent.ToolCall(runId, toolStart.Data.ToolName, toolStart.Data.Arguments));
                break;
            case ToolExecutionCompleteEvent toolComplete:
                Publish(RunStreamEvent.ToolResult(
                    runId,
                    toolComplete.Data.ToolDescription?.Name ?? toolComplete.Data.ToolCallId,
                    toolComplete.Data.Result));
                break;
            case SessionIdleEvent:
                complete.TrySetResult();
                break;
            case SessionErrorEvent error:
                complete.TrySetException(new InvalidOperationException($"Copilot session 錯誤：{error.Data.Message}"));
                break;
        }
    }

    private void Publish(RunStreamEvent evt) =>
        eventBus.PublishAsync(evt).GetAwaiter().GetResult();
}
