using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Tools;

public sealed class GitCommitTool(
    IAgentPolicyEngine policy,
    IApprovalCoordinator approvals,
    IGitClient git,
    IVcsStateRepository state,
    IVcsProfileRepository profiles) : PolicyEnforcedAgentTool(policy, approvals)
{
    public override ToolDescriptor Descriptor { get; } = new(
        "git_commit",
        "Commit current Git workspace changes.",
        AgentCapability.Write | AgentCapability.Execute,
        AgentRiskLevel.Medium,
        TimeSpan.FromMinutes(2));

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ToolExecutionRequest request,
        CancellationToken ct)
    {
        var profileId = RequireString(request.Arguments, "profileId");
        var message = RequireString(request.Arguments, "message");
        var profile = await profiles.GetAsync(profileId, ct);
        var before = await git.StatusAsync(request.Context.WorkspacePath, ct);
        var operation = NewOperation(
            request,
            profileId,
            VcsType.Git,
            "commit",
            sslVerificationEnabled: profile?.SslVerificationEnabled ?? true,
            beforeRevision: ParseGitRevision(before.Output));
        await state.SaveOperationAsync(operation, ct);

        var result = await git.CommitAsync(profileId, request.Context.WorkspacePath, message, ct);
        Complete(operation, result.Success, result.CommitId, result.Error);
        await state.SaveOperationAsync(operation, ct);
        return new ToolExecutionResult(result.Success, result.Output, result.Error);
    }

    private static string? ParseGitRevision(string output) => output
        .Split('\n')
        .FirstOrDefault(line => line.StartsWith("# branch.oid ", StringComparison.Ordinal))?[13..]
        .Trim();

    internal static VcsOperation NewOperation(
        ToolExecutionRequest request,
        string profileId,
        VcsType type,
        string operation,
        string? targetRef = null,
        bool sslVerificationEnabled = true,
        string? beforeRevision = null) => new()
    {
        RunId = request.Context.RunId,
        ProjectId = request.Context.ProjectId,
        ConnectionProfileId = profileId,
        VcsType = type,
        Operation = operation,
        TargetRef = targetRef,
        SslVerificationEnabled = sslVerificationEnabled,
        BeforeRevision = beforeRevision,
    };

    internal static void Complete(
        VcsOperation operation,
        bool success,
        string? afterRevision,
        string? error)
    {
        operation.Status = success ? VcsOperationStatus.Succeeded : VcsOperationStatus.Failed;
        operation.AfterRevision = afterRevision;
        operation.ErrorSanitized = error;
        operation.EndedAt = DateTimeOffset.UtcNow;
    }
}

public sealed class GitPushTool(
    IAgentPolicyEngine policy,
    IApprovalCoordinator approvals,
    IGitClient git,
    IProtectedRefMatcher protectedRefs,
    IVcsStateRepository state,
    IVcsProfileRepository profiles) : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "git_push",
        "Push a Git branch without force.",
        AgentCapability.Execute | AgentCapability.Network | AgentCapability.ExternalSideEffect,
        AgentRiskLevel.High,
        TimeSpan.FromMinutes(10));

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var profileId = RequireString(request.Arguments, "profileId");
            var remote = RequireString(request.Arguments, "remote");
            var branch = RequireString(request.Arguments, "branch");
            var isProtected = await protectedRefs.IsProtectedAsync(
                VcsType.Git,
                branch,
                request.Context.ProjectId,
                ct);
            var permission = new AgentPermissionRequest(
                Descriptor.Name,
                Descriptor.Capabilities,
                Descriptor.RiskLevel,
                $"{remote}/{branch}",
                request.Context.WorkspacePath,
                isProtected ? "Push to a protected Git branch." : Descriptor.Description);
            var decision = policy.Evaluate(
                new AgentPolicyContext(request.Context.Mode, request.Context.WorkspacePath, isProtected),
                permission);
            if (decision.Kind == PolicyDecisionKind.Deny)
                return new ToolExecutionResult(false, "", decision.Reason);

            var approvalRequired = decision.Kind == PolicyDecisionKind.RequireApproval || isProtected;
            if (approvalRequired)
            {
                var approval = await approvals.RequestAsync(request.Context.RunId, permission, ct);
                if (!approval.Approved)
                {
                    return new ToolExecutionResult(
                        false,
                        "",
                        approval.Comment ?? "Push rejected.",
                        ApprovalRequired: true,
                        ApprovalResult: "rejected");
                }
            }

            var profile = await profiles.GetAsync(profileId, ct);
            var before = await git.StatusAsync(request.Context.WorkspacePath, ct);
            var operation = GitCommitTool.NewOperation(
                request,
                profileId,
                VcsType.Git,
                "push",
                branch,
                profile?.SslVerificationEnabled ?? true,
                ParseGitRevision(before.Output));
            await state.SaveOperationAsync(operation, ct);
            var result = await git.PushAsync(
                profileId,
                request.Context.WorkspacePath,
                remote,
                branch,
                ct);
            GitCommitTool.Complete(operation, result.Success, operation.BeforeRevision, result.Error);
            await state.SaveOperationAsync(operation, ct);
            return new ToolExecutionResult(
                result.Success,
                result.Output,
                result.Error,
                ApprovalRequired: approvalRequired,
                ApprovalResult: approvalRequired ? "approved" : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolExecutionResult(false, "", ex.Message);
        }
    }

    private static string RequireString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new ArgumentException($"Argument '{name}' is required.");

    private static string? ParseGitRevision(string output) => output
        .Split('\n')
        .FirstOrDefault(line => line.StartsWith("# branch.oid ", StringComparison.Ordinal))?[13..]
        .Trim();
}

