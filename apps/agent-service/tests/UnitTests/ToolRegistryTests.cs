using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;

namespace AgentService.UnitTests;

public sealed class ToolRegistryTests
{
    [Fact]
    public async Task Execute_RegisteredTool_IsWrappedInTelemetry()
    {
        var telemetry=new Telemetry();var registry=new ToolRegistry([new EchoTool()],telemetry);var request=new ToolExecutionRequest("echo",new Dictionary<string,object?>{{"value","ok"}},new("run",AgentMode.Auto,Environment.CurrentDirectory));var result=await registry.ExecuteAsync(request);Assert.True(result.Success);Assert.Equal("ok",result.Output);Assert.Equal(1,telemetry.Started);Assert.Equal(1,telemetry.Completed);
    }
    [Fact]
    public async Task Execute_UnknownTool_IsRejectedWithoutTelemetry()
    {
        var telemetry=new Telemetry();var registry=new ToolRegistry([],telemetry);var result=await registry.ExecuteAsync(new("missing",new Dictionary<string,object?>(),new("run",AgentMode.Auto,Environment.CurrentDirectory)));Assert.False(result.Success);Assert.Contains("Unknown tool",result.Error);Assert.Equal(0,telemetry.Started);
    }
    private sealed class EchoTool:IAgentTool{public ToolDescriptor Descriptor{get;}=new("echo","test",AgentCapability.Read,AgentRiskLevel.Low,TimeSpan.FromSeconds(1));public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request,CancellationToken ct=default)=>Task.FromResult(new ToolExecutionResult(true,request.Arguments["value"]?.ToString()??""));}
    private sealed class Telemetry:IToolCallTelemetry{public int Started;public int Completed;public Task<string?> StartAsync(ToolExecutionRequest request,CancellationToken ct=default){Started++;return Task.FromResult<string?>("id");}public Task CompleteAsync(string? id,ToolExecutionResult result,CancellationToken ct=default){Completed++;return Task.CompletedTask;}}
}
