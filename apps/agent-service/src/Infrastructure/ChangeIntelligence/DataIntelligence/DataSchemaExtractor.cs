using System.Security.Cryptography;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;

public sealed class DataSchemaExtractor(IEnumerable<IDataArtifactAdapter> adapters) : IDataSchemaExtractor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".sql", ".cs", ".java", ".xml" };
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", "bin", "obj", "node_modules", "target", "dist", "build", "packages" };

    public Task<DataExtractionResult> ExtractAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default) =>
        ExtractCoreAsync(workspaceRoot, files: null, cancellationToken);

    public Task<DataExtractionResult> ExtractFilesAsync(
        string workspaceRoot,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default) =>
        ExtractCoreAsync(workspaceRoot, files, cancellationToken);

    private async Task<DataExtractionResult> ExtractCoreAsync(
        string workspaceRoot,
        IReadOnlyList<string>? files,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        var graph = new CodeAnalysisResult();
        var diagnostics = new List<DataExtractionDiagnostic>();
        var gaps = new HashSet<string>(StringComparer.Ordinal);
        var scanned = new List<DataArtifactScanRecord>();
        var skipped = new List<DataArtifactScanRecord>();

        foreach (var path in (files ?? EnumerateFiles(root).ToList())
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFullPath)
            .Where(path => path.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            string content;
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                await using var stream = new MemoryStream(bytes, writable: false);
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: false);
                content = await reader.ReadToEndAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new(relative, "scanner", "error", ex.Message));
                skipped.Add(new(
                    relative,
                    Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    "skipped",
                    ex.Message,
                    string.Empty));
                continue;
            }

            // contentHash is always the SHA-256 of the original file bytes. Hashing
            // decoded/re-encoded text would give BOM and non-UTF-8 files a different
            // identity from the manifest scanner and make unchanged projects rebuild.
            var artifact = new DataArtifact(
                path,
                relative,
                content,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            var matching = adapters
                .Where(adapter => adapter.CanAnalyze(artifact))
                .OrderBy(adapter => adapter.Id, StringComparer.Ordinal)
                .ThenBy(adapter => adapter.Version, StringComparer.Ordinal)
                .ToList();
            if (matching.Count == 0)
            {
                skipped.Add(new(relative, Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), "skipped", "沒有相符的 Data Artifact adapter。", artifact.ContentHash));
                continue;
            }
            scanned.Add(new(relative, string.Join(',', matching.Select(item => item.Id)), "indexed", null, artifact.ContentHash));
            foreach (var adapter in matching)
            {
                try
                {
                    var result = adapter.Analyze(artifact);
                    // Preserve every extractor contribution. V2 canonicalization merges
                    // duplicate identities into deterministic locations/evidence arrays;
                    // first-wins here would irreversibly discard provenance.
                    graph.Nodes.AddRange(result.Graph.Nodes);
                    graph.Edges.AddRange(result.Graph.Edges);
                    diagnostics.AddRange(result.Diagnostics);
                    foreach (var gap in result.CapabilityGaps) gaps.Add(gap);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add(new(relative, adapter.Id, "error", ex.Message));
                }
            }
        }

        if (graph.Nodes.Count == 0 && files is null)
            gaps.Add("未偵測到受支援的 DDL、migration、SQL query 或 ORM mapping；資料影響範圍未知。");
        return new(graph, diagnostics, gaps.ToList(), scanned, skipped);
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> files;
            IEnumerable<string> subdirectories;
            try
            {
                files = Directory.EnumerateFiles(directory);
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
                if (SupportedExtensions.Contains(Path.GetExtension(file))) yield return file;
            foreach (var child in subdirectories)
                if (!ExcludedDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
        }
    }
}
