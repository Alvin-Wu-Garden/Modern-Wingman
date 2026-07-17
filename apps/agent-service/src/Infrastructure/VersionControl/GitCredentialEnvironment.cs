using AgentService.Domain.Models;

namespace AgentService.Infrastructure.VersionControl;

internal sealed class GitCredentialEnvironment : IDisposable
{
    private readonly string? _askPassPath;
    public IReadOnlyDictionary<string, string?> Variables { get; }

    public GitCredentialEnvironment(VcsConnectionProfile profile)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_SSL_NO_VERIFY"] = profile.SslVerificationEnabled ? null : "true",
        };
        if (profile.HasSecret)
        {
            _askPassPath = Path.Combine(
                Path.GetTempPath(),
                "wingman-git-askpass-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(_askPassPath, "@echo off\r\necho %WINGMAN_GIT_TOKEN%\r\n");
            variables["GIT_ASKPASS"] = _askPassPath;
            variables["WINGMAN_GIT_TOKEN"] = profile.SecretValue;
        }
        Variables = variables;
    }

    public string AddUsername(string repositoryUrl, string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return repositoryUrl;
        var builder = new UriBuilder(repositoryUrl) { UserName = username, Password = "" };
        return builder.Uri.AbsoluteUri;
    }

    public void Dispose()
    {
        if (_askPassPath is null)
            return;
        try { File.Delete(_askPassPath); } catch { }
    }
}
