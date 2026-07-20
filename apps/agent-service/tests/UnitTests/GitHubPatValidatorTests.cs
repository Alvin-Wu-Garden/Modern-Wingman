using AgentService.Infrastructure.Providers;

namespace AgentService.UnitTests;

public sealed class GitHubPatValidatorTests
{
    [Theory]
    [InlineData("github_pat_11AA22BB33CC44DD55EE")]
    [InlineData("  github_pat_11AA22BB33CC44DD55EE  ")]
    public void IsFineGrainedPat_AcceptsGithubPatPrefix(string token)
    {
        Assert.True(GitHubPatValidator.IsFineGrainedPat(token));
        Assert.Null(GitHubPatValidator.GetFormatError(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ghp_classicPersonalAccessToken")]
    [InlineData("gho_oauthToken")]
    [InlineData("not-a-github-token")]
    public void IsFineGrainedPat_RejectsUnsupportedTokens(string? token)
    {
        Assert.False(GitHubPatValidator.IsFineGrainedPat(token));
        Assert.Contains("github_pat_", GitHubPatValidator.GetFormatError(token));
    }
}
