using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentService.Infrastructure.Skills;

public sealed class YamlSkillManifestLoader : ISkillManifestLoader
{
    public const string FileName = "wingman.yaml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<SkillManifest?> LoadAsync(
        string skillRoot,
        CancellationToken ct = default)
    {
        var path = Path.Combine(skillRoot, FileName);
        if (!File.Exists(path))
            return null;

        var yaml = await File.ReadAllTextAsync(path, ct);
        var manifest = Deserializer.Deserialize<SkillManifest>(yaml)
            ?? throw new InvalidDataException("wingman.yaml is empty.");
        Validate(manifest, skillRoot);
        return manifest;
    }

    private static void Validate(SkillManifest manifest, string skillRoot)
    {
        if (manifest.Version != 1)
            throw new InvalidDataException($"Unsupported wingman.yaml version: {manifest.Version}.");
        if (manifest.Runtime is null)
            throw new InvalidDataException("wingman.yaml must define runtime.");
        if (!TryParseRuntimeKind(manifest.Runtime.Type, out _))
            throw new InvalidDataException($"Unsupported runtime type: {manifest.Runtime.Type}.");
        if (manifest.Runtime.Entrypoints.Count == 0)
            throw new InvalidDataException("At least one runtime entrypoint is required.");

        foreach (var (name, entrypoint) in manifest.Runtime.Entrypoints)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(entrypoint.Path))
                throw new InvalidDataException("Entrypoint names and paths are required.");
            if (entrypoint.TimeoutSeconds is < 1 or > 3600)
                throw new InvalidDataException(
                    $"Entrypoint '{name}' timeout must be between 1 and 3600 seconds.");

            ResolveSkillPath(skillRoot, entrypoint.Path);
            if (!string.IsNullOrWhiteSpace(entrypoint.WorkingDirectory))
                ResolveSkillPath(skillRoot, entrypoint.WorkingDirectory);
            foreach (var (parameterName, parameter) in entrypoint.Parameters)
            {
                if (string.IsNullOrWhiteSpace(parameterName) || parameter.Type is not ("string" or "number" or "boolean"))
                    throw new InvalidDataException($"Entrypoint '{name}' has an invalid parameter schema.");
                if (parameter.Flag?.Any(char.IsWhiteSpace) == true)
                    throw new InvalidDataException($"Parameter '{parameterName}' flag cannot contain whitespace.");
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.Runtime.DependencyFile))
            ResolveSkillPath(skillRoot, manifest.Runtime.DependencyFile);
        if (!string.IsNullOrWhiteSpace(manifest.Runtime.LockFile))
            ResolveSkillPath(skillRoot, manifest.Runtime.LockFile);
    }

    public static bool TryParseRuntimeKind(string value, out SkillRuntimeKind kind) =>
        Enum.TryParse(value switch
        {
            "nodejs" => nameof(SkillRuntimeKind.Node),
            "powershell" or "pwsh" => nameof(SkillRuntimeKind.PowerShell),
            _ => value,
        }, true, out kind);

    public static string ResolveSkillPath(string skillRoot, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
            throw new InvalidDataException("Skill paths must be relative.");

        var root = Path.GetFullPath(skillRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Skill path escapes the skill directory.");
        return candidate;
    }
}
