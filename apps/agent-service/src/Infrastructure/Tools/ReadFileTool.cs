using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Tools;

public sealed class ReadFileTool(
    IAgentPolicyEngine policyEngine,
    IApprovalCoordinator approvalCoordinator)
    : PolicyEnforcedAgentTool(policyEngine, approvalCoordinator)
{
    private const int MaxCharacters = 1_000_000;

    public override ToolDescriptor Descriptor { get; } = new(
        "read_file",
        "Read a UTF-8 text file inside the active workspace.",
        AgentCapability.Read,
        AgentRiskLevel.Low,
        TimeSpan.FromSeconds(30));

    protected override AgentPermissionRequest BuildPermissionRequest(ToolExecutionRequest request)
    {
        var target = WorkspacePathGuard.ResolveReadable(
            request.Context.WorkspacePath,
            RequireString(request.Arguments, "path"));
        return new AgentPermissionRequest(
            Descriptor.Name,
            Descriptor.Capabilities,
            Descriptor.RiskLevel,
            target,
            request.Context.WorkspacePath,
            Descriptor.Description);
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ToolExecutionRequest request,
        CancellationToken ct)
    {
        var path = WorkspacePathGuard.ResolveReadable(
            request.Context.WorkspacePath,
            RequireString(request.Arguments, "path"));
        var info = new FileInfo(path);
        if (!info.Exists)
            return new ToolExecutionResult(false, "", $"File not found: {path}");
        if (info.Length > MaxCharacters * 4L)
            return new ToolExecutionResult(false, "", "File exceeds the read limit.");

        var content = await File.ReadAllTextAsync(path, ct);
        if (content.Length > MaxCharacters)
            content = content[..MaxCharacters] + Environment.NewLine + "[content truncated]";
        return new ToolExecutionResult(true, content);
    }
}
