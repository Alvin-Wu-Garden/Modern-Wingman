using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.VersionControl;

public sealed class ProtectedRefMatcher(IVcsStateRepository repository) : IProtectedRefMatcher
{
    private static readonly string[] GitDefaults = ["main", "master", "develop", "release/*"];
    private static readonly string[] SvnDefaults = ["trunk", "tags/*"];

    public async Task<bool> IsProtectedAsync(
        VcsType type, string reference, string? projectId = null, CancellationToken ct = default)
    {
        var configured = await repository.ListProtectedRefsAsync(type, projectId, ct);
        var patterns = configured.Count > 0
            ? configured.Select(x => x.Pattern)
            : type == VcsType.Git ? GitDefaults : SvnDefaults;
        var normalized = reference.Replace('\\', '/').Trim('/');
        return patterns.Any(pattern => GlobMatches(pattern, normalized));
    }

    internal static bool GlobMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern.Trim('/')).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
