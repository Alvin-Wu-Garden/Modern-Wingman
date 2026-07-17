using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Tools;

public abstract class StructuredProcessTool(
    IAgentPolicyEngine policy,
    IApprovalCoordinator approvals,
    IProcessRunner runner,
    ISensitiveDataRedactor redactor,
    IRunEventBus eventBus) : PolicyEnforcedAgentTool(policy, approvals)
{
    protected async Task<ToolExecutionResult> RunAsync(ToolExecutionRequest request, CancellationToken ct)
    {
        var executable = RequireString(request.Arguments, "executable");
        var args = ReadArguments(request.Arguments);
        var result = await runner.RunAsync(new ProcessInvocation(
            executable,
            args,
            request.Context.WorkspacePath,
            Descriptor.DefaultTimeout,
            OnOutput: (line, token) => eventBus.PublishAsync(
                RunStreamEvent.ToolOutput(request.Context.RunId, Descriptor.Name, line),
                token)), ct);
        var success = result.ExitCode == 0 && !result.TimedOut;
        return new(success, redactor.Redact(result.StandardOutput), success ? null : redactor.Redact(result.StandardError), result.ExitCode, result.TimedOut, result.DurationMs);
    }

    private static IReadOnlyList<string> ReadArguments(IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("arguments", out var value) || value is null) return [];
        if (value is IEnumerable<string> strings) return strings.ToList();
        if (value is JsonElement { ValueKind: JsonValueKind.Array } json) return json.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        throw new ArgumentException("arguments must be an array of strings.");
    }
}

public sealed class RunBuildTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals, IProcessRunner runner, ISensitiveDataRedactor redactor, IRunEventBus eventBus)
    : StructuredProcessTool(policy, approvals, runner, redactor, eventBus)
{
    public override ToolDescriptor Descriptor { get; } = new("run_build", "Run a project build command.", AgentCapability.Execute, AgentRiskLevel.Medium, TimeSpan.FromMinutes(20));
    protected override Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct) => RunAsync(request, ct);
}

public sealed class RunTestTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals, IProcessRunner runner, ISensitiveDataRedactor redactor, IRunEventBus eventBus)
    : StructuredProcessTool(policy, approvals, runner, redactor, eventBus)
{
    public override ToolDescriptor Descriptor { get; } = new("run_test", "Run a project test command.", AgentCapability.Execute, AgentRiskLevel.Medium, TimeSpan.FromMinutes(30));
    protected override Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct) => RunAsync(request, ct);
}

public sealed class GitStatusTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals, IGitClient git)
    : PolicyEnforcedAgentTool(policy, approvals)
{
    public override ToolDescriptor Descriptor { get; } = new("git_status", "Read Git working tree status.", AgentCapability.Read | AgentCapability.Execute, AgentRiskLevel.Low, TimeSpan.FromSeconds(30));
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct) { var result=await git.StatusAsync(request.Context.WorkspacePath,ct); return new(result.Success,result.Output,result.Error); }
}

public sealed class GitDiffTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals, IGitClient git)
    : PolicyEnforcedAgentTool(policy, approvals)
{
    public override ToolDescriptor Descriptor { get; } = new("git_diff", "Read Git workspace diff.", AgentCapability.Read | AgentCapability.Execute, AgentRiskLevel.Low, TimeSpan.FromSeconds(30));
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct) { var staged=request.Arguments.TryGetValue("staged",out var value)&&value is true; var result=await git.DiffAsync(request.Context.WorkspacePath,staged,ct); return new(result.Success,result.Output,result.Error); }
}

public sealed class GitBranchTool(IAgentPolicyEngine policy,IApprovalCoordinator approvals,IGitClient git):PolicyEnforcedAgentTool(policy,approvals)
{
    public override ToolDescriptor Descriptor{get;}=new("git_branch","List, switch, or create a Wingman Git branch.",AgentCapability.Read|AgentCapability.Write|AgentCapability.Execute,AgentRiskLevel.Medium,TimeSpan.FromSeconds(30));
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request,CancellationToken ct)
    {
        var action=request.Arguments.TryGetValue("action",out var value)?value?.ToString()?.ToLowerInvariant():"list";
        GitCommandResult result;if(action=="list")result=await git.BranchesAsync(request.Context.WorkspacePath,ct);else{var branch=request.Arguments.TryGetValue("branch",out var name)&&name is string text&&!string.IsNullOrWhiteSpace(text)?text:throw new ArgumentException("branch is required.");var create=action=="create";if(create&&!branch.StartsWith("wingman/",StringComparison.OrdinalIgnoreCase))branch="wingman/"+branch.Trim('/');var start=request.Arguments.TryGetValue("startPoint",out var point)?point?.ToString():null;result=await git.SwitchAsync(request.Context.WorkspacePath,branch,create,start,ct);}return new(result.Success,result.Output,result.Error);
    }
}

public sealed class SvnStatusTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals, ISvnClient svn)
    : PolicyEnforcedAgentTool(policy, approvals)
{
    public override ToolDescriptor Descriptor { get; } = new("svn_status", "Read SVN working copy status.", AgentCapability.Read | AgentCapability.Execute, AgentRiskLevel.Low, TimeSpan.FromSeconds(30));
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct) { var result=await svn.StatusAsync(request.Context.WorkspacePath,ct); return new(result.Success,result.Output,result.Error); }
}

public sealed class SvnDiffTool(IAgentPolicyEngine policy, IApprovalCoordinator approvals, ISvnClient svn)
    : PolicyEnforcedAgentTool(policy, approvals)
{
    public override ToolDescriptor Descriptor { get; } = new("svn_diff", "Read SVN working copy diff.", AgentCapability.Read | AgentCapability.Execute, AgentRiskLevel.Low, TimeSpan.FromSeconds(30));
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request, CancellationToken ct) { var result=await svn.DiffAsync(request.Context.WorkspacePath,ct); return new(result.Success,result.Output,result.Error); }
}
