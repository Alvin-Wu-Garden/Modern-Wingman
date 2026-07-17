using System.Text.Json;
using AgentService.Application.Contracts;

namespace AgentService.Infrastructure.Skills;

public sealed class PluginCatalog(IConfiguration configuration) : IPluginCatalog
{
    private readonly string root = Path.GetFullPath(configuration["Plugins:Root"] ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Wingman", "plugins"));

    public async Task<IReadOnlyList<WingmanPluginManifest>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return [];
        var result = new List<WingmanPluginManifest>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try { result.Add(await ValidateAsync(directory, ct)); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        return result.OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<WingmanPluginManifest> ValidateAsync(string pluginRoot, CancellationToken ct = default)
    {
        var fullRoot = Path.GetFullPath(pluginRoot);
        var manifestPath = Path.Combine(fullRoot, ".wingman-plugin", "plugin.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Plugin manifest .wingman-plugin/plugin.json was not found.");
        await using var stream = File.OpenRead(manifestPath);
        var document = await JsonSerializer.DeserializeAsync<ManifestDocument>(stream, JsonOptions, ct)
            ?? throw new InvalidDataException("Plugin manifest is empty.");
        if (document.SchemaVersion != 1) throw new InvalidDataException("Only plugin schemaVersion 1 is supported.");
        if (string.IsNullOrWhiteSpace(document.Id) || string.IsNullOrWhiteSpace(document.Name) || string.IsNullOrWhiteSpace(document.Version))
            throw new InvalidDataException("Plugin id, name, and version are required.");
        IReadOnlyList<string> Paths(IEnumerable<string>? values) =>
            (values ?? []).Select(value => ValidateRelativePath(fullRoot, value)).ToList();
        return new(document.Id, document.Name, document.Version, document.SchemaVersion,
            Paths(document.Skills), Paths(document.McpServers), Paths(document.Hooks), Paths(document.Assets), fullRoot);
    }

    private static string ValidateRelativePath(string rootPath, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Plugin entries must be non-empty relative paths.");
        var resolved = Path.GetFullPath(Path.Combine(rootPath, relative));
        var prefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Plugin path escapes its package: {relative}");
        return relative.Replace('\\', '/');
    }

    private sealed record ManifestDocument(
        string Id, string Name, string Version, int SchemaVersion,
        IReadOnlyList<string>? Skills, IReadOnlyList<string>? McpServers,
        IReadOnlyList<string>? Hooks, IReadOnlyList<string>? Assets);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
