namespace AgentService.Infrastructure.Providers;

/// <summary>
/// GitHub Copilot SDK 僅支援 fine-grained PAT（github_pat_）。
/// Classic PAT（ghp_）不會被 Copilot CLI 採用。
/// </summary>
public static class GitHubPatValidator
{
    public static bool IsFineGrainedPat(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        token.Trim().StartsWith("github_pat_", StringComparison.Ordinal);

    public static string? GetFormatError(string? token) => IsFineGrainedPat(token)
        ? null
        : "GitHub Copilot 僅支援 github_pat_ 開頭的 fine-grained PAT；ghp_ classic PAT 不支援。";
}
