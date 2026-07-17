using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Marketplace;
using Wingman.Marketplace.Contracts;

namespace AgentService.Infrastructure.AgentFramework.Plugins;

/// <summary>
/// Adapts enabled plugin .mcp.json files into transient MCP definitions. It never
/// writes plugin entries to the normal MCP table and only supplies them when the
/// existing MCP catalog is refreshed for actual tool discovery.
/// </summary>
public sealed class PluginMcpServerSource(
    IEnabledPluginCapabilitySource capabilities,
    IMarketplacePluginConfigurationStore configurationStore,
    IRuntimeResolver runtimeResolver) : IPluginMcpServerSource
{
    public async Task<IReadOnlyList<McpServerDefinition>> ListEnabledAsync(CancellationToken ct = default)
    {
        var result = new List<McpServerDefinition>();
        foreach (var plugin in await capabilities.GetSnapshotAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(plugin.InstalledPath)) continue;
            var root = Path.GetFullPath(plugin.InstalledPath);
            var configuration = await configurationStore.GetValuesAsync(plugin.PluginId, ct);
            foreach (var relative in plugin.McpPaths)
            {
                var path = ResolvePath(root, relative);
                if (path is null || !File.Exists(path)) continue;
                try { result.AddRange(await ReadFileAsync(plugin.PluginId, root, path, configuration, ct)); }
                catch (Exception) when (!ct.IsCancellationRequested) { /* one invalid component must not disable other plugin capabilities */ }
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<McpServerDefinition>> ReadFileAsync(
        string pluginId,
        string pluginRoot,
        string path,
        IReadOnlyDictionary<string, string> configuration,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object) return [];
        var result = new List<McpServerDefinition>();
        foreach (var server in servers.EnumerateObject())
        {
            if (server.Value.ValueKind != JsonValueKind.Object) continue;
            var value = server.Value;
            var command = String(value, "command");
            var url = String(value, "url");
            var hasRuntime = value.TryGetProperty("runtime", out _);
            if (hasRuntime && !string.IsNullOrWhiteSpace(command)) continue;
            var transport = hasRuntime || command is not null ? McpTransport.Stdio : String(value, "type")?.Equals("sse", StringComparison.OrdinalIgnoreCase) == true ? McpTransport.Sse : McpTransport.Http;
            if (transport == McpTransport.Stdio && string.IsNullOrWhiteSpace(command) && !hasRuntime) continue;
            if (transport != McpTransport.Stdio)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            }
            if (!TryBuildEnvironment(value, configuration, out var environment)) continue;
            var arguments = Strings(value, "args");
            if (hasRuntime)
            {
                var runtime = await ResolveRuntimeAsync(value, pluginRoot, ct);
                if (runtime is null) continue;
                command = runtime.Executable;
                arguments = (runtime.PrefixArguments ?? []).Concat([runtime.Entrypoint]).Concat(arguments).ToList();
            }
            var name = $"plugin:{pluginId}:{server.Name}";
            result.Add(new(StableNegativeId(name), name, transport, command, arguments, url, environment, true));
        }
        return result;
    }

    private static string? ResolvePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static IReadOnlyList<string> Strings(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList() : [];
    private async Task<ResolvedPluginMcpRuntime?> ResolveRuntimeAsync(JsonElement server, string pluginRoot, CancellationToken ct)
    {
        if (!server.TryGetProperty("runtime", out var definition) || definition.ValueKind != JsonValueKind.Object) return null;
        var kind = String(definition, "kind")?.Trim().ToLowerInvariant() switch
        {
            "python" => SkillRuntimeKind.Python,
            "node" or "nodejs" => SkillRuntimeKind.Node,
            "powershell" or "pwsh" => SkillRuntimeKind.PowerShell,
            _ => (SkillRuntimeKind?)null,
        };
        var entrypoint = String(server, "entrypoint");
        if (kind is null || string.IsNullOrWhiteSpace(entrypoint)) return null;
        var resolvedEntrypoint = ResolvePath(pluginRoot, entrypoint);
        if (resolvedEntrypoint is null || !File.Exists(resolvedEntrypoint) || !HasMatchingExtension(kind.Value, resolvedEntrypoint)) return null;
        var runtime = await runtimeResolver.ResolveAsync(new(kind.Value, String(definition, "version"), pluginRoot, pluginRoot), ct);
        return runtime is null ? null : new(runtime.ExecutablePath, runtime.PrefixArguments, resolvedEntrypoint);
    }
    private static bool TryBuildEnvironment(JsonElement value, IReadOnlyDictionary<string, string> configuration, out IReadOnlyDictionary<string, string> environment)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!value.TryGetProperty("env", out var env) || env.ValueKind != JsonValueKind.Object) { environment = result; return true; }
        foreach (var item in env.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.String || !IsName(item.Name)) { environment = result; return false; }
            var configuredValue = item.Value.GetString()!;
            if (IsPlaceholder(configuredValue))
            {
                if (!configuration.TryGetValue(item.Name, out configuredValue) || string.IsNullOrWhiteSpace(configuredValue)) { environment = result; return false; }
            }
            result[item.Name] = configuredValue;
        }
        environment = result;
        return true;
    }
    internal static bool IsPlaceholder(string value) => value.StartsWith("REPLACE_WITH_YOUR_", StringComparison.Ordinal);
    internal static bool IsSecretName(string name) => name.Contains("key", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase) || name.Contains("pat", StringComparison.OrdinalIgnoreCase);
    private static bool HasMatchingExtension(SkillRuntimeKind kind, string path) => kind switch
    {
        SkillRuntimeKind.Python => Path.GetExtension(path).Equals(".py", StringComparison.OrdinalIgnoreCase),
        SkillRuntimeKind.Node => new[] { ".js", ".cjs", ".mjs" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase),
        SkillRuntimeKind.PowerShell => Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
    private static bool IsName(string name) => name.Length > 0 && name.All(item => char.IsLetterOrDigit(item) || item == '_') && !char.IsDigit(name[0]);
    private static long StableNegativeId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var number = BitConverter.ToInt64(hash, 0) & long.MaxValue;
        return -Math.Max(1, number);
    }
    private sealed record ResolvedPluginMcpRuntime(string Executable, IReadOnlyList<string>? PrefixArguments, string Entrypoint);
}
