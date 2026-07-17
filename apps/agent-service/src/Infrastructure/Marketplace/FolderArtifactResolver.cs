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
        foreach (var pluginManifest in Directory.EnumerateFiles(root, "plugin.json", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(Path.GetDirectoryName(path))?.Equals(".codex-plugin", StringComparison.OrdinalIgnoreCase) == true)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pluginRoot = Directory.GetParent(Path.GetDirectoryName(pluginManifest)!)!.FullName;
            var validation = await ValidatePluginAsync(pluginManifest, Path.Combine(pluginRoot, "wingman.json"), cancellationToken);
            candidates.Add(Create(pluginRoot, MarketplaceArtifactKind.WingmanPlugin, validation.IsValid ? MarketplaceDiscoveryStatus.Resolved : MarketplaceDiscoveryStatus.Invalid, "codex-plugin-compat/2026-07", validation.Error));
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

    private static async Task<(bool IsValid, string? Error)> ValidatePluginAsync(string pluginManifest, string wingmanManifest, CancellationToken ct)
    {
        if (!File.Exists(wingmanManifest)) return (false, "缺少 wingman.json。");
        var pluginRoot = Directory.GetParent(Path.GetDirectoryName(pluginManifest)!)!.FullName;
        if (Directory.EnumerateFiles(pluginRoot, "*", SearchOption.AllDirectories)
            .Any(path => Path.GetExtension(path) is ".dll" or ".so" or ".dylib"))
            return (false, "Wingman Plugin 不允許攜帶 .NET 或 native binary extension。");
        try
        {
            await using var stream = File.OpenRead(pluginManifest);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            foreach (var property in new[] { "name", "version" })
                if (!document.RootElement.TryGetProperty(property, out var value) || string.IsNullOrWhiteSpace(value.GetString())) return (false, $".codex-plugin/plugin.json 缺少必要欄位 {property}。");
            foreach (var component in new[] { "skills", "mcpServers", "hooks", "apps" })
            {
                if (!document.RootElement.TryGetProperty(component, out var value)) continue;
                foreach (var path in ComponentPaths(value))
                {
                    var relative = path.Replace('\\', '/');
                    if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
                    if (Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(relative) || relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..")) return (false, $"Plugin component path 不安全：{path}");
                    var resolved = Path.GetFullPath(Path.Combine(pluginRoot, relative));
                    var prefix = pluginRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (!File.Exists(resolved) && !Directory.Exists(resolved))) return (false, $"Plugin component 不存在：{path}");
                }
            }
            await using var wingmanStream = File.OpenRead(wingmanManifest);
            using var wingman = await JsonDocument.ParseAsync(wingmanStream, cancellationToken: ct);
            if (wingman.RootElement.ValueKind != JsonValueKind.Object) return (false, "wingman.json 必須是 JSON object。");
            if (new[] { "install", "uninstall", "update", "postinstall", "preinstall" }.Any(name => wingman.RootElement.TryGetProperty(name, out _)))
                return (false, "Wingman Plugin 不允許 install/update/uninstall lifecycle script。");
            return (true, null);
        }
        catch (JsonException) { return (false, ".codex-plugin/plugin.json 不是有效 JSON。"); }

        static IEnumerable<string> ComponentPaths(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array => value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!),
            _ => [],
        };
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
