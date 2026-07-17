using System.Text.Json;
using System.Security.Cryptography;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.VersionControl;

public sealed class ShadowGitService(
    IVcsRuntimeResolver runtimeResolver,
    IProcessRunner processRunner,
    IGitClient gitClient,
    ISvnClient? svnClient=null) : IShadowGitService
{
    public async Task<ShadowGitBaseline> InitializeAsync(
        string svnWorkingCopyPath,
        string shadowRepositoryPath,
        string svnUrl,
        string svnRevision,
        CancellationToken ct = default)
    {
        var source = Path.GetFullPath(svnWorkingCopyPath);
        var target = Path.GetFullPath(shadowRepositoryPath);
        if (!Directory.Exists(Path.Combine(source, ".svn")))
            throw new InvalidOperationException("The source is not an SVN working copy.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidOperationException("Shadow Git destination is not empty.");

        Directory.CreateDirectory(target);
        CopyTree(source, target, ct);
        await File.WriteAllTextAsync(
            Path.Combine(target, ".wingman-svn-baseline.json"),
            JsonSerializer.Serialize(new BaselineMetadata { SvnUrl=svnUrl,SvnRevision=svnRevision,SvnWorkingCopyPath=source,CapturedAt=DateTimeOffset.UtcNow }), ct);
        await File.WriteAllTextAsync(Path.Combine(target, ".gitignore"), ".svn/\n", ct);

        var runtime = await RequireGitAsync(ct);
        await RequireSuccessAsync(runtime, ["init", "--initial-branch=baseline"], target, ct);
        await RequireSuccessAsync(runtime, ["add", "--all"], target, ct);
        await RequireSuccessAsync(runtime,
            ["-c", "user.name=Modern Wingman", "-c", "user.email=wingman@wingman.local", "commit", "-m", $"SVN baseline r{svnRevision}"],
            target, ct);
        var revision = await RequireSuccessAsync(runtime, ["rev-parse", "HEAD"], target, ct);
        return new ShadowGitBaseline(target, source, svnUrl, svnRevision, revision.StandardOutput.Trim());
    }

    public Task<GitCommandResult> CreateRunWorktreeAsync(
        string shadowRepositoryPath,
        string worktreePath,
        string branchName,
        CancellationToken ct = default) =>
        gitClient.CreateWorktreeAsync(shadowRepositoryPath, worktreePath, branchName, "baseline", ct);

    public async Task<ShadowApplyResult> ApplyToSvnAsync(string profileId,string shadowRepositoryPath,string runWorktreePath,CancellationToken ct=default)
    {
        if(svnClient is null)throw new InvalidOperationException("SVN client is unavailable.");var metadataPath=Path.Combine(shadowRepositoryPath,".wingman-svn-baseline.json");var metadata=JsonSerializer.Deserialize<BaselineMetadata>(await File.ReadAllTextAsync(metadataPath,ct))??throw new InvalidDataException("Invalid Shadow Git baseline metadata.");var remote=await svnClient.GetRevisionAsync(profileId,metadata.SvnUrl,ct);if(!remote.Success)return new(false,false,[],[],[],remote.Error);var remoteRevision=remote.Output.Trim();if(remoteRevision!=metadata.SvnRevision){var merged=await UpdateAndMergeRemoteAsync(profileId,shadowRepositoryPath,runWorktreePath,metadataPath,metadata,remoteRevision,ct);if(!merged.Success)return merged;}
        var baseline=Files(shadowRepositoryPath);var current=Files(runWorktreePath);var added=current.Keys.Except(baseline.Keys,StringComparer.OrdinalIgnoreCase).ToList();var deleted=baseline.Keys.Except(current.Keys,StringComparer.OrdinalIgnoreCase).ToList();var modified=current.Keys.Intersect(baseline.Keys,StringComparer.OrdinalIgnoreCase).Where(path=>current[path]!=baseline[path]).ToList();var renamed=new List<string>();foreach(var source in deleted.ToList()){var destination=added.FirstOrDefault(path=>current[path]==baseline[source]);if(destination is null)continue;var move=await svnClient.MoveAsync(metadata.SvnWorkingCopyPath,source,destination,ct);if(!move.Success)return new(false,false,added,modified,deleted,move.Error,renamed);deleted.Remove(source);added.Remove(destination);renamed.Add($"{source} -> {destination}");}
        foreach(var path in modified){var target=Path.Combine(metadata.SvnWorkingCopyPath,path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(Path.Combine(runWorktreePath,path),target,true);}foreach(var path in added){var target=Path.Combine(metadata.SvnWorkingCopyPath,path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(Path.Combine(runWorktreePath,path),target,true);var result=await svnClient.AddAsync(metadata.SvnWorkingCopyPath,path,ct);if(!result.Success)return new(false,false,added,modified,deleted,result.Error,renamed);}foreach(var directory in EmptyDirectories(runWorktreePath).Except(EmptyDirectories(shadowRepositoryPath),StringComparer.OrdinalIgnoreCase)){var target=Path.Combine(metadata.SvnWorkingCopyPath,directory);Directory.CreateDirectory(target);var result=await svnClient.AddAsync(metadata.SvnWorkingCopyPath,directory,ct);if(!result.Success&&result.Error?.Contains("already under version control",StringComparison.OrdinalIgnoreCase)!=true)return new(false,false,added,modified,deleted,result.Error,renamed);}foreach(var path in deleted){var result=await svnClient.DeleteAsync(metadata.SvnWorkingCopyPath,path,ct);if(!result.Success)return new(false,false,added,modified,deleted,result.Error,renamed);}return new(true,false,added,modified,deleted,Renamed:renamed);
    }

    private async Task<ShadowApplyResult> UpdateAndMergeRemoteAsync(
        string profileId,
        string shadowRepositoryPath,
        string runWorktreePath,
        string metadataPath,
        BaselineMetadata metadata,
        string remoteRevision,
        CancellationToken ct)
    {
        var runtime = await RequireGitAsync(ct);
        var stageRun = await processRunner.RunAsync(new ProcessInvocation(
            runtime.ExecutablePath!, ["add", "--all"], runWorktreePath, TimeSpan.FromMinutes(2)), ct);
        if (stageRun.ExitCode != 0)
            return new(false, true, [], [], [], stageRun.StandardError);
        var commitRun = await processRunner.RunAsync(new ProcessInvocation(
            runtime.ExecutablePath!,
            ["-c", "user.name=Modern Wingman", "-c", "user.email=wingman@wingman.local", "commit", "-m", "Wingman run changes before SVN update"],
            runWorktreePath,
            TimeSpan.FromMinutes(2)), ct);
        if (commitRun.ExitCode != 0 &&
            !commitRun.StandardOutput.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) &&
            !commitRun.StandardError.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, true, [], [], [], commitRun.StandardError);
        }

        var update = await svnClient!.UpdateAsync(profileId, metadata.SvnWorkingCopyPath, ct);
        if (!update.Success)
            return new(false, true, [], [], [], update.Error ?? "SVN update failed.");

        MirrorWorkingCopy(metadata.SvnWorkingCopyPath, shadowRepositoryPath, ct);
        metadata.SvnRevision = remoteRevision;
        metadata.CapturedAt = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata), ct);

        var add = await processRunner.RunAsync(new ProcessInvocation(
            runtime.ExecutablePath!, ["add", "--all"], shadowRepositoryPath, TimeSpan.FromMinutes(2)), ct);
        if (add.ExitCode != 0)
            return new(false, true, [], [], [], add.StandardError);
        var commit = await processRunner.RunAsync(new ProcessInvocation(
            runtime.ExecutablePath!,
            ["-c", "user.name=Modern Wingman", "-c", "user.email=wingman@wingman.local", "commit", "-m", $"SVN remote update r{remoteRevision}"],
            shadowRepositoryPath,
            TimeSpan.FromMinutes(2)), ct);
        if (commit.ExitCode != 0 &&
            !commit.StandardOutput.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) &&
            !commit.StandardError.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, true, [], [], [], commit.StandardError);
        }

        var merge = await processRunner.RunAsync(new ProcessInvocation(
            runtime.ExecutablePath!, ["merge", "--no-edit", "baseline"], runWorktreePath, TimeSpan.FromMinutes(5)), ct);
        if (merge.ExitCode != 0)
        {
            var conflicts = await processRunner.RunAsync(new ProcessInvocation(
                runtime.ExecutablePath!, ["diff", "--name-only", "--diff-filter=U"], runWorktreePath, TimeSpan.FromSeconds(30)), ct);
            var names = conflicts.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return new(
                false,
                true,
                [],
                [],
                [],
                $"SVN r{remoteRevision} was merged into the run workspace and produced conflicts: {string.Join(", ", names)}");
        }
        return new(true, false, [], [], []);
    }

    private static void MirrorWorkingCopy(string source, string target, CancellationToken ct)
    {
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains(".svn", StringComparer.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(source, path), StringComparer.OrdinalIgnoreCase);
        foreach (var targetFile in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(target, targetFile);
            if (relative.Split(Path.DirectorySeparatorChar).Contains(".git", StringComparer.OrdinalIgnoreCase) ||
                Path.GetFileName(relative) is ".wingman-svn-baseline.json" or ".gitignore")
                continue;
            if (!sourceFiles.ContainsKey(relative))
                File.Delete(targetFile);
        }
        foreach (var (relative, sourceFile) in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination, overwrite: true);
        }
    }

    private async Task<VcsRuntimeInfo> RequireGitAsync(CancellationToken ct)
    {
        var runtime = await runtimeResolver.ResolveAsync(VcsType.Git, ct);
        if (!runtime.Available || runtime.ExecutablePath is null)
            throw new InvalidOperationException(runtime.Error ?? "Git runtime is unavailable.");
        return runtime;
    }

    private async Task<ProcessExecutionResult> RequireSuccessAsync(
        VcsRuntimeInfo runtime, IReadOnlyList<string> args, string cwd, CancellationToken ct)
    {
        var result = await processRunner.RunAsync(
            new ProcessInvocation(runtime.ExecutablePath!, args, cwd, TimeSpan.FromMinutes(2)), ct);
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidOperationException(result.StandardError);
        return result;
    }

    private static void CopyTree(string source, string target, CancellationToken ct)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            if (relative.Split(Path.DirectorySeparatorChar).Contains(".svn", StringComparer.OrdinalIgnoreCase))
                continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Shadow Git source cannot contain links or junctions.");
            Directory.CreateDirectory(Path.Combine(target, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Contains(".svn", StringComparer.OrdinalIgnoreCase))
                continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }
    private static Dictionary<string,string> Files(string root)=>Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(path=>!path.Split(Path.DirectorySeparatorChar).Any(segment=>segment is ".git" or ".svn")&&Path.GetFileName(path)!=".wingman-svn-baseline.json"&&Path.GetFileName(path)!=".gitignore").ToDictionary(path=>Path.GetRelativePath(root,path),HashFile,StringComparer.OrdinalIgnoreCase);
    private static string HashFile(string path){var bytes=File.ReadAllBytes(path);if(!bytes.Contains((byte)0)){try{var text=System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n","\n");bytes=System.Text.Encoding.UTF8.GetBytes(text);}catch{}}return Convert.ToHexString(SHA256.HashData(bytes));}
    private static IReadOnlyList<string> EmptyDirectories(string root)=>Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories).Where(path=>!path.Split(Path.DirectorySeparatorChar).Any(segment=>segment is ".git" or ".svn")&&!Directory.EnumerateFileSystemEntries(path).Any()).Select(path=>Path.GetRelativePath(root,path)).ToList();
    private sealed class BaselineMetadata{public string SvnUrl{get;set;}="";public string SvnRevision{get;set;}="";public string SvnWorkingCopyPath{get;set;}="";public DateTimeOffset CapturedAt{get;set;}}
}