public sealed class SvnCommitTool(
    IAgentPolicyEngine policy,
    IApprovalCoordinator approvals,
    ISvnClient svn,
    IProtectedRefMatcher protectedRefs,
    IVcsStateRepository state,
    IVcsProfileRepository profiles) : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "svn_commit",
        "Commit SVN working copy changes.",
        AgentCapability.Write | AgentCapability.Execute | AgentCapability.Network |
        AgentCapability.ExternalSideEffect,
        AgentRiskLevel.High,
        TimeSpan.FromMinutes(10));

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var profileId = RequireString(request.Arguments, "profileId");
            var message = RequireString(request.Arguments, "message");
            var target = RequireString(request.Arguments, "targetPath");
            var isProtected = await protectedRefs.IsProtectedAsync(
                VcsType.Svn,
                target,
                request.Context.ProjectId,
                ct);
            var permission = new AgentPermissionRequest(
                Descriptor.Name,
                Descriptor.Capabilities,
                Descriptor.RiskLevel,
                target,
                request.Context.WorkspacePath,
                isProtected ? "Commit to a protected SVN path." : Descriptor.Description);
            var decision = policy.Evaluate(
                new AgentPolicyContext(request.Context.Mode, request.Context.WorkspacePath, isProtected),
                permission);
            if (decision.Kind == PolicyDecisionKind.Deny)
                return new ToolExecutionResult(false, "", decision.Reason);

            var approvalRequired = decision.Kind == PolicyDecisionKind.RequireApproval || isProtected;
            if (approvalRequired)
            {
                var approval = await approvals.RequestAsync(request.Context.RunId, permission, ct);
                if (!approval.Approved)
                {
                    return new ToolExecutionResult(
                        false,
                        "",
                        approval.Comment ?? "SVN commit rejected.",
                        ApprovalRequired: true,
                        ApprovalResult: "rejected");
                }
            }

            var profile = await profiles.GetAsync(profileId, ct);
            var before = await svn.GetRevisionAsync(profileId, request.Context.WorkspacePath, ct);
            var operation = GitCommitTool.NewOperation(
                request,
                profileId,
                VcsType.Svn,
                "commit",
                target,
                profile?.SslVerificationEnabled ?? true,
                before.Success ? before.Output.Trim() : null);
            await state.SaveOperationAsync(operation, ct);
            var result = await svn.CommitAsync(profileId, request.Context.WorkspacePath, message, ct);
            GitCommitTool.Complete(operation, result.Success, result.Revision, result.Error);
            await state.SaveOperationAsync(operation, ct);
            return new ToolExecutionResult(
                result.Success,
                result.Output,
                result.Error,
                ApprovalRequired: approvalRequired,
                ApprovalResult: approvalRequired ? "approved" : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolExecutionResult(false, "", ex.Message);
        }
    }

    private static string RequireString(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new ArgumentException($"Argument '{name}' is required.");
}
