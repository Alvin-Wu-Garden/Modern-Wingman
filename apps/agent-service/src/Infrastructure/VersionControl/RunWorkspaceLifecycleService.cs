using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;

namespace AgentService.Infrastructure.VersionControl;

public sealed class RunWorkspaceLifecycleService(
    IRunRepository runs,
    IVcsStateRepository vcsState,
    IVcsRuntimeResolver runtimes,
    IProcessRunner processRunner,
    IGitClient git,
    ISvnClient svn,
    IShadowGitService shadowGit,
    IProtectedRefMatcher protectedRefs,
    IAuditEventRecorder audit,
    IChangeSetService changeSets,
    IConfiguration configuration,
    IAgentSettingsStore? settings = null) : IRunWorkspaceLifecycleService
{
    public async Task<WorkspaceActionPreview> PreviewAsync(string runId,CancellationToken ct=default)
    {
        var run=await runs.GetAsync(runId,ct)??throw new KeyNotFoundException(runId);var binding=run.ProjectId is null?null:await vcsState.GetBindingAsync(run.ProjectId,ct);if(binding is null)return new(null,null,run.Branch,run.BaseRevision,false);var target=binding.VcsType==VcsType.Git?run.Branch:binding.RepositoryUrl;var isProtected=!string.IsNullOrWhiteSpace(target)&&await protectedRefs.IsProtectedAsync(binding.VcsType,target,run.ProjectId,ct);return new(binding.VcsType.ToString().ToLowerInvariant(),binding.RepositoryUrl??binding.RepositoryPath,target,run.BaseRevision??binding.Revision,isProtected);
    }

    public async Task<WorkspaceActionResult> ExecuteAsync(string runId,string action,string? message,bool protectedConfirmed,CancellationToken ct=default)
    {
        var run=await runs.GetAsync(runId,ct)??throw new KeyNotFoundException(runId);if(string.IsNullOrWhiteSpace(run.ExecutionWorkspacePath)||!Directory.Exists(run.ExecutionWorkspacePath))return new(false,action,Error:"Run has no retained execution workspace.");var binding=run.ProjectId is null?null:await vcsState.GetBindingAsync(run.ProjectId,ct);var normalized=action.Trim().ToLowerInvariant();WorkspaceActionResult result=normalized switch
        {
            "retain"=>new(true,normalized,"Workspace retained."),
            "discard"=>await DiscardAsync(run,ct),
            "apply"=>await ApplyAsync(run,binding,ct),
            "commit"=>await CommitAsync(run,binding,message,ct),
            "push"=>await PushAsync(run,binding,protectedConfirmed,ct),
            "svn_commit"=>await SvnCommitAsync(run,binding,message,protectedConfirmed,ct),
            _=>new(false,normalized,Error:"Unsupported workspace action."),
        };await audit.RecordAsync(new("workspace_action","agent_run",run.Id,normalized,result.Success?"success":"failed","user",TraceId:run.TraceId,DetailsJson:System.Text.Json.JsonSerializer.Serialize(new{run.WorkspaceStrategy,run.Branch,run.BaseRevision,result.RequiresProtectedConfirmation,error=result.Error})),CancellationToken.None);return result;
    }

    private async Task<WorkspaceActionResult> CommitAsync(RunEntity run,ProjectVcsBinding? binding,string? message,CancellationToken ct)
    {
        if(binding?.VcsType!=VcsType.Git||string.IsNullOrWhiteSpace(binding.ConnectionProfileId))return new(false,"commit",Error:"Git binding/profile is missing.");var result=await git.CommitAsync(binding.ConnectionProfileId,run.ExecutionWorkspacePath!,string.IsNullOrWhiteSpace(message)?$"Wingman run {run.Id[..8]}":message,ct);return new(result.Success,"commit",result.Output,result.Error);
    }

    private async Task<WorkspaceActionResult> PushAsync(RunEntity run,ProjectVcsBinding? binding,bool confirmed,CancellationToken ct)
    {
        if(binding?.VcsType!=VcsType.Git||string.IsNullOrWhiteSpace(binding.ConnectionProfileId)||string.IsNullOrWhiteSpace(run.Branch))return new(false,"push",Error:"Git binding/profile/branch is missing.");var isProtected=await protectedRefs.IsProtectedAsync(VcsType.Git,run.Branch,run.ProjectId,ct);if(isProtected&&!confirmed)return new(false,"push",Error:"Protected branch confirmation is required.",RequiresProtectedConfirmation:true);var result=await git.PushAsync(binding.ConnectionProfileId,run.ExecutionWorkspacePath!,"origin",run.Branch,ct);return new(result.Success,"push",result.Output,result.Error);
    }

    private async Task<WorkspaceActionResult> SvnCommitAsync(
        RunEntity run,
        ProjectVcsBinding? binding,
        string? message,
        bool confirmed,
        CancellationToken ct)
    {
        if (binding?.VcsType != VcsType.Svn ||
            string.IsNullOrWhiteSpace(binding.ConnectionProfileId) ||
            string.IsNullOrWhiteSpace(binding.RepositoryUrl))
        {
            return new(false, "svn_commit", Error: "SVN binding/profile is missing.");
        }

        var isProtected = await protectedRefs.IsProtectedAsync(
            VcsType.Svn,
            binding.RepositoryUrl,
            run.ProjectId,
            ct);
        if (isProtected && !confirmed)
        {
            return new(
                false,
                "svn_commit",
                Error: "Protected SVN path confirmation is required.",
                RequiresProtectedConfirmation: true);
        }

        var shadow = Path.Combine(
            await GetRootAsync("workspace.shadow_git_root", "Workspace:ShadowGitRoot", "shadow-git", ct),
            run.ProjectId!);
        var applied = await shadowGit.ApplyToSvnAsync(
            binding.ConnectionProfileId,
            shadow,
            run.ExecutionWorkspacePath!,
            ct);
        if (!applied.Success)
        {
            if (applied.RevisionConflict)
            {
                run.Status = RunStatus.WaitingApproval;
                run.Error = applied.Error ?? "SVN revision conflict requires user resolution.";
                await runs.SaveAsync(run, ct);
            }

            return new(false, "svn_commit", Error: applied.Error);
        }

        var commit = await svn.CommitAsync(
            binding.ConnectionProfileId,
            run.WorkspacePath!,
            string.IsNullOrWhiteSpace(message) ? $"Wingman run {run.Id[..8]}" : message,
            ct);
        return new(commit.Success, "svn_commit", commit.Output, commit.Error);
    }

    private async Task<WorkspaceActionResult> ApplyAsync(RunEntity run,ProjectVcsBinding? binding,CancellationToken ct)
    {
        if(run.WorkspaceStrategy==WorkspaceStrategy.SvnShadowGit)return new(false,"apply",Error:"Use SVN Commit to apply a Shadow Git workspace.");if(run.WorkspaceStrategy!=WorkspaceStrategy.GitWorktree||string.IsNullOrWhiteSpace(run.Branch)||string.IsNullOrWhiteSpace(run.WorkspacePath))return new(false,"apply",Error:"Apply requires a Git worktree run.");var status=await git.StatusAsync(run.WorkspacePath,ct);if(!status.Success)return new(false,"apply",Error:status.Error);var sourceIsDirty=status.Output.Split('\n').Any(x=>!string.IsNullOrWhiteSpace(x)&&!x.StartsWith('#'));if(sourceIsDirty){if(!run.IncludeUncommittedChanges||string.IsNullOrWhiteSpace(run.CheckpointId))return new(false,"apply",Error:"Source workspace has uncommitted changes that were not included in this run baseline.");var applied=await changeSets.ApplyToWorkspaceAsync(run.CheckpointId,run.WorkspacePath,ct);return new(applied.Success,"apply",applied.Success?$"Applied {applied.RestoredFiles.Count} Agent changes to the dirty source workspace.":null,applied.Success?null:$"Source files changed after the run started: {string.Join(", ",applied.Conflicts)}");}if(binding is null)return new(false,"apply",Error:"Git binding is missing.");var commit=await CommitAsync(run,binding,null,ct);if(!commit.Success&&!(commit.Error?.Contains("nothing to commit",StringComparison.OrdinalIgnoreCase)??false))return commit;var runtime=await runtimes.ResolveAsync(VcsType.Git,ct);if(!runtime.Available)return new(false,"apply",Error:runtime.Error);var merge=await processRunner.RunAsync(new(runtime.ExecutablePath!,["merge","--no-ff","--no-edit",run.Branch],run.WorkspacePath,TimeSpan.FromMinutes(5)),ct);return new(merge.ExitCode==0&&!merge.TimedOut,"apply",merge.StandardOutput,merge.ExitCode==0?null:merge.StandardError);
    }

    private async Task<WorkspaceActionResult> DiscardAsync(RunEntity run,CancellationToken ct)
    {
        if(run.WorkspaceStrategy is not (WorkspaceStrategy.GitWorktree or WorkspaceStrategy.SvnShadowGit))return new(false,"discard",Error:"Run workspace is not an isolated Git worktree.");var repository=run.WorkspaceStrategy==WorkspaceStrategy.GitWorktree?run.WorkspacePath!:Path.Combine(await GetRootAsync("workspace.shadow_git_root","Workspace:ShadowGitRoot","shadow-git",ct),run.ProjectId!);var runtime=await runtimes.ResolveAsync(VcsType.Git,ct);if(!runtime.Available)return new(false,"discard",Error:runtime.Error);var remove=await processRunner.RunAsync(new(runtime.ExecutablePath!,["worktree","remove","--force",run.ExecutionWorkspacePath!],repository,TimeSpan.FromMinutes(2)),ct);if(remove.ExitCode!=0)return new(false,"discard",Error:remove.StandardError);if(!string.IsNullOrWhiteSpace(run.Branch))await processRunner.RunAsync(new(runtime.ExecutablePath!,["branch","-D",run.Branch],repository,TimeSpan.FromSeconds(30)),ct);return new(true,"discard","Workspace discarded.");
    }

    private async Task<string> GetRootAsync(string settingKey,string configurationKey,string name,CancellationToken ct){var value=settings is null?null:await settings.GetAsync(settingKey,ct);if(string.IsNullOrWhiteSpace(value))value=configuration[configurationKey];return Path.GetFullPath(string.IsNullOrWhiteSpace(value)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".Wingman",name):value);}
}
