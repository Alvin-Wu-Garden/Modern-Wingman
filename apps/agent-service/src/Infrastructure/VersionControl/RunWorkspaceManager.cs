using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.VersionControl;

public sealed class RunWorkspaceManager : IRunWorkspaceManager
{
    private readonly IGitClient _git;
    private readonly IConfiguration _configuration;
    private readonly IShadowGitService? _shadowGit;
    private readonly ISvnClient? _svn;
    private readonly IVcsStateRepository? _vcsState;
    private readonly IAgentSettingsStore? _settings;

    public RunWorkspaceManager(IGitClient git, IConfiguration configuration)
        : this(git, configuration, null, null, null, null)
    {
    }

    public RunWorkspaceManager(
        IGitClient git,
        IConfiguration configuration,
        IShadowGitService? shadowGit,
        ISvnClient? svn,
        IVcsStateRepository? vcsState,
        IAgentSettingsStore? settings = null)
    {
        _git = git;
        _configuration = configuration;
        _shadowGit = shadowGit;
        _svn = svn;
        _vcsState = vcsState;
        _settings = settings;
    }

    public WorkspaceStrategy ResolveStrategy(string workspacePath, WorkspaceStrategy requested)
    {
        if (requested != WorkspaceStrategy.Direct)
            return requested;

        var source = Path.GetFullPath(workspacePath);
        if (Directory.Exists(Path.Combine(source, ".svn")))
            return WorkspaceStrategy.SvnShadowGit;
        if (Directory.Exists(Path.Combine(source, ".git")) || File.Exists(Path.Combine(source, ".git")))
            return WorkspaceStrategy.GitWorktree;
        return WorkspaceStrategy.Snapshot;
    }

    public async Task<PreparedRunWorkspace> PrepareAsync(RunEntity run, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(run.ExecutionWorkspacePath) &&
            Directory.Exists(run.ExecutionWorkspacePath))
        {
            return new PreparedRunWorkspace(run.ExecutionWorkspacePath, run.Branch, run.BaseRevision);
        }

        if (string.IsNullOrWhiteSpace(run.WorkspacePath))
        {
            if (run.WorkspaceStrategy is WorkspaceStrategy.GitWorktree or WorkspaceStrategy.SvnShadowGit)
                throw new InvalidOperationException("Run workspace is required for an isolated workspace strategy.");
            return new PreparedRunWorkspace(null, null, null);
        }

        var source = Path.GetFullPath(run.WorkspacePath);
        if (run.Mode is AgentMode.Ask or AgentMode.Plan)
            return new PreparedRunWorkspace(source, null, null);

