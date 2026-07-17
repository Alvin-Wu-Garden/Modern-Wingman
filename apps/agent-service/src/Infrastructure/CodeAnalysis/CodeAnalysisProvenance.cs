using System.Security.Cryptography;
using System.Text;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.CodeAnalysis;

/// <summary>
/// Applies run-level provenance to every extracted relationship.  A relationship can
/// depend on more than one source file, so its content hash is the deterministic hash
/// of the analyzed source snapshot rather than an arbitrary endpoint file.
/// </summary>
internal static class CodeAnalysisProvenance
{
    public static void StampEdges(CodeAnalysisResult result, DateTimeOffset indexedAt)
    {
        var sourceHashes = result.Nodes
            .Where(node => node.Kind == CodeNodeKind.File && !string.IsNullOrWhiteSpace(node.ContentHash))
            .Select(node => $"{node.FilePath}:{node.ContentHash}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var snapshotHash = sourceHashes.Count == 0
            ? null
            : Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', sourceHashes))))
                .ToLowerInvariant();

        foreach (var edge in result.Edges)
        {
            edge.IndexedAt ??= indexedAt;
            edge.ContentHash ??= snapshotHash;
        }
    }
}
