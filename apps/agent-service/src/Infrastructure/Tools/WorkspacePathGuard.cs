namespace AgentService.Infrastructure.Tools;

public static class WorkspacePathGuard
{
    private static readonly HashSet<string> SensitiveFileNames = new(
        [
            ".git-credentials", ".npmrc", ".pypirc", ".netrc",
            "id_rsa", "id_ed25519", "credentials.json", "login data",
            "cookies", "web data",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string workspacePath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("Workspace path is required.", nameof(workspacePath));
        if (string.IsNullOrWhiteSpace(candidatePath))
            throw new ArgumentException("Target path is required.", nameof(candidatePath));

        var root = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.IsPathFullyQualified(candidatePath)
            ? Path.GetFullPath(candidatePath)
            : Path.GetFullPath(Path.Combine(root, candidatePath));

        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            return candidate;

        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The target path is outside the active workspace.");

        RejectEscapingReparsePoint(root, candidate);
        return candidate;
    }

    public static string ResolveReadable(string workspacePath, string candidatePath)
    {
        var resolved = Resolve(workspacePath, candidatePath);
        if (SensitiveFileNames.Contains(Path.GetFileName(resolved)))
            throw new UnauthorizedAccessException("Reading credential or browser secret files is not allowed.");
        return resolved;
    }

    private static void RejectEscapingReparsePoint(string root, string candidate)
    {
        var current = new DirectoryInfo(root);
        var relative = Path.GetRelativePath(root, candidate);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, segment));
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;

            var target = current.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
                throw new UnauthorizedAccessException("Unable to resolve workspace reparse point.");
            var targetPath = Path.GetFullPath(target.FullName);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!targetPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetPath, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    "The target path escapes the workspace through a link or junction.");
            }
        }
    }
}
