namespace AgentService.Domain.Models;

public enum SkillRuntimeKind
{
    Python,
    Node,
    PowerShell,
}

public sealed class SkillManifest
{
    public int Version { get; set; } = 1;
    public SkillRuntimeManifest? Runtime { get; set; }
}

public sealed class SkillRuntimeManifest
{
    public string Type { get; set; } = "";
    public string? Version { get; set; }
    public string? DependencyFile { get; set; }
    public string? PackageManager { get; set; }
    public string? LockFile { get; set; }
    public bool InstallNetwork { get; set; }
    public Dictionary<string, SkillEntrypointManifest> Entrypoints { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SkillEntrypointManifest
{
    public string Path { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public bool Network { get; set; }
    public bool RequiresApproval { get; set; }
    public List<string> RequiredEnvironment { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, SkillParameterManifest> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SkillParameterManifest
{
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Flag { get; set; }
    public List<string> AllowedValues { get; set; } = [];
}

public sealed record RuntimeResolutionRequest(
    SkillRuntimeKind Kind,
    string? VersionConstraint,
    string SkillRoot,
    string WorkspacePath);

public sealed record ResolvedRuntime(
    SkillRuntimeKind Kind,
    string ExecutablePath,
    Version Version,
    string Source,
    IReadOnlyList<string>? PrefixArguments = null);
