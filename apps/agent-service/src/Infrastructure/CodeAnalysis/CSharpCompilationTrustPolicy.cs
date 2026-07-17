using AgentService.Domain.Models;

namespace AgentService.Infrastructure.CodeAnalysis;

/// <summary>
/// Prevents the synthetic workspace-wide Roslyn compilation from presenting a
/// cross-project binding as compiler-exact.  Source projects are compiled together
/// today to remain restore-free; until per-TFM MSBuildWorkspace loading is available,
/// every cross-project relation is explicitly heuristic and carries the limitation.
/// </summary>
internal static class CSharpCompilationTrustPolicy
{
    public static void Apply(CodeAnalysisResult result)
    {
        var projectKeys = result.Nodes
            .Where(node => node.Kind == CodeNodeKind.Project)
            .Select(node => node.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (projectKeys.Count < 2) return;

        var fileOwners = result.Edges
            .Where(edge => edge.Kind == CodeEdgeKind.Contains &&
                           projectKeys.Contains(edge.SourceKey) &&
                           edge.TargetKey.StartsWith("file:", StringComparison.Ordinal))
            .GroupBy(edge => edge.TargetKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().SourceKey, StringComparer.OrdinalIgnoreCase);
        var declaredReferences = result.Edges
            .Where(edge => edge.Kind == CodeEdgeKind.ProjectReferences)
            .Select(edge => (edge.SourceKey, edge.TargetKey))
            .ToHashSet();
        var nodes = result.Nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);

        foreach (var edge in result.Edges.Where(edge => edge.SourceKind == GraphSourceKind.Compiler))
        {
            if (!nodes.TryGetValue(edge.SourceKey, out var source) ||
                !nodes.TryGetValue(edge.TargetKey, out var target) ||
                string.IsNullOrWhiteSpace(source.FilePath) ||
                string.IsNullOrWhiteSpace(target.FilePath) ||
                !fileOwners.TryGetValue($"file:{source.FilePath}", out var sourceProject) ||
                !fileOwners.TryGetValue($"file:{target.FilePath}", out var targetProject) ||
                string.Equals(sourceProject, targetProject, StringComparison.Ordinal))
                continue;

            var declared = declaredReferences.Contains((sourceProject, targetProject));
            edge.Confidence = GraphConfidence.Heuristic;
            edge.Reason = declared
                ? "Cross-project symbol resolved by Wingman's synthetic workspace compilation; project reference is declared, but target-framework/package binding was not loaded by MSBuildWorkspace."
                : "Cross-project symbol resolved only because all workspace sources share a synthetic compilation; no matching project reference was found.";
        }
    }
}
