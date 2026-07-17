using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.Tools;

public sealed class ToolRegistry : IManagedToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;
    private readonly object _gate = new();

    private readonly IToolCallTelemetry _telemetry;
    private readonly IAgentHookDispatcher? _hooks;

    public ToolRegistry(
        IEnumerable<IAgentTool> tools,
        IToolCallTelemetry telemetry,
        IAgentHookDispatcher? hooks = null)
    {
        _telemetry = telemetry;
        _hooks = hooks;
        _tools = tools.ToDictionary(
            tool => tool.Descriptor.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ToolDescriptor> ListTools()
    {
        lock (_gate) return _tools.Values
            .Select(tool => tool.Descriptor)
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TryGet(string name, out IAgentTool? tool)
    {
        lock (_gate) return _tools.TryGetValue(name, out tool);
    }

    public void Register(IAgentTool tool)
    {
        lock (_gate) _tools[tool.Descriptor.Name] = tool;
    }

    public void ReplaceSource(string source, IReadOnlyList<IAgentTool> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(tools);
        lock (_gate)
        {
            foreach (var name in _tools.Where(pair => string.Equals(pair.Value.Descriptor.Source, source, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key).ToArray())
                _tools.Remove(name);
            foreach (var tool in tools)
            {
                if (!string.Equals(tool.Descriptor.Source, source, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Managed replacement tools must use the replacement source.");
                _tools[tool.Descriptor.Name] = tool;
            }
        }
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
    {
        IAgentTool? tool;
        lock (_gate) _tools.TryGetValue(request.ToolName, out tool);
        if (tool is null)
        {
            return new ToolExecutionResult(
                false,
                "",
                $"Unknown tool: {request.ToolName}");
        }
        if (_hooks is not null)
            await _hooks.DispatchAsync(new(
                AgentHookStage.BeforeTool,
                request.Context.RunId,
                request.ToolName,
                request.Context.WorkspacePath,
                request.Arguments), ct);
        var telemetryId=await _telemetry.StartAsync(request,ct);
        try{var result=await tool.ExecuteAsync(request,ct);await _telemetry.CompleteAsync(telemetryId,result,ct);if(_hooks is not null){await _hooks.DispatchAsync(new(AgentHookStage.AfterTool,request.Context.RunId,request.ToolName,request.Context.WorkspacePath,request.Arguments,result),ct);if(result.Success&&request.ToolName is "apply_patch" or "delete_file")await _hooks.DispatchAsync(new(AgentHookStage.AfterFileChange,request.Context.RunId,request.ToolName,request.Context.WorkspacePath,request.Arguments,result),ct);}return result;}
        catch(OperationCanceledException){await _telemetry.CompleteAsync(telemetryId,new ToolExecutionResult(false,"","Cancelled"),CancellationToken.None);throw;}
    }
}
