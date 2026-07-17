using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Infrastructure.AgentFramework.Plugins;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

public sealed class MarketplacePluginService(
    IMarketplaceArtifactStore artifactStore,
    IMarketplacePluginStore pluginStore,
    IMarketplacePluginConfigurationStore configurationStore,
    IMcpToolCatalog mcpCatalog,
    IEnumerable<IPluginCapabilitySnapshotInvalidator> capabilityInvalidators,
    IEnumerable<IPluginRuntimeEnablementObserver> enablementObservers,
    PluginRuntimeManifestLoader runtimeManifestLoader,
    IMarketplaceActivityRecorder? activity = null) : IMarketplacePluginService
{
    public Task<IReadOnlyList<MarketplacePluginInstallation>> ListAsync(CancellationToken cancellationToken = default) => pluginStore.ListInstallationsAsync(cancellationToken);

    public async Task<MarketplacePluginPreview> PreviewAsync(string installationId, CancellationToken cancellationToken = default)
    {
        var installation = (await pluginStore.ListInstallationsAsync(cancellationToken)).SingleOrDefault(item => item.Id == installationId)
            ?? throw new KeyNotFoundException("找不到 Plugin installation。");
        var manifest = await PluginManifestReader.ReadAsync(installation.InstalledPath, cancellationToken);
        var runtime = runtimeManifestLoader.Load(new(installation.PluginId, installation.Version, manifest.Skills, manifest.McpServers, manifest.FunctionIds, manifest.HookIds, installation.InstalledPath));
        return new(installation.Id, installation.PluginId, installation.Version, manifest.Skills, manifest.McpServers, manifest.FunctionIds, manifest.HookIds,
            $"已驗證 {runtime.Functions.Count} 個 Function、{runtime.Hooks.Count} 個 Hook。Enable 後才會註冊能力；安裝/更新不執行 lifecycle script，Plugin 不允許 in-process binary extension 或 shell command。");
    }

    public async Task<MarketplacePluginInstallation> InstallAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await artifactStore.GetArtifactAsync(artifactId, cancellationToken) ?? throw new KeyNotFoundException("找不到 Plugin artifact。");
        if (artifact.Kind != MarketplaceArtifactKind.WingmanPlugin) throw new InvalidOperationException("只有 Wingman Plugin artifact 可以安裝到 Plugin Store。");
        var manifest = await PluginManifestReader.ReadAsync(artifact.SnapshotPath, cancellationToken);
        _ = runtimeManifestLoader.Load(new(manifest.Id, manifest.Version, manifest.Skills, manifest.McpServers, manifest.FunctionIds, manifest.HookIds, artifact.SnapshotPath));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Wingman", "plugins", "installed", manifest.Id, manifest.Version);
        CopyPlugin(artifact.SnapshotPath, root);
        var installation = new MarketplacePluginInstallation(Guid.NewGuid().ToString("N"), artifact.Id, manifest.Id, manifest.Version, Enabled: false, root, DateTimeOffset.UtcNow);
        await pluginStore.SaveInstallationAsync(installation, cancellationToken);
        InvalidateCapabilities();
        if (activity is not null)
            await activity.RecordAsync(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "plugin-install", "Completed", artifact.Id, null,
                $"plugin={installation.PluginId};version={installation.Version}", DateTimeOffset.UtcNow), cancellationToken);
        return installation;
    }

    public async Task SetEnabledAsync(string installationId, bool enabled, CancellationToken cancellationToken = default)
    {
        await pluginStore.SetEnabledAsync(installationId, enabled, cancellationToken);
        var installation = (await pluginStore.ListInstallationsAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == installationId);
        if (installation is not null)
            foreach (var observer in enablementObservers)
                observer.OnPluginEnablementChanged(installation.PluginId, enabled);
        InvalidateCapabilities();
        // Plugin MCP definitions are transient. Refreshing here both discovers newly enabled
        // servers and removes managed tools from a Plugin that was just disabled.
        await mcpCatalog.RefreshAsync(cancellationToken);
        if (activity is not null)
            await activity.RecordAsync(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "plugin-enable", enabled ? "Enabled" : "Disabled", installation?.ArtifactId, null,
                $"installation={installationId}", DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task<MarketplacePluginConfiguration> GetConfigurationAsync(string installationId, CancellationToken cancellationToken = default)
    {
        var installation = await GetInstallationAsync(installationId, cancellationToken);
        var fields = await GetConfigurationFieldsAsync(installation.InstalledPath, cancellationToken);
        var configured = await configurationStore.GetValuesAsync(installation.PluginId, cancellationToken);
        return new(installation.Id, installation.PluginId, fields.Select(field => new MarketplacePluginConfigurationField(
            field.Name,
            field.IsSecret,
            configured.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value))).ToList());
    }

    public async Task SaveConfigurationAsync(string installationId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        var installation = await GetInstallationAsync(installationId, cancellationToken);
        var allowed = await GetConfigurationFieldsAsync(installation.InstalledPath, cancellationToken);
        var names = allowed.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        var invalid = values.Keys.FirstOrDefault(key => !names.Contains(key));
        if (invalid is not null) throw new InvalidDataException($"Plugin 未宣告設定欄位：{invalid}。");
        await configurationStore.SaveValuesAsync(installation.PluginId, values, cancellationToken);
        if (installation.Enabled) await mcpCatalog.RefreshAsync(cancellationToken);
        if (activity is not null)
            await activity.RecordAsync(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "plugin-configure", "Completed", installation.ArtifactId, null,
                $"plugin={installation.PluginId};fields={string.Join(',', values.Keys.OrderBy(key => key, StringComparer.Ordinal))}", DateTimeOffset.UtcNow), cancellationToken);
    }

    private async Task<MarketplacePluginInstallation> GetInstallationAsync(string installationId, CancellationToken ct) =>
        (await pluginStore.ListInstallationsAsync(ct)).SingleOrDefault(item => item.Id == installationId)
        ?? throw new KeyNotFoundException("找不到 Plugin installation。");

    private static async Task<IReadOnlyList<PluginConfigurationField>> GetConfigurationFieldsAsync(string root, CancellationToken ct)
    {
        var manifest = await PluginManifestReader.ReadAsync(root, ct);
        var fields = new Dictionary<string, PluginConfigurationField>(StringComparer.Ordinal);
        foreach (var relative in manifest.McpServers)
        {
            var path = ResolvePluginPath(root, relative);
            if (path is null || !File.Exists(path)) continue;
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!document.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object) continue;
            foreach (var server in servers.EnumerateObject().Select(item => item.Value).Where(value => value.ValueKind == JsonValueKind.Object))
            {
                if (!server.TryGetProperty("env", out var environment) || environment.ValueKind != JsonValueKind.Object) continue;
                foreach (var item in environment.EnumerateObject())
                {
                    if (item.Value.ValueKind != JsonValueKind.String || !PluginMcpServerSource.IsPlaceholder(item.Value.GetString()!) || !IsEnvironmentName(item.Name)) continue;
                    fields[item.Name] = new(item.Name, PluginMcpServerSource.IsSecretName(item.Name));
                }
            }
        }
        return fields.Values.OrderBy(field => field.Name, StringComparer.Ordinal).ToList();
    }

    private static string? ResolvePluginPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
    private static bool IsEnvironmentName(string name) => name.Length > 0 && name.All(character => char.IsLetterOrDigit(character) || character == '_') && !char.IsDigit(name[0]);
    private sealed record PluginConfigurationField(string Name, bool IsSecret);

    private void InvalidateCapabilities()
    {
        foreach (var invalidator in capabilityInvalidators) invalidator.Invalidate();
    }

    private static void CopyPlugin(string source, string destination)
    {
        if (Directory.Exists(destination)) return;
        var parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(source, directory)));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { var target = Path.Combine(staging, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); }
            if (!Directory.Exists(destination)) Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}

