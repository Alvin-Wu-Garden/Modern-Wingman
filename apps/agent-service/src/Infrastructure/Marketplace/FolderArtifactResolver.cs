using System.Security.Cryptography;
using System.Text.Json;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>只解析使用者明確選擇的 folder；不執行來源內容，也不依 repository 名稱猜測 artifact。</summary>
public sealed class FolderArtifactResolver : IArtifactResolver
{
    private const int MaxFiles = 2_000;
    private const long MaxTotalBytes = 100 * 1024 * 1024;
    private const long MaxFileBytes = 10 * 1024 * 1024;

    public async Task<IReadOnlyList<MarketplaceArtifactCandidate>> ResolveFolderAsync(string sourceFolder, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder)) throw new ArgumentException("請選擇要匯入的資料夾。", nameof(sourceFolder));
        var root = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到匯入資料夾：{root}");
        ValidateTree(root, cancellationToken);

        var candidates = new List<MarketplaceArtifactCandidate>();
        foreach (var skillFile in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateRoot = Path.GetDirectoryName(skillFile)!;
            var validation = await ValidateSkillAsync(skillFile, cancellationToken);
            candidates.Add(Create(candidateRoot, MarketplaceArtifactKind.Skill, validation.IsValid ? MarketplaceDiscoveryStatus.Resolved : MarketplaceDiscoveryStatus.Invalid, "agent-skill-standard/v1", validation.Error));
        }
        foreach (var mcpFile in Directory.EnumerateFiles(root, ".mcp.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isCanonical = await HasCanonicalMcpDefinitionAsync(mcpFile, cancellationToken);
            candidates.Add(Create(Path.GetDirectoryName(mcpFile)!, MarketplaceArtifactKind.McpServer, isCanonical ? MarketplaceDiscoveryStatus.Resolved : MarketplaceDiscoveryStatus.ManualSetupRequired, isCanonical ? "wingman-mcp-definition/v1" : null, isCanonical ? null : "找不到可確定解析的 mcpServers definition，需手動補齊設定。"));
        }
        return candidates.GroupBy(candidate => $"{candidate.Kind}|{candidate.ArtifactPath}", StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();

        MarketplaceArtifactCandidate Create(string artifactPath, MarketplaceArtifactKind kind, MarketplaceDiscoveryStatus status, string? profile, string? message)
            => new(Guid.NewGuid().ToString("N"), root, artifactPath, kind, Path.GetFileName(artifactPath), status, profile, message, DateTimeOffset.UtcNow);
    }

    private static void ValidateTree(string root, CancellationToken cancellationToken)
    {
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fileCount = 0; long totalBytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("匯入來源包含離開 root 的 path。");
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("匯入來源不可包含 symlink、junction 或其他 reparse point。");
            if ((attributes & FileAttributes.Directory) != 0) continue;
            if (++fileCount > MaxFiles) throw new InvalidDataException($"匯入來源超過檔案上限 {MaxFiles}。");
            var length = new FileInfo(fullPath).Length;
            if (length > MaxFileBytes) throw new InvalidDataException("匯入來源包含超過單檔大小上限的檔案。");
            if ((totalBytes += length) > MaxTotalBytes) throw new InvalidDataException("匯入來源超過總大小上限。");
        }
    }

    private static async Task<(bool IsValid, string? Error)> ValidateSkillAsync(string skillFile, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(skillFile, ct);
        if (string.IsNullOrWhiteSpace(content)) return (false, "SKILL.md 不可為空白。");
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (false, "SKILL.md 缺少 YAML frontmatter。");
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) return (false, "SKILL.md 的 YAML frontmatter 未正確結束。");
        var frontmatter = normalized[4..end];
        if (!frontmatter.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => line.Contains(':', StringComparison.Ordinal))) return (false, "SKILL.md 的 YAML frontmatter 無法解析。");
        return (true, null);
    }

    private static async Task<bool> HasCanonicalMcpDefinitionAsync(string file, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(file);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object && servers.EnumerateObject().Any();
        }
        catch (JsonException) { return false; }
    }

    internal static async Task<string> HashDirectoryAsync(string root, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/')));
            hash.AppendData([0]);
            await using var stream = File.OpenRead(file); var buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
