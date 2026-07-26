using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;
using Wingman.Marketplace.Application;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentService.Infrastructure.Marketplace;

public sealed class MarketplaceArtifactService(
    IArtifactResolver resolver,
    IMarketplaceArtifactStore store,
    MarketplaceRegistryPathResolver paths,
    MarketplaceArtifactQualityScorer qualityScorer) : IMarketplaceArtifactService
{
    public Task<IReadOnlyList<MarketplaceArtifact>> ListArtifactsAsync(CancellationToken cancellationToken = default) => store.ListArtifactsAsync(cancellationToken);

    public Task<MarketplaceImportResult> ImportFolderAsync(string sourceFolder, CancellationToken cancellationToken = default)
        => ImportFolderCoreAsync(sourceFolder, sourceLocation: null, cancellationToken);

    private async Task<MarketplaceImportResult> ImportFolderCoreAsync(string sourceFolder, string? sourceLocation, CancellationToken cancellationToken)
    {
        var candidates = await resolver.ResolveFolderAsync(sourceFolder, cancellationToken);
        if (!string.IsNullOrWhiteSpace(sourceLocation)) candidates = candidates.Select(candidate => candidate with { SourceLocation = sourceLocation }).ToList();
        var artifacts = new List<MarketplaceArtifact>();
        foreach (var candidate in candidates.Where(candidate => candidate.Status is MarketplaceDiscoveryStatus.Resolved or MarketplaceDiscoveryStatus.ManualSetupRequired))
        {
            var (snapshotPath, hash) = await CopyToImmutableCacheAsync(candidate.ArtifactPath, candidate.Kind, cancellationToken);
            var existing = await store.GetArtifactByContentHashAsync(hash, candidate.Kind, cancellationToken);
            artifacts.Add(existing ?? new(Guid.NewGuid().ToString("N"), candidate.Id, candidate.Kind, candidate.DisplayName, snapshotPath, hash, candidate.Status, candidate.ValidationProfileId, DateTimeOffset.UtcNow));
        }
        await store.SaveImportAsync(candidates, artifacts, artifacts.Select(qualityScorer.Score).ToList(), cancellationToken);
        return new MarketplaceImportResult(
            sourceLocation ?? Path.GetFullPath(sourceFolder),
            candidates,
            artifacts);
    }

    public async Task<MarketplaceImportResult> ImportArchiveAsync(string archivePath, string? sourceLocation = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath)) throw new ArgumentException("請選擇要匯入的 archive。", nameof(archivePath));
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到 archive。", fullPath);
        if (!Path.GetExtension(fullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(fullPath).Equals(".skill", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("只支援 ZIP 或 .skill archive。");
        Directory.CreateDirectory(paths.StagingRoot);
        var staging = Path.Combine(paths.StagingRoot, "archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractSafeArchive(fullPath, staging, cancellationToken);
            return await ImportFolderCoreAsync(staging, sourceLocation, cancellationToken);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private async Task<(string Path, string Hash)> CopyToImmutableCacheAsync(string source, MarketplaceArtifactKind kind, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.BlobRoot); Directory.CreateDirectory(paths.StagingRoot);
        var staging = Path.Combine(paths.StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            CopyTree(source, staging, ct);
            if (kind == MarketplaceArtifactKind.McpServer) SanitizeMcpSecrets(staging);
            var hash = await FolderArtifactResolver.HashDirectoryAsync(staging, ct);
            var destination = Path.Combine(paths.BlobRoot, hash);
            if (Directory.Exists(destination)) return (destination, hash);
            if (!Directory.Exists(destination)) Directory.Move(staging, destination);
            return (destination, hash);
        }
        finally { if (Directory.Exists(staging)) await Task.Run(() => Directory.Delete(staging, recursive: true), CancellationToken.None); }
    }

    // Registry snapshots must never retain a key that happened to be present in an imported MCP file.
    // This is intentionally conservative: only names that look like credentials are rewritten.
    private static void SanitizeMcpSecrets(string snapshotRoot)
    {
        foreach (var file in Directory.EnumerateFiles(snapshotRoot, ".mcp.json", SearchOption.AllDirectories))
        {
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (root?["mcpServers"] is not JsonObject servers) continue;
            var changed = false;
            foreach (var (_, definition) in servers)
            {
                if (definition?["env"] is not JsonObject env) continue;
                foreach (var (name, value) in env.ToList())
                {
                    if (value is null || !LooksLikeSecret(name)) continue;
                    env[name] = "REPLACE_WITH_YOUR_API_KEY";
                    changed = true;
                }
            }
            if (changed) File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static bool LooksLikeSecret(string name) => name.Contains("key", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase);

    private static void CopyTree(string source, string destination, CancellationToken ct)
    {
        var sourceRoot = Path.GetFullPath(source); var destinationRoot = Path.GetFullPath(destination);
        var destinationPrefix = destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested(); if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不可複製 reparse point。");
            var target = Path.GetFullPath(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
            if (!target.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不安全的匯入路徑。");
            Directory.CreateDirectory(target);
        }
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested(); if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不可複製 reparse point。");
            var target = Path.GetFullPath(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file)));
            if (!target.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不安全的匯入路徑。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, overwrite: false);
        }
    }

    private static void ExtractSafeArchive(string archivePath, string destination, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 2_000) throw new InvalidDataException("archive 超過檔案上限。");
        long totalSize = 0;
        var root = Path.GetFullPath(destination); var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.FullName)) continue;
            if (Path.IsPathRooted(entry.FullName) || entry.FullName.Replace('\\', '/').Split('/').Any(part => part is ".." or ".")) throw new InvalidDataException("archive 包含不安全路徑。");
            if (entry.Length > 10 * 1024 * 1024 || (totalSize += entry.Length) > 100 * 1024 * 1024) throw new InvalidDataException("archive 超過大小上限。");
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("archive path traversal。");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open(); using var output = File.Create(target); input.CopyTo(output);
        }
    }
}
