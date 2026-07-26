using System.Text.Json;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

public sealed class MarketplaceDeploymentService(
    IMarketplaceArtifactStore artifactStore,
    IMarketplaceDeploymentStore deploymentStore,
    IMarketplaceInstallabilityStore installabilityStore,
    IEnumerable<IAgentTargetAdapter> adapters) : IMarketplaceDeploymentService
{
    private readonly IReadOnlyDictionary<string, IAgentTargetAdapter> _adapters = adapters.ToDictionary(adapter => adapter.Descriptor.Id, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<MarketplaceTargetDescriptor>> ListTargetsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MarketplaceTargetDescriptor>>(_adapters.Values.Select(DescribeTarget).OrderBy(target => target.DisplayName).ToList());

    public async Task<MarketplaceDeploymentPlan> PreviewSkillAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) throw new ArgumentException("至少選擇一個部署目標。", nameof(requests));
        var items = new List<MarketplaceInstallabilityResult>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? targetPath = null;
            try
            {
                var artifact = await artifactStore.GetArtifactAsync(request.ArtifactId, cancellationToken) ?? throw new KeyNotFoundException("找不到 artifact。");
                if (artifact.Kind != MarketplaceArtifactKind.Skill) throw new InvalidOperationException("只有原生 Skill artifact 可以跨 IDE copy 部署。");
                if (!_adapters.TryGetValue(request.TargetId, out var adapter)) throw new InvalidOperationException("不支援的 Agent Target。");
                if (!adapter.Descriptor.SupportsSkill) throw new InvalidOperationException($"{adapter.Descriptor.DisplayName} 不支援 Skill。");
                targetPath = Path.Combine(adapter.ResolveSkillDirectory(request.Scope, request.ProjectPath), SafeName(artifact.DisplayName));
                if (!Directory.Exists(targetPath))
                    items.Add(new(artifact.Id, request.TargetId, request.Scope, "Compatible", targetPath, null, DateTimeOffset.UtcNow));
                else
                {
                    var hash = await FolderArtifactResolver.HashDirectoryAsync(targetPath, cancellationToken);
                    items.Add(new(artifact.Id, request.TargetId, request.Scope,
                        string.Equals(hash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase) ? "AlreadyDeployed" : "BlockedByConflict",
                        targetPath,
                        string.Equals(hash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase) ? "目標已包含相同內容；執行後將由 Wingman 記錄為受管理部署。" : "目標已存在不同內容，Wingman 不會覆寫。",
                        DateTimeOffset.UtcNow));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DirectoryNotFoundException or KeyNotFoundException or IOException or UnauthorizedAccessException)
            { items.Add(new(request.ArtifactId, request.TargetId, request.Scope, "Incompatible", targetPath, ex.Message, DateTimeOffset.UtcNow)); }
        }
        await installabilityStore.SaveAsync(items, cancellationToken);
        return new(requests[0].ArtifactId, "DeploySkill", items);
    }

    public Task<IReadOnlyList<MarketplaceDeploymentState>> ListDeploymentStatesAsync(string artifactId, CancellationToken cancellationToken = default)
        => deploymentStore.ListDeploymentStatesAsync(artifactId, cancellationToken);

    public async Task<MarketplaceDeploymentBatchResult> DeploySkillAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) throw new ArgumentException("至少選擇一個部署目標。", nameof(requests));
        var results = new List<MarketplaceDeploymentResult>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var artifact = await artifactStore.GetArtifactAsync(request.ArtifactId, cancellationToken) ?? throw new KeyNotFoundException("找不到 artifact。");
                if (artifact.Kind != MarketplaceArtifactKind.Skill) throw new InvalidOperationException("只有原生 Skill artifact 可以跨 IDE copy 部署。");
                if (!_adapters.TryGetValue(request.TargetId, out var adapter)) throw new InvalidOperationException("不支援的 Agent Target。");
                var targetDirectory = adapter.ResolveSkillDirectory(request.Scope, request.ProjectPath);
                var skillName = SafeName(artifact.DisplayName);
                var targetPath = Path.Combine(targetDirectory, skillName);
                if (Directory.Exists(targetPath))
                {
                    var targetHash = await FolderArtifactResolver.HashDirectoryAsync(targetPath, cancellationToken);
                    if (!string.Equals(targetHash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        await deploymentStore.SaveDeploymentAsync(artifact.Id, request, targetPath, artifact.ContentHash, "BlockedByConflict", cancellationToken);
                        results.Add(new(request.TargetId, request.Scope, "BlockedByConflict", targetPath, "目標已存在非 Wingman managed 或內容已變更的 Skill，未覆寫。"));
                        continue;
                    }
                }
                else CopyAtomically(artifact.SnapshotPath, targetPath, cancellationToken);
                await deploymentStore.SaveDeploymentAsync(artifact.Id, request, targetPath, artifact.ContentHash, "Deployed", cancellationToken);
                if (request.Scope == MarketplaceDeploymentScope.Project) WriteProjectLock(request.ProjectPath!, artifact, request, targetPath);
                results.Add(new(request.TargetId, request.Scope, "Deployed", targetPath, null));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or KeyNotFoundException)
            { results.Add(new(request.TargetId, request.Scope, "Failed", null, ex.Message)); }
        }
        return new(results);
    }

    public async Task<MarketplaceDeploymentBatchResult> RemoveFromAllManagedTargetsAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        var deployments = await deploymentStore.ListDeploymentsAsync(artifactId, cancellationToken);
        var results = new List<MarketplaceDeploymentResult>();
        foreach (var deployment in deployments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deployment.Status is not ("Deployed" or "BlockedByConflict" or "DetachedDueToDrift")) continue;
            try
            {
                if (Directory.Exists(deployment.TargetPath))
                {
                    var currentHash = await FolderArtifactResolver.HashDirectoryAsync(deployment.TargetPath, cancellationToken);
                    if (!string.Equals(currentHash, deployment.DeployedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        await deploymentStore.UpdateDeploymentStatusAsync(artifactId, deployment.TargetId, deployment.Scope, "DetachedDueToDrift", cancellationToken);
                        results.Add(new(deployment.TargetId, deployment.Scope, "DetachedDueToDrift", deployment.TargetPath, "使用者已修改目標內容，Wingman 不刪除。"));
                        continue;
                    }
                    Directory.Delete(deployment.TargetPath, recursive: true);
                }
                await deploymentStore.UpdateDeploymentStatusAsync(artifactId, deployment.TargetId, deployment.Scope, "Removed", cancellationToken);
                results.Add(new(deployment.TargetId, deployment.Scope, "Removed", deployment.TargetPath, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { results.Add(new(deployment.TargetId, deployment.Scope, "Failed", deployment.TargetPath, ex.Message)); }
        }
        return new(results);
    }

    private static void CopyAtomically(string source, string destination, CancellationToken ct)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("artifact snapshot 不存在。");
        var parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".wingman-staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(source, staging, ct); Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) { ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory))); }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { ct.ThrowIfCancellationRequested(); var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); }
    }

    private static string SafeName(string value)
    {
        var safe = string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-')).Trim('-', '.');
        return string.IsNullOrWhiteSpace(safe) ? "skill" : safe[..Math.Min(safe.Length, 100)];
    }

    private static MarketplaceTargetDescriptor DescribeTarget(IAgentTargetAdapter adapter)
    {
        var descriptor = adapter.Descriptor;
        try
        {
            var skillsPath = descriptor.SupportsSkill && descriptor.SupportsGlobalScope ? adapter.ResolveSkillDirectory(MarketplaceDeploymentScope.Global, null) : null;
            string? configPath = null;
            if (descriptor.SupportsMcp && descriptor.SupportsGlobalScope)
            {
                try { configPath = adapter.ResolveMcpConfigPath(MarketplaceDeploymentScope.Global, null); }
                catch (InvalidOperationException) { }
            }
            var detected = (skillsPath is not null && Directory.Exists(Path.GetDirectoryName(skillsPath))) || (configPath is not null && File.Exists(configPath));
            return descriptor with { IsDetected = detected, DetectionReason = detected ? "已偵測到使用者層設定或資料夾。" : "尚未偵測到使用者層設定；仍可手動選擇此 Target。" };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { return descriptor with { IsDetected = false, DetectionReason = ex.Message }; }
    }

    private static void WriteProjectLock(string projectPath, MarketplaceArtifact artifact, MarketplaceDeploymentRequest request, string targetPath)
    {
        var folder = Path.Combine(projectPath, ".wingman"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "extensions.lock.json");
        var entries = File.Exists(path) ? JsonSerializer.Deserialize<List<ProjectLockEntry>>(File.ReadAllText(path)) ?? [] : [];
        entries.RemoveAll(entry => entry.ArtifactId == artifact.Id && entry.TargetId == request.TargetId && entry.Scope == request.Scope.ToString());
        entries.Add(new(artifact.Id, artifact.ContentHash, request.TargetId, request.Scope.ToString(), targetPath));
        var staging = path + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllText(staging, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true })); File.Move(staging, path, overwrite: true);
    }

    private sealed record ProjectLockEntry(string ArtifactId, string ContentHash, string TargetId, string Scope, string TargetPath);
}
