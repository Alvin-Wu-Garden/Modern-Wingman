using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace AgentService.Infrastructure.CodeAnalysis;

internal sealed record CSharpWorkspaceSnapshot(
    IReadOnlyList<CSharpAnalysisDocument> Documents,
    IReadOnlyList<Compilation> Compilations,
    IReadOnlyList<string> Diagnostics,
    bool IsSynthetic = false);

internal sealed record CSharpBodyChange(
    string AbsolutePath,
    string RelativePath,
    string Text);

internal sealed record CSharpBodyUpdate(
    bool Applied,
    string? EscalationReason,
    IReadOnlyList<CSharpAnalysisDocument> Documents,
    IReadOnlyList<Compilation> AffectedCompilations,
    bool IsSynthetic = false);

/// <summary>Loads real solution/project compilations and falls back without failing indexing.</summary>
internal static class CSharpWorkspaceLoader
{
    private static readonly object RegistrationGate = new();
    private static readonly SemaphoreSlim WorkspaceGate = new(1, 1);
    private const int MaxCachedWorkspaces = 4;
    private static readonly Dictionary<string, WorkspaceCacheEntry> Caches =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _accessSequence;
    private static readonly HashSet<string> BuildFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "nuget.config", "directory.build.props", "directory.build.targets",
        "directory.packages.props",
    };
    private static readonly HashSet<string> BuildFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".sln", ".slnx", ".csproj", ".props", ".targets" };

    public static string ComputeInputFingerprint(
        string projectRoot,
        IEnumerable<(string RelativePath, string ContentHash)> sourceFiles)
    {
        var root = Path.GetFullPath(projectRoot);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var source in sourceFiles.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
            Append(hash, $"source\0{Normalize(source.RelativePath)}\0{source.ContentHash}\n");

        foreach (var path in EnumerateBuildInputs(root))
        {
            var relative = Normalize(Path.GetRelativePath(root, path));
            Append(hash, $"build\0{relative}\0");
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.SequentialScan);
            var contentHash = SHA256.HashData(stream);
            Append(hash, $"{Convert.ToHexString(contentHash).ToLowerInvariant()}\n");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async Task<CSharpWorkspaceSnapshot?> TryLoadAsync(
        string projectRoot,
        IReadOnlyCollection<string> requestedFiles,
        string inputFingerprint,
        ILogger logger,
        CancellationToken ct)
    {
        var normalizedRoot = Path.GetFullPath(projectRoot);
        var solution = Enumerate(normalizedRoot, "*.sln").FirstOrDefault();
        var projects = Enumerate(normalizedRoot, "*.csproj").ToList();
        if (solution is null && projects.Count == 0) return null;

        await WorkspaceGate.WaitAsync(ct);
        try
        {
            if (Caches.TryGetValue(normalizedRoot, out var cached) &&
                string.Equals(cached.InputFingerprint, inputFingerprint, StringComparison.Ordinal))
            {
                Caches[normalizedRoot] = cached = cached with { AccessSequence = ++_accessSequence };
                logger.LogInformation(
                    "Reusing content-addressed MSBuild workspace: {Documents} documents, fingerprint={Fingerprint}",
                    cached.Snapshot.Documents.Count,
                    inputFingerprint[..Math.Min(12, inputFingerprint.Length)]);
                return cached.Snapshot;
            }

            EnsureMsBuildRegistered();
            using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
            {
                ["DesignTimeBuild"] = "true",
                ["BuildProjectReferences"] = "false",
                ["SkipCompilerExecution"] = "true",
            });
            workspace.SkipUnrecognizedProjects = true;
            var diagnostics = new List<string>();
            workspace.WorkspaceFailed += (_, args) => diagnostics.Add(args.Diagnostic.Message);

            Solution loaded;
            if (solution is not null)
            {
                loaded = await workspace.OpenSolutionAsync(solution, cancellationToken: ct);
            }
            else
            {
                loaded = workspace.CurrentSolution;
                foreach (var projectPath in projects)
                {
                    var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
                    loaded = project.Solution;
                }
            }

            var requested = requestedFiles.Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var documents = new List<CSharpAnalysisDocument>();
            var compilations = new List<Compilation>();
            foreach (var project in loaded.Projects.Where(project => project.Language == LanguageNames.CSharp))
            {
                ct.ThrowIfCancellationRequested();
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null)
                {
                    diagnostics.Add($"Compilation unavailable: {project.FilePath ?? project.Name}");
                    continue;
                }
                compilations.Add(compilation);
                foreach (var document in project.Documents)
                {
                    if (document.FilePath is null || !requested.Contains(Path.GetFullPath(document.FilePath))) continue;
                    var root = await document.GetSyntaxRootAsync(ct);
                    var model = await document.GetSemanticModelAsync(ct);
                    if (root is null || model is null) continue;
                    var relative = Path.GetRelativePath(projectRoot, document.FilePath).Replace('\\', '/');
                    documents.Add(new CSharpAnalysisDocument(root, model, relative, $"file:{relative}"));
                }
            }

            if (documents.Count == 0 || compilations.Count == 0)
                return null;
            var snapshot = new CSharpWorkspaceSnapshot(documents, compilations, diagnostics);
            StoreCache(normalizedRoot, inputFingerprint, snapshot);
            logger.LogInformation(
                "MSBuild workspace loaded: {Projects} compilations, {Documents} documents, {Diagnostics} diagnostics",
                compilations.Count, documents.Count, diagnostics.Count);
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MSBuild workspace loading failed; using conservative synthetic compilation fallback");
            return null;
        }
        finally
        {
            WorkspaceGate.Release();
        }
    }

    /// <summary>
    /// Applies body-only text changes to the cached immutable Roslyn compilations.
    /// Any declaration-surface change returns an explicit escalation reason without
    /// mutating the cache. This is the correctness gate for fast incremental indexing.
    /// </summary>
    public static async Task<CSharpBodyUpdate> TryApplyBodyChangesAsync(
        string projectRoot,
        IReadOnlyList<CSharpBodyChange> changes,
        string updatedInputFingerprint,
        ILogger logger,
        CancellationToken ct)
    {
        var normalizedRoot = Path.GetFullPath(projectRoot);
        await WorkspaceGate.WaitAsync(ct);
        try
        {
            if (!Caches.TryGetValue(normalizedRoot, out var cached))
            {
                return new(false, "Roslyn workspace cache is unavailable for this project.", [], []);
            }
            Caches[normalizedRoot] = cached = cached with { AccessSequence = ++_accessSequence };

            var replacements = new Dictionary<SyntaxTree, (SyntaxTree Tree, SyntaxNode Root, string RelativePath)>();
            foreach (var change in changes)
            {
                ct.ThrowIfCancellationRequested();
                var matching = cached.Snapshot.Documents
                    .Where(document => string.Equals(
                        document.RelativePath,
                        Normalize(change.RelativePath),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matching.Count == 0)
                    return new(false, $"Changed C# document is not part of the loaded workspace: {change.RelativePath}", [], []);

                foreach (var document in matching)
                {
                    var oldTree = document.Root.SyntaxTree;
                    var newText = SourceText.From(change.Text, oldTree.Encoding ?? Encoding.UTF8);
                    var newTree = oldTree.WithChangedText(newText);
                    var newRoot = await newTree.GetRootAsync(ct);
                    if (!string.Equals(
                        DeclarationSurface(document.Root),
                        DeclarationSurface(newRoot),
                        StringComparison.Ordinal))
                    {
                        return new(false,
                            $"Declaration surface changed: {change.RelativePath}", [], []);
                    }
                    replacements[oldTree] = (newTree, newRoot, Normalize(change.RelativePath));
                }
            }

            var compilations = cached.Snapshot.Compilations.ToArray();
            var affectedIndexes = new HashSet<int>();
            foreach (var (oldTree, replacement) in replacements)
            {
                var index = Array.FindIndex(compilations,
                    compilation => compilation.SyntaxTrees.Contains(oldTree));
                if (index < 0)
                    return new(false,
                        $"Changed syntax tree is not part of a cached compilation: {replacement.RelativePath}", [], []);
                compilations[index] = compilations[index].ReplaceSyntaxTree(oldTree, replacement.Tree);
                affectedIndexes.Add(index);
            }

            var documents = new List<CSharpAnalysisDocument>(cached.Snapshot.Documents.Count);
            var changedDocuments = new List<CSharpAnalysisDocument>();
            foreach (var document in cached.Snapshot.Documents)
            {
                if (!replacements.TryGetValue(document.Root.SyntaxTree, out var replacement))
                {
                    documents.Add(document);
                    continue;
                }

                var compilation = compilations.First(item => item.SyntaxTrees.Contains(replacement.Tree));
                var updated = new CSharpAnalysisDocument(
                    replacement.Root,
                    compilation.GetSemanticModel(replacement.Tree),
                    replacement.RelativePath,
                    $"file:{replacement.RelativePath}");
                documents.Add(updated);
                changedDocuments.Add(updated);
            }

            var updatedSnapshot = new CSharpWorkspaceSnapshot(
                documents,
                compilations,
                cached.Snapshot.Diagnostics,
                cached.Snapshot.IsSynthetic);
            StoreCache(normalizedRoot, updatedInputFingerprint, updatedSnapshot);
            logger.LogInformation(
                "Applied {Count} body-only C# changes to {Compilations} cached compilations",
                changes.Count,
                affectedIndexes.Count);
            return new(true, null, changedDocuments,
                affectedIndexes.OrderBy(index => index).Select(index => compilations[index]).ToList(),
                updatedSnapshot.IsSynthetic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Incremental Roslyn workspace update failed; escalating to full index");
            return new(false, $"Roslyn incremental update failed: {ex.Message}", [], []);
        }
        finally
        {
            WorkspaceGate.Release();
        }
    }

    public static async Task RememberSyntheticAsync(
        string projectRoot,
        string inputFingerprint,
        IReadOnlyList<CSharpAnalysisDocument> documents,
        Compilation compilation,
        CancellationToken ct)
    {
        await WorkspaceGate.WaitAsync(ct);
        try
        {
            StoreCache(
                Path.GetFullPath(projectRoot),
                inputFingerprint,
                new CSharpWorkspaceSnapshot(
                    documents,
                    [compilation],
                    ["Synthetic compilation fallback"],
                    IsSynthetic: true));
        }
        finally
        {
            WorkspaceGate.Release();
        }
    }

    private static string DeclarationSurface(SyntaxNode root)
    {
        var excluded = root.DescendantNodes().SelectMany(node => node switch
            {
                BaseMethodDeclarationSyntax method => BodySpans(method.Body, method.ExpressionBody),
                AccessorDeclarationSyntax accessor => BodySpans(accessor.Body, accessor.ExpressionBody),
                LocalFunctionStatementSyntax local => BodySpans(local.Body, local.ExpressionBody),
                _ => [],
            })
            .OrderBy(span => span.Start)
            .ToArray();
        var builder = new StringBuilder();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            if (excluded.Any(span => span.Contains(token.Span))) continue;
            builder.Append(token.RawKind).Append(':').Append(token.ValueText).Append('\0');
        }
        return builder.ToString();

        static IEnumerable<TextSpan> BodySpans(BlockSyntax? body, ArrowExpressionClauseSyntax? expression)
        {
            if (body is not null) yield return body.Span;
            if (expression is not null) yield return expression.Span;
        }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (MSBuildLocator.IsRegistered) return;
        lock (RegistrationGate)
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
        }
    }

    private static IEnumerable<string> Enumerate(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is ".git" or "bin" or "obj" or "node_modules"))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateBuildInputs(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var paths = new List<string>();
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> files;
            IEnumerable<string> subdirectories;
            try
            {
                files = Directory.EnumerateFiles(directory);
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (BuildFileNames.Contains(name) || BuildFileExtensions.Contains(Path.GetExtension(file)))
                    paths.Add(file);
            }
            foreach (var child in subdirectories)
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or ".vs" or "bin" or "obj" or "node_modules" or "packages")
                    continue;
                pending.Push(child);
            }
        }
        return paths.OrderBy(path => Normalize(Path.GetRelativePath(root, path)), StringComparer.Ordinal);
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void StoreCache(
        string projectRoot,
        string inputFingerprint,
        CSharpWorkspaceSnapshot snapshot)
    {
        Caches[projectRoot] = new WorkspaceCacheEntry(
            inputFingerprint,
            snapshot,
            ++_accessSequence);
        if (Caches.Count <= MaxCachedWorkspaces) return;
        var evicted = Caches
            .Where(pair => !string.Equals(pair.Key, projectRoot, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Value.AccessSequence)
            .First();
        Caches.Remove(evicted.Key);
    }

    private sealed record WorkspaceCacheEntry(
        string InputFingerprint,
        CSharpWorkspaceSnapshot Snapshot,
        long AccessSequence);
}
