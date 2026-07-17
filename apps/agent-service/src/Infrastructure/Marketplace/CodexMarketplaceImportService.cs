using System.Text.Json;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>只處理使用者明確選取的 Codex marketplace.json；不讀取 Codex 個人設定或 cache。</summary>
public sealed class CodexMarketplaceImportService(IMarketplaceArtifactService artifactService) : ICodexMarketplaceImportService
{
    public async Task<CodexMarketplaceImportResult> ImportAsync(string marketplaceJsonPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(marketplaceJsonPath)) throw new ArgumentException("請選擇 marketplace.json。", nameof(marketplaceJsonPath));
        var fullPath = Path.GetFullPath(marketplaceJsonPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到 marketplace.json。", fullPath);
        var root = Path.GetDirectoryName(fullPath)!;
        await using var stream = File.OpenRead(fullPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Codex marketplace.json 必須包含 plugins array。");

        var imported = 0; var invalid = 0; var skipped = new List<string>();
        foreach (var plugin in plugins.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = plugin.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "unnamed" : "unnamed";
            if (!TryGetLocalPath(plugin, out var relativePath)) { skipped.Add($"{name}: 非 local source，未自動下載。"); continue; }
            var source = Path.GetFullPath(Path.Combine(root, relativePath));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source)) { skipped.Add($"{name}: source.path 無效或離開 marketplace root。"); continue; }
            var result = await artifactService.ImportFolderAsync(source, cancellationToken);
            imported += result.Artifacts.Count;
            invalid += result.Candidates.Count(candidate => candidate.Status == MarketplaceDiscoveryStatus.Invalid);
        }
        return new(imported, invalid, skipped);
    }

    private static bool TryGetLocalPath(JsonElement plugin, out string relativePath)
    {
        relativePath = string.Empty;
        if (!plugin.TryGetProperty("source", out var source)) return false;
        if (source.ValueKind == JsonValueKind.String) { relativePath = source.GetString() ?? string.Empty; return relativePath.StartsWith("./", StringComparison.Ordinal); }
        if (source.ValueKind != JsonValueKind.Object) return false;
        var kind = source.TryGetProperty("source", out var sourceKind) ? sourceKind.GetString() : null;
        if (!string.Equals(kind, "local", StringComparison.OrdinalIgnoreCase) || !source.TryGetProperty("path", out var path)) return false;
        relativePath = path.GetString() ?? string.Empty;
        return relativePath.StartsWith("./", StringComparison.Ordinal);
    }
}
