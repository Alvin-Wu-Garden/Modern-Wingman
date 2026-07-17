using AgentService.Application.Models;
namespace AgentService.Application.Contracts;
public interface ISkillScriptRunner
{
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request,CancellationToken ct=default);
}