        return run.WorkspaceStrategy switch
        {
            WorkspaceStrategy.GitWorktree => await PrepareGitWorktreeAsync(run, source, ct),
            WorkspaceStrategy.SvnShadowGit => await PrepareSvnShadowGitAsync(run, source, ct),
            _ => new PreparedRunWorkspace(source, null, null),
        };
    }

    private async Task<PreparedRunWorkspace> PrepareGitWorktreeAsync(
        RunEntity run,
        string source,
        CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(source, ".git")) && !File.Exists(Path.Combine(source, ".git")))
            throw new InvalidOperationException("Git worktree strategy requires a Git repository.");

        var status = await _git.StatusAsync(source, ct);
        if (!status.Success)
            throw new InvalidOperationException(status.Error ?? "Unable to read Git repository status.");

        var destination = Path.Combine(await GetRootAsync("workspace.worktree_root", "Workspace:WorktreeRoot", "workspaces", ct), run.Id);
        var branch = CreateRunBranch(run.Id);
        var created = await _git.CreateWorktreeAsync(source, destination, branch, "HEAD", ct);
        if (!created.Success)
            throw new InvalidOperationException(created.Error ?? "Unable to create Git worktree.");

        if (run.IncludeUncommittedChanges)
            CarryUncommittedChanges(source, destination, status.Output, ct);

        var worktreeStatus = await _git.StatusAsync(destination, ct);
        var baseRevision = ParseGitRevision(worktreeStatus.Output);
        return new PreparedRunWorkspace(destination, branch, baseRevision);
    }

    private async Task<PreparedRunWorkspace> PrepareSvnShadowGitAsync(
        RunEntity run,
        string source,
        CancellationToken ct)
    {
        if (_shadowGit is null || _svn is null || _vcsState is null)
            throw new InvalidOperationException("SVN Shadow Git services are unavailable.");
        if (string.IsNullOrWhiteSpace(run.ProjectId))
            throw new InvalidOperationException("SVN Shadow Git requires a project binding.");
        if (!Directory.Exists(Path.Combine(source, ".svn")))
            throw new InvalidOperationException("SVN Shadow Git requires an SVN working copy.");

        var binding = await _vcsState.GetBindingAsync(run.ProjectId, ct);
        if (binding is null || binding.VcsType != VcsType.Svn)
            throw new InvalidOperationException("The project does not have an SVN binding.");
        if (string.IsNullOrWhiteSpace(binding.ConnectionProfileId) ||
            string.IsNullOrWhiteSpace(binding.RepositoryUrl))
        {
            throw new InvalidOperationException("The SVN binding is missing its profile or repository URL.");
        }

        var shadowRepository = Path.Combine(
            await GetRootAsync("workspace.shadow_git_root", "Workspace:ShadowGitRoot", "shadow-git", ct),
            run.ProjectId);
        if (!Directory.Exists(Path.Combine(shadowRepository, ".git")))
        {
            var revision = await _svn.GetRevisionAsync(
                binding.ConnectionProfileId,
                binding.RepositoryUrl,
                ct);
            if (!revision.Success)
                throw new InvalidOperationException(revision.Error ?? "Unable to read the SVN revision.");

            await _shadowGit.InitializeAsync(
                source,
                shadowRepository,
                binding.RepositoryUrl,
                revision.Output.Trim(),
                ct);
        }

        var destination = Path.Combine(await GetRootAsync("workspace.worktree_root", "Workspace:WorktreeRoot", "workspaces", ct), run.Id);
        var branch = CreateRunBranch(run.Id);
        var created = await _shadowGit.CreateRunWorktreeAsync(
            shadowRepository,
            destination,
            branch,
            ct);
        if (!created.Success)
            throw new InvalidOperationException(created.Error ?? "Unable to create the Shadow Git worktree.");

        var status = await _git.StatusAsync(destination, ct);
        return new PreparedRunWorkspace(destination, branch, ParseGitRevision(status.Output));
    }

    private async Task<string> GetRootAsync(
        string settingKey,
        string configurationKey,
        string directoryName,
        CancellationToken ct)
    {
        var configured = _settings is null ? null : await _settings.GetAsync(settingKey, ct);
        if (string.IsNullOrWhiteSpace(configured))
            configured = _configuration[configurationKey];
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".Wingman",
                directoryName)
            : configured;
        return Path.GetFullPath(root);
    }

    private static string CreateRunBranch(string runId) => $"wingman/{runId[..8]}";

    private static void CarryUncommittedChanges(
        string source,
        string destination,
        string porcelain,
        CancellationToken ct)
    {
        foreach (var change in ParseDirtyPaths(porcelain))
        {
            ct.ThrowIfCancellationRequested();
            if (change.OriginalPath is not null)
                DeleteDestinationPath(destination, change.OriginalPath);

            var sourcePath = Infrastructure.Tools.WorkspacePathGuard.Resolve(source, change.Path);
            var destinationPath = Infrastructure.Tools.WorkspacePathGuard.Resolve(destination, change.Path);
            if (!File.Exists(sourcePath))
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                continue;
            }
            if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException($"Git dirty file cannot be a link or junction: {change.Path}");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    internal static IReadOnlyList<(string Path, string? OriginalPath)> ParseDirtyPaths(string output)
    {
        var result = new List<(string Path, string? OriginalPath)>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.StartsWith("# ", StringComparison.Ordinal) || rawLine.StartsWith("! ", StringComparison.Ordinal))
                continue;
            if (rawLine.StartsWith("? ", StringComparison.Ordinal))
            {
                result.Add((UnquoteGitPath(rawLine[2..]), null));
                continue;
            }
            if (rawLine.StartsWith("1 ", StringComparison.Ordinal))
            {
                var fields = rawLine.Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 9) result.Add((UnquoteGitPath(fields[8]), null));
                continue;
            }
            if (rawLine.StartsWith("2 ", StringComparison.Ordinal))
            {
                var fields = rawLine.Split(' ', 10, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 10) continue;
                var names = fields[9].Split('\t', 2);
                result.Add((UnquoteGitPath(names[0]), names.Length == 2 ? UnquoteGitPath(names[1]) : null));
                continue;
            }
            if (rawLine.StartsWith("u ", StringComparison.Ordinal))
            {
                var fields = rawLine.Split(' ', 11, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 11) result.Add((UnquoteGitPath(fields[10]), null));
            }
        }
        return result.Distinct().ToList();
    }

    private static string UnquoteGitPath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return trimmed;
    }

    private static void DeleteDestinationPath(string destination, string relativePath)
    {
        var path = Infrastructure.Tools.WorkspacePathGuard.Resolve(destination, relativePath);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string? ParseGitRevision(string output) => output
        .Split('\n')
        .FirstOrDefault(line => line.StartsWith("# branch.oid ", StringComparison.Ordinal))?[13..]
        .Trim();
}
