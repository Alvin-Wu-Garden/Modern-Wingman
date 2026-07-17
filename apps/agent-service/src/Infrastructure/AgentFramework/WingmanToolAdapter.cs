using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using Microsoft.Extensions.AI;

namespace AgentService.Infrastructure.AgentFramework;

public static class WingmanToolAdapter
{
    public static string BuildPrompt(IToolRegistry registry)
    {
        var builder=new StringBuilder("\n## Modern Wingman Tools\nUse call_wingman_tool with one of these registered tools. Never emulate a tool by constructing an unregistered shell command.\n");foreach(var tool in registry.ListTools())builder.Append("- ").Append(tool.Name).Append(": ").Append(tool.Description).Append(" schema=").AppendLine(tool.InputSchemaJson);return builder.ToString();
    }
    public static AIFunction Create(IToolRegistry registry,AgentCreationContext context)=>AIFunctionFactory.Create(async(string toolName,string argumentsJson,CancellationToken ct)=>
    {
        IReadOnlyDictionary<string,object?> arguments;try{arguments=JsonSerializer.Deserialize<Dictionary<string,object?>>(argumentsJson)??new Dictionary<string,object?>();}catch(JsonException ex){return $"Invalid tool arguments JSON: {ex.Message}";}
        var result=await registry.ExecuteAsync(new ToolExecutionRequest(toolName,arguments,new ToolExecutionContext(context.RunId??Guid.NewGuid().ToString("N"),context.Mode,context.WorkspacePath??Environment.CurrentDirectory,context.ProjectId)),ct);return JsonSerializer.Serialize(result);
    },name:"call_wingman_tool",description:"Call a registered Modern Wingman tool. Arguments are a JSON object matching the selected tool schema.");
}