public sealed class EnabledPluginCapabilitySource(IMarketplacePluginStore pluginStore) : IEnabledPluginCapabilitySource
{
    public async Task<IReadOnlyList<EnabledPluginCapabilities>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var installations = await pluginStore.ListInstallationsAsync(cancellationToken);
        var result = new List<EnabledPluginCapabilities>();
        foreach (var installation in installations.Where(item => item.Enabled))
        {
            try
            {
                var manifest = await PluginManifestReader.ReadAsync(installation.InstalledPath, cancellationToken);
                result.Add(new(installation.PluginId, installation.Version, manifest.Skills, manifest.McpServers, manifest.FunctionIds, manifest.HookIds, installation.InstalledPath));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // An invalid or manually removed package cannot register capabilities; installation remains visible for repair.
            }
        }
        return result;
    }
}

/// <summary>Runtime observes enablement so it can refuse new work and cancel managed processes on disable.</summary>
public interface IPluginRuntimeEnablementObserver
{
    void OnPluginEnablementChanged(string pluginId, bool enabled);
}

internal sealed record MarketplacePluginManifest(string Id, string Version, IReadOnlyList<string> Skills, IReadOnlyList<string> McpServers, IReadOnlyList<string> FunctionIds, IReadOnlyList<string> HookIds);

internal static class PluginManifestReader
{
    public static async Task<MarketplacePluginManifest> ReadAsync(string root, CancellationToken ct)
    {
        var path = Path.Combine(root, ".codex-plugin", "plugin.json");
        var wingmanPath = Path.Combine(root, "wingman.json");
        if (!File.Exists(path) || !File.Exists(wingmanPath)) throw new InvalidDataException("Plugin 缺少必要 manifest。");
        await using var stream = File.OpenRead(path); using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var element = document.RootElement;
        var id = Required(element, "name"); var version = Required(element, "version");
        using var wingman = JsonDocument.Parse(await File.ReadAllTextAsync(wingmanPath, ct));
        return new(id, version, Strings(element, "skills"), Strings(element, "mcpServers"), Strings(wingman.RootElement, "functions"), Strings(wingman.RootElement, "hooks"));
    }

    private static string Required(JsonElement element, string name) => element.TryGetProperty(name, out var property) && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! : throw new InvalidDataException($"Plugin manifest 缺少 {name}。");
    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return [];
        if (property.ValueKind == JsonValueKind.String) return [property.GetString()!];
        if (property.ValueKind != JsonValueKind.Array) return [];
        return property.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.TryGetProperty("id", out var id) ? id.GetString() : null).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList();
    }
}
