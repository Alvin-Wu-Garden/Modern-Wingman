using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Tools;

public sealed class SearchFilesTool(
    IAgentPolicyEngine policyEngine,
    IApprovalCoordinator approvalCoordinator,
    IProcessRunner processRunner)
    : PolicyEnforcedAgentTool(policyEngine, approvalCoordinator)
{
    public override ToolDescriptor Descriptor { get; } = new(
        "search_files",
        "Search workspace text with ripgrep and return file, line, and matching text.",
        AgentCapability.Read | AgentCapability.Execute,
        AgentRiskLevel.Low,
        TimeSpan.FromSeconds(30));

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ToolExecutionRequest request,
        CancellationToken ct)
    {
        var pattern = RequireString(request.Arguments, "pattern");
        var relativePath = request.Arguments.TryGetValue("path", out var pathValue) &&
                           pathValue is string pathText &&
                           !string.IsNullOrWhiteSpace(pathText)
            ? pathText
            : ".";
        var searchPath = WorkspacePathGuard.ResolveReadable(
            request.Context.WorkspacePath,
            relativePath);
        var result = await processRunner.RunAsync(
            new ProcessInvocation(
                "rg",
                ["--line-number", "--no-heading", "--color", "never",
                 "--glob", "!.git-credentials", "--glob", "!.npmrc", "--glob", "!.pypirc",
                 "--glob", "!.netrc", "--glob", "!id_rsa", "--glob", "!id_ed25519",
                 "--glob", "!credentials.json", "--glob", "!Login Data", "--glob", "!Cookies",
                 "--", pattern, searchPath],
                request.Context.WorkspacePath,
                Descriptor.DefaultTimeout),
            ct);

        var success = result.ExitCode is 0 or 1 && !result.TimedOut;
        return new ToolExecutionResult(
            success,
            result.StandardOutput,
            success ? null : result.StandardError,
            result.ExitCode,
            result.TimedOut,
            result.DurationMs);
    }
}
