using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Skills;

public sealed partial class LocalRuntimeResolver(
    IProcessRunner processRunner,
    ILogger<LocalRuntimeResolver> logger) : IRuntimeResolver
{
    public async Task<ResolvedRuntime?> ResolveAsync(
        RuntimeResolutionRequest request,
        CancellationToken ct = default)
    {
        foreach (var candidate in EnumerateCandidates(request).DistinctBy(x => (x.Path,string.Join(" ",x.PrefixArguments))))
        {
            var version = await TryGetVersionAsync(request.Kind, candidate, request.WorkspacePath, ct);
            if (version is null ||
                !RuntimeVersionConstraint.IsSatisfied(version, request.VersionConstraint))
            {
                continue;
            }

            logger.LogInformation(
                "Resolved {RuntimeKind} {Version} from {Source}: {Path}",
                request.Kind,
                version,
                candidate.Source,
                candidate.Path);
            return new ResolvedRuntime(
                request.Kind,
                candidate.Path,
                version,
                candidate.Source,
                candidate.PrefixArguments);
        }

        return null;
    }

    private static IEnumerable<RuntimeCandidate> EnumerateCandidates(
        RuntimeResolutionRequest request)
    {
        var managedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".Wingman",
            "runtimes");
        var candidates = request.Kind switch
        {
            SkillRuntimeKind.Python => new[]
            {
                (Path.Combine(request.SkillRoot, ".wingman-runtime", "Scripts", "python.exe"), "skill-managed"),
                (Path.Combine(request.SkillRoot, ".venv", "Scripts", "python.exe"), "skill"),
                (Path.Combine(request.WorkspacePath, ".venv", "Scripts", "python.exe"), "project"),
            },
            SkillRuntimeKind.Node => new[]
            {
                (Path.Combine(request.SkillRoot, "node.exe"), "skill"),
                (Path.Combine(request.WorkspacePath, "node.exe"), "project"),
            },
            _ => Array.Empty<(string, string)>(),
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate.Item1))
                yield return new(candidate.Item1, candidate.Item2, []);

        var runtimeFolder = request.Kind.ToString().ToLowerInvariant();
        var kindRoot = Path.Combine(managedRoot, runtimeFolder);
        if (Directory.Exists(kindRoot))
        {
            var executable = request.Kind switch
            {
                SkillRuntimeKind.Python => "python.exe",
                SkillRuntimeKind.Node => "node.exe",
                _ => "pwsh.exe",
            };
            foreach (var path in Directory.EnumerateFiles(kindRoot, executable, SearchOption.AllDirectories))
                yield return new(path, "wingman", []);
        }

        var bundledKindRoot = Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "runtimes",
            runtimeFolder);
        if (Directory.Exists(bundledKindRoot))
        {
            var executable = request.Kind switch
            {
                SkillRuntimeKind.Python => "python.exe",
                SkillRuntimeKind.Node => "node.exe",
                _ => "pwsh.exe",
            };
            foreach (var path in Directory.EnumerateFiles(
                         bundledKindRoot,
                         executable,
                         SearchOption.AllDirectories))
            {
                yield return new(path, "bundled", []);
            }
        }

        var commandName = request.Kind switch
        {
            SkillRuntimeKind.Python => "python.exe",
            SkillRuntimeKind.Node => "node.exe",
            _ => "pwsh.exe",
        };
        var pathCommand = FindOnPath(commandName);
        if (pathCommand is not null)
            yield return new(pathCommand, "system", []);
        if (request.Kind == SkillRuntimeKind.Python)
        {
            var launcher=FindOnPath("py.exe");
            if(launcher is not null)yield return new(launcher,"py-launcher",["-3"]);
        }
        if (request.Kind == SkillRuntimeKind.PowerShell)
        {
            var windowsPowerShell = FindOnPath("powershell.exe");
            if (windowsPowerShell is not null)
                yield return new(windowsPowerShell, "system", []);
        }
    }

    private async Task<Version?> TryGetVersionAsync(
        SkillRuntimeKind kind,
        RuntimeCandidate candidate,
        string workingDirectory,
        CancellationToken ct)
    {
        try
        {
            var versionArguments = kind switch
            {
                SkillRuntimeKind.PowerShell =>
                    new[] { "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()" },
                _ => new[] { "--version" },
            };
            var arguments=candidate.PrefixArguments.Concat(versionArguments).ToArray();
            var result = await processRunner.RunAsync(
                new ProcessInvocation(
                    candidate.Path,
                    arguments,
                    workingDirectory,
                    TimeSpan.FromSeconds(10),
                    MaxOutputCharacters: 4096),
                ct);
            if (result.ExitCode != 0)
                return null;
            var text = result.StandardOutput + " " + result.StandardError;
            var match = VersionPattern().Match(text);
            return match.Success
                ? RuntimeVersionConstraint.ParseVersion(match.Value)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Runtime candidate failed: {Path}", candidate.Path);
            return null;
        }
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }

    [GeneratedRegex(@"\d+(?:\.\d+){1,3}")]
    private static partial Regex VersionPattern();

    private sealed record RuntimeCandidate(string Path,string Source,IReadOnlyList<string> PrefixArguments);
}
