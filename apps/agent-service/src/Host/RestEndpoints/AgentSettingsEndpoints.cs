using AgentService.Application.Contracts;

namespace AgentService.Host.RestEndpoints;

public static class AgentSettingsEndpoints
{
    private const string WorkspaceRoot = "workspace.root";
    private const string WorktreeRoot = "workspace.worktree_root";
    private const string ShadowGitRoot = "workspace.shadow_git_root";

    public sealed record WorkspaceSettingsRequest(
        string WorkspaceRoot,
        string WorktreeRoot,
        string ShadowGitRoot);

    public static IEndpointRouteBuilder MapAgentSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/agent");
        group.MapGet("/workspace", GetWorkspaceSettings);
        group.MapPut("/workspace", SaveWorkspaceSettings);
        return app;
    }

    private static async Task<IResult> GetWorkspaceSettings(
        IAgentSettingsStore store,
        CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var values = await store.GetAllAsync(ct);
        return Results.Ok(new WorkspaceSettingsRequest(
            Get(values, WorkspaceRoot, Path.Combine(home, ".Wingman", "projects")),
            Get(values, WorktreeRoot, Path.Combine(home, ".Wingman", "workspaces")),
            Get(values, ShadowGitRoot, Path.Combine(home, ".Wingman", "shadow-git"))));
    }

    private static async Task<IResult> SaveWorkspaceSettings(
        WorkspaceSettingsRequest request,
        IAgentSettingsStore store,
        CancellationToken ct)
    {
        string workspace;
        string worktree;
        string shadow;
        try
        {
            workspace = ValidatePath(request.WorkspaceRoot, nameof(request.WorkspaceRoot));
            worktree = ValidatePath(request.WorktreeRoot, nameof(request.WorktreeRoot));
            shadow = ValidatePath(request.ShadowGitRoot, nameof(request.ShadowGitRoot));
        }
        catch (ArgumentException error)
        {
            return Results.BadRequest(new { error = error.Message });
        }
        await store.SetAsync(WorkspaceRoot, workspace, ct);
        await store.SetAsync(WorktreeRoot, worktree, ct);
        await store.SetAsync(ShadowGitRoot, shadow, ct);
        return Results.Ok(new WorkspaceSettingsRequest(workspace, worktree, shadow));
    }

    private static string ValidatePath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException($"{name} must be an absolute path.");
        return Path.GetFullPath(value);
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
}
