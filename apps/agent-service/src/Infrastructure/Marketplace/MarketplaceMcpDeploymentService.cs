using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>
/// Applies a validated .mcp.json definition to a target JSON config. This service is deliberately
/// file-only: it does not launch, connect to, or health-check the configured MCP server.
/// </summary>
public sealed class MarketplaceMcpDeploymentService(
    IMarketplaceArtifactStore artifactStore,
    IMarketplaceDeploymentStore deploymentStore,
    IMarketplaceInstallabilityStore installabilityStore,
    IEnumerable<IAgentTargetAdapter> adapters,
    IMarketplaceActivityRecorder? activity = null) : IMarketplaceMcpDeploymentService
{
    private const string SecretPlaceholder = "REPLACE_WITH_YOUR_API_KEY";
    private readonly IReadOnlyDictionary<string, IAgentTargetAdapter> _adapters = adapters.ToDictionary(adapter => adapter.Descriptor.Id, StringComparer.OrdinalIgnoreCase);

    public async Task<MarketplaceDeploymentPlan> PreviewAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) throw new ArgumentException("至少選擇一個配置目標。", nameof(requests));
        var items = new List<MarketplaceInstallabilityResult>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? configPath = null;
            try
            {
                var artifact = await artifactStore.GetArtifactAsync(request.ArtifactId, cancellationToken) ?? throw new KeyNotFoundException("找不到 artifact。");
                if (artifact.Kind != MarketplaceArtifactKind.McpServer || artifact.Status != MarketplaceDiscoveryStatus.Resolved)
                    throw new InvalidOperationException("只有已解析且可驗證的 MCP Definition 可以一鍵配置。");
                if (!_adapters.TryGetValue(request.TargetId, out var adapter)) throw new InvalidOperationException("不支援的 Agent Target。");
                if (!adapter.Descriptor.SupportsMcp) throw new InvalidOperationException($"{adapter.Descriptor.DisplayName} 尚未支援 MCP 設定檔部署。");
                configPath = adapter.ResolveMcpConfigPath(request.Scope, request.ProjectPath);
                var definitions = await ReadDefinitionAsync(artifact.SnapshotPath, cancellationToken);
                var deployments = await deploymentStore.ListDeploymentsAsync(artifact.Id, cancellationToken);
                var ownsConfig = deployments.Any(item => item.Status is "Configured" or "NeedsUserInput" or "PrerequisiteMissing" && PathsEqual(item.TargetPath, configPath));
                var existing = ReadConfig(configPath);
                var servers = GetOrCreateServers(existing);
                var conflict = definitions.Keys.FirstOrDefault(id => servers.ContainsKey(id) && !ownsConfig);
                var secrets = definitions.Values.Any(HasSecretEnvironment);
                items.Add(conflict is null
                    ? new(artifact.Id, request.TargetId, request.Scope, secrets ? "CompatibleNeedsUserInput" : "Compatible", configPath, secrets ? "將寫入 REPLACE_WITH_YOUR_API_KEY；需由使用者補齊。" : null, DateTimeOffset.UtcNow)
                    : new(artifact.Id, request.TargetId, request.Scope, "BlockedByConflict", configPath, $"MCP ID '{conflict}' 已存在且非 Wingman 管理，預覽不允許覆寫。", DateTimeOffset.UtcNow));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DirectoryNotFoundException or KeyNotFoundException or IOException or UnauthorizedAccessException or JsonException)
            { items.Add(new(request.ArtifactId, request.TargetId, request.Scope, "Incompatible", configPath, ex.Message, DateTimeOffset.UtcNow)); }
        }
        await installabilityStore.SaveAsync(items, cancellationToken);
        return new(requests[0].ArtifactId, "ConfigureMcp", items);
    }

    public async Task<MarketplaceDeploymentBatchResult> ConfigureAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) throw new ArgumentException("至少選擇一個配置目標。", nameof(requests));
        var results = new List<MarketplaceDeploymentResult>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var artifact = await artifactStore.GetArtifactAsync(request.ArtifactId, cancellationToken) ?? throw new KeyNotFoundException("找不到 artifact。");
                if (artifact.Kind != MarketplaceArtifactKind.McpServer || artifact.Status != MarketplaceDiscoveryStatus.Resolved)
                    throw new InvalidOperationException("只有已解析且可驗證的 MCP Definition 可以一鍵配置。");
                if (!_adapters.TryGetValue(request.TargetId, out var adapter)) throw new InvalidOperationException("不支援的 Agent Target。");
                var configPath = adapter.ResolveMcpConfigPath(request.Scope, request.ProjectPath);
                var definitions = await ReadDefinitionAsync(artifact.SnapshotPath, cancellationToken);
                var deployments = await deploymentStore.ListDeploymentsAsync(artifact.Id, cancellationToken);
                var ownsConfig = deployments.Any(item => item.Status is "Configured" or "NeedsUserInput" or "PrerequisiteMissing" && PathsEqual(item.TargetPath, configPath));
                var document = ReadConfig(configPath);
                var servers = GetOrCreateServers(document);
                foreach (var (serverId, definition) in definitions)
                {
                    if (servers.ContainsKey(serverId) && !ownsConfig)
                        throw new InvalidOperationException($"MCP ID '{serverId}' 已存在，且無法證明由 Wingman 建立，因此不覆寫。");
                    var replacement = SanitizeSecrets((JsonObject)definition.DeepClone());
                    if (servers[serverId] is JsonObject existing && ownsConfig) PreserveUserSecrets(existing, replacement);
                    servers[serverId] = replacement;
                }
                WriteAtomically(configPath, document);
                var needsUserInput = definitions.Values.Any(HasSecretEnvironment);
                var status = needsUserInput ? "NeedsUserInput" : "Configured";
                await deploymentStore.SaveDeploymentAsync(artifact.Id, request, configPath, HashConfig(document), status, cancellationToken);
                results.Add(new(request.TargetId, request.Scope, status, configPath, needsUserInput ? "已填入 REPLACE_WITH_YOUR_API_KEY；請在目標 Agent config 補上實際 Key。" : null));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
            { results.Add(new(request.TargetId, request.Scope, "Failed", null, ex.Message)); }
        }
        await RecordResultsAsync("mcp-configure", requests[0].ArtifactId, results, cancellationToken);
        return new(results);
    }

    public async Task<MarketplaceDeploymentBatchResult> RemoveFromAllManagedTargetsAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await artifactStore.GetArtifactAsync(artifactId, cancellationToken) ?? throw new KeyNotFoundException("找不到 artifact。");
        if (artifact.Kind != MarketplaceArtifactKind.McpServer) throw new InvalidOperationException("只有 MCP artifact 可以移除 MCP 設定。");
        var definitions = await ReadDefinitionAsync(artifact.SnapshotPath, cancellationToken);
        var results = new List<MarketplaceDeploymentResult>();
        foreach (var deployment in await deploymentStore.ListDeploymentsAsync(artifactId, cancellationToken))
        {
            if (deployment.Status is not ("Configured" or "NeedsUserInput" or "PrerequisiteMissing")) continue;
            try
            {
                if (File.Exists(deployment.TargetPath))
                {
                    var document = ReadConfig(deployment.TargetPath);
                    var servers = GetOrCreateServers(document);
                    foreach (var serverId in definitions.Keys) servers.Remove(serverId);
                    WriteAtomically(deployment.TargetPath, document);
                }
                await deploymentStore.UpdateDeploymentStatusAsync(artifactId, deployment.TargetId, deployment.Scope, "Removed", cancellationToken);
                results.Add(new(deployment.TargetId, deployment.Scope, "Removed", deployment.TargetPath, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { results.Add(new(deployment.TargetId, deployment.Scope, "Failed", deployment.TargetPath, ex.Message)); }
        }
        await RecordResultsAsync("mcp-remove", artifactId, results, cancellationToken);
        return new(results);
    }

    private async Task RecordResultsAsync(string eventType, string artifactId, IReadOnlyList<MarketplaceDeploymentResult> results, CancellationToken ct)
    {
        if (activity is null) return;
        var operationId = Guid.NewGuid().ToString("N");
        foreach (var result in results)
            await activity.RecordAsync(new(Guid.NewGuid().ToString("N"), operationId, eventType, result.Status, artifactId, result.TargetId,
                result.Message ?? result.TargetPath, DateTimeOffset.UtcNow), ct);
    }

    private static async Task<IReadOnlyDictionary<string, JsonObject>> ReadDefinitionAsync(string snapshotPath, CancellationToken ct)
    {
        var file = Directory.EnumerateFiles(snapshotPath, ".mcp.json", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidDataException("MCP artifact 缺少 .mcp.json。");
        var json = await File.ReadAllTextAsync(file, ct);
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException(".mcp.json 必須是 JSON object。");
        if (root["mcpServers"] is not JsonObject servers || servers.Count == 0) throw new InvalidDataException(".mcp.json 缺少 mcpServers definition。");
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (id, value) in servers)
        {
            if (string.IsNullOrWhiteSpace(id) || value is not JsonObject definition) throw new InvalidDataException("mcpServers 必須包含具名稱的 object definition。");
            result[id] = definition;
        }
        return result;
    }

    private static JsonObject ReadConfig(string configPath)
    {
        if (!File.Exists(configPath)) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidDataException("目標 MCP config 必須是 JSON object。");
    }

    private static JsonObject GetOrCreateServers(JsonObject document)
    {
        if (document["mcpServers"] is null)
        {
            var servers = new JsonObject();
            document["mcpServers"] = servers;
            return servers;
        }
        return document["mcpServers"] as JsonObject ?? throw new InvalidDataException("目標 MCP config 的 mcpServers 必須是 JSON object。");
    }

    private static JsonObject SanitizeSecrets(JsonObject definition)
    {
        if (definition["env"] is not JsonObject env) return definition;
        foreach (var (name, value) in env.ToList())
            if (IsSecretName(name) && value is not null) env[name] = SecretPlaceholder;
        return definition;
    }

    private static void PreserveUserSecrets(JsonObject existing, JsonObject replacement)
    {
        if (existing["env"] is not JsonObject existingEnv || replacement["env"] is not JsonObject replacementEnv) return;
        foreach (var (name, _) in replacementEnv.ToList())
        {
            if (!IsSecretName(name) || existingEnv[name] is null) continue;
            if (existingEnv[name] is not JsonValue existingValue || !existingValue.TryGetValue<string>(out var secret) || secret == SecretPlaceholder) continue;
            replacementEnv[name] = existingEnv[name]!.DeepClone();
        }
    }

    private static bool HasSecretEnvironment(JsonObject definition) => definition["env"] is JsonObject env && env.Any(pair => IsSecretName(pair.Key));
    private static bool IsSecretName(string name) => name.Contains("key", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase);
    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string HashConfig(JsonObject document) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.ToJsonString()))).ToLowerInvariant();

    private static void WriteAtomically(string path, JsonObject document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + ".wingman-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(staging, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(staging, path, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
