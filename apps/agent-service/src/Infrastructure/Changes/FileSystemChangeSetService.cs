using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.Tools;
using DiffPlex.Renderer;
using System.Text.RegularExpressions;

namespace AgentService.Infrastructure.Changes;

public sealed class FileSystemChangeSetService : IChangeSetService
{
    private static readonly string[] DefaultExcludedDirectories =
        [".git", ".svn", "node_modules", "bin", "obj", "target", "dist", ".vs"];

    private readonly string _checkpointRoot;
    private readonly HashSet<string> _excludedDirectories;
    private readonly long _maxSnapshotBytes;
    private readonly int _maxFileCount;

    public FileSystemChangeSetService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".Wingman",
            "checkpoints"), DefaultExcludedDirectories, 2L * 1024 * 1024 * 1024, 100_000)
    {
    }

    public FileSystemChangeSetService(IConfiguration configuration)
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".Wingman",
                "checkpoints"),
            ReadExcludedDirectories(configuration),
            configuration.GetValue("ChangeSets:MaxSnapshotBytes", 2L * 1024 * 1024 * 1024),
            configuration.GetValue("ChangeSets:MaxFileCount", 100_000))
    {
    }

    internal FileSystemChangeSetService(string checkpointRoot)
        : this(checkpointRoot, DefaultExcludedDirectories, 2L * 1024 * 1024 * 1024, 100_000)
    {
    }

    internal FileSystemChangeSetService(
        string checkpointRoot,
        IEnumerable<string> excludedDirectories,
        long maxSnapshotBytes,
        int maxFileCount)
    {
        _checkpointRoot = Path.GetFullPath(checkpointRoot);
        _excludedDirectories = excludedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _maxSnapshotBytes = Math.Max(1, maxSnapshotBytes);
        _maxFileCount = Math.Max(1, maxFileCount);
    }

    public async Task<string> CreateCheckpointAsync(
        string runId,
        string workspacePath,
        CancellationToken ct = default)
    {
        var workspace = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException(workspace);

        var workspaceFiles = EnumerateWorkspaceFiles(workspace).ToList();
        if (workspaceFiles.Count > _maxFileCount)
        {
            throw new InvalidOperationException(
                $"Workspace snapshot contains {workspaceFiles.Count:N0} files, exceeding the configured limit of {_maxFileCount:N0}. " +
                "Add generated folders to ChangeSets:ExcludedDirectories or increase ChangeSets:MaxFileCount before running the Agent.");
        }
        var snapshotBytes = workspaceFiles.Sum(path => new FileInfo(path).Length);
        if (snapshotBytes > _maxSnapshotBytes)
        {
            throw new InvalidOperationException(
                $"Workspace snapshot requires {snapshotBytes:N0} bytes, exceeding the configured limit of {_maxSnapshotBytes:N0}. " +
                "Add generated folders to ChangeSets:ExcludedDirectories or increase ChangeSets:MaxSnapshotBytes before running the Agent.");
        }

        var checkpointId = Guid.NewGuid().ToString("N");
        var checkpointDirectory = Path.Combine(_checkpointRoot, checkpointId);
        var filesDirectory = Path.Combine(checkpointDirectory, "files");
        Directory.CreateDirectory(filesDirectory);

        var manifest = new CheckpointManifest
        {
            Id = checkpointId,
            RunId = runId,
            WorkspacePath = workspace,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        foreach (var file in workspaceFiles)
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspace, file));
            var destination = Path.Combine(filesDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
            manifest.BaselineHashes[relativePath] = await HashFileAsync(file, ct);
        }

        await WriteManifestAsync(checkpointDirectory, manifest, ct);
        return checkpointId;
    }

    public async Task<ChangeSet> GetChangeSetAsync(
        string checkpointId,
        CancellationToken ct = default)
    {
        var checkpointDirectory = ResolveCheckpointDirectory(checkpointId);
        var manifest = await ReadManifestAsync(checkpointDirectory, ct);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateWorkspaceFiles(manifest.WorkspacePath))
        {
            var relativePath = NormalizeRelativePath(
                Path.GetRelativePath(manifest.WorkspacePath, file));
            current[relativePath] = await HashFileAsync(file, ct);
        }

        var paths = manifest.BaselineHashes.Keys
            .Concat(current.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        var changes = new List<ChangedFile>();
        foreach (var relativePath in paths)
        {
            manifest.BaselineHashes.TryGetValue(relativePath, out var baselineHash);
            current.TryGetValue(relativePath, out var currentHash);
            if (baselineHash == currentHash)
                continue;

            var kind = baselineHash is null
                ? ChangedFileKind.Added
                : currentHash is null
                    ? ChangedFileKind.Deleted
                    : ChangedFileKind.Modified;
            var baselinePath = Path.Combine(
                checkpointDirectory,
                "files",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var currentPath = WorkspacePathGuard.Resolve(manifest.WorkspacePath, relativePath);
            var binary = IsBinary(baselinePath) || IsBinary(currentPath);
            var unifiedDiff = binary ? null : BuildUnifiedDiff(relativePath, baselinePath, currentPath);
            changes.Add(new ChangedFile(
                relativePath,
                kind,
                baselineHash,
                currentHash,
                binary,
                unifiedDiff,
                Hunks: unifiedDiff is null ? null : ParseHunks(unifiedDiff)));
        }

        DetectRenames(changes);

        manifest.ComparisonHashes = current;
        await WriteManifestAsync(checkpointDirectory, manifest, ct);
        return new ChangeSet(
            manifest.Id,
            manifest.RunId,
            manifest.WorkspacePath,
            manifest.CreatedAt,
            changes);
    }

    public async Task<RestoreCheckpointResult> RestoreAsync(
        string checkpointId,
        bool force = false,
        CancellationToken ct = default)
    {
        var checkpointDirectory = ResolveCheckpointDirectory(checkpointId);
        var manifest = await ReadManifestAsync(checkpointDirectory, ct);
        if (manifest.ComparisonHashes is null)
            throw new InvalidOperationException("Generate the change set before restoring the checkpoint.");

        var paths = manifest.BaselineHashes.Keys
            .Concat(manifest.ComparisonHashes.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return await RestoreCoreAsync(checkpointDirectory, manifest, paths, force, ct);
    }

    public async Task<RestoreCheckpointResult> RestoreFilesAsync(
        string checkpointId,
        IReadOnlyCollection<string> relativePaths,
        bool force = false,
        CancellationToken ct = default)
    {
        var checkpointDirectory = ResolveCheckpointDirectory(checkpointId);
        var manifest = await ReadManifestAsync(checkpointDirectory, ct);
        if (manifest.ComparisonHashes is null)
            throw new InvalidOperationException("Generate the change set before restoring files.");
        var paths = ValidateSelectedPaths(manifest, relativePaths);
        return await RestoreCoreAsync(checkpointDirectory, manifest, paths, force, ct);
    }

    public async Task<RestoreCheckpointResult> AcceptFilesAsync(
        string checkpointId,
        IReadOnlyCollection<string> relativePaths,
        CancellationToken ct = default)
    {
        var checkpointDirectory = ResolveCheckpointDirectory(checkpointId);
        var manifest = await ReadManifestAsync(checkpointDirectory, ct);
        if (manifest.ComparisonHashes is null)
            throw new InvalidOperationException("Generate the change set before accepting files.");
        var accepted = new List<string>();
        var conflicts = new List<string>();
        foreach (var relativePath in ValidateSelectedPaths(manifest, relativePaths))
        {
            ct.ThrowIfCancellationRequested();
            var current = WorkspacePathGuard.Resolve(manifest.WorkspacePath, relativePath);
            manifest.ComparisonHashes.TryGetValue(relativePath, out var expectedHash);
            var actualHash = File.Exists(current) ? await HashFileAsync(current, ct) : null;
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(relativePath);
                continue;
            }
            var baseline = Path.Combine(checkpointDirectory, "files", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(current))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
                File.Copy(current, baseline, overwrite: true);
                manifest.BaselineHashes[relativePath] = actualHash!;
            }
            else
            {
                if (File.Exists(baseline)) File.Delete(baseline);
                manifest.BaselineHashes.Remove(relativePath);
            }
            accepted.Add(relativePath);
        }
        await WriteManifestAsync(checkpointDirectory, manifest, ct);
        return new RestoreCheckpointResult(conflicts.Count == 0, accepted, conflicts);
    }

    public async Task<RestoreCheckpointResult> ApplyToWorkspaceAsync(
        string checkpointId,
        string targetWorkspacePath,
        CancellationToken ct = default)
    {
        var checkpointDirectory = ResolveCheckpointDirectory(checkpointId);
        var manifest = await ReadManifestAsync(checkpointDirectory, ct);
        var targetRoot = Path.GetFullPath(targetWorkspacePath);
        if (!Directory.Exists(targetRoot))
            throw new DirectoryNotFoundException(targetRoot);

        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateWorkspaceFiles(manifest.WorkspacePath))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(manifest.WorkspacePath, file));
            current[relative] = await HashFileAsync(file, ct);
        }
        var changedPaths = manifest.BaselineHashes.Keys
            .Concat(current.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path =>
            {
                manifest.BaselineHashes.TryGetValue(path, out var baselineHash);
                current.TryGetValue(path, out var currentHash);
                return !string.Equals(baselineHash, currentHash, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        var conflicts = new List<string>();
        foreach (var relative in changedPaths)
        {
            var target = WorkspacePathGuard.Resolve(targetRoot, relative);
            manifest.BaselineHashes.TryGetValue(relative, out var expectedHash);
            var actualHash = File.Exists(target) ? await HashFileAsync(target, ct) : null;
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                conflicts.Add(relative);
        }
        if (conflicts.Count > 0)
            return new RestoreCheckpointResult(false, [], conflicts);

        var applied = new List<string>();
        foreach (var relative in changedPaths)
        {
            ct.ThrowIfCancellationRequested();
            var source = WorkspacePathGuard.Resolve(manifest.WorkspacePath, relative);
            var target = WorkspacePathGuard.Resolve(targetRoot, relative);
            if (File.Exists(source))
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException($"ChangeSet source cannot be a link: {relative}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
            applied.Add(relative);
        }
        return new RestoreCheckpointResult(true, applied, []);
    }

    public async Task<RestoreCheckpointResult> RestoreHunksAsync(
        string checkpointId,
        string relativePath,
        IReadOnlyCollection<int> hunkIndexes,
        CancellationToken ct = default)
    {
        if (hunkIndexes.Count == 0)
            throw new ArgumentException("At least one hunk must be selected.", nameof(hunkIndexes));
        var checkpointDirectory = ResolveCheckpointDirectory(checkpointId);
        var manifest = await ReadManifestAsync(checkpointDirectory, ct);
        var normalized = NormalizeRelativePath(relativePath);
        var currentPath = WorkspacePathGuard.Resolve(manifest.WorkspacePath, normalized);
        var baselinePath = Path.Combine(checkpointDirectory, "files", normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(currentPath) || !File.Exists(baselinePath) || IsBinary(currentPath) || IsBinary(baselinePath))
            return new RestoreCheckpointResult(false, [], [normalized]);

        if (manifest.ComparisonHashes?.TryGetValue(normalized, out var reviewedHash) == true)
        {
            var actualHash = await HashFileAsync(currentPath, ct);
            if (!string.Equals(reviewedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                return new RestoreCheckpointResult(false, [], [normalized]);
        }

        var diff = BuildUnifiedDiff(normalized, baselinePath, currentPath);
        var hunks = ParseHunks(diff).Where(hunk => hunkIndexes.Contains(hunk.Index))
            .OrderByDescending(hunk => hunk.NewStart)
            .ToList();
        if (hunks.Count != hunkIndexes.Distinct().Count())
            return new RestoreCheckpointResult(false, [], [normalized]);

        var lines = ReadTextLines(currentPath);
        foreach (var hunk in hunks)
        {
            var expected = hunk.Lines.Where(line => line.Length > 0 && line[0] is ' ' or '+')
                .Select(line => line[1..]).ToList();
            var replacement = hunk.Lines.Where(line => line.Length > 0 && line[0] is ' ' or '-')
                .Select(line => line[1..]).ToList();
            var start = Math.Max(0, hunk.NewStart - 1);
            if (start + expected.Count > lines.Count || !lines.Skip(start).Take(expected.Count).SequenceEqual(expected))
                return new RestoreCheckpointResult(false, [], [normalized]);
            lines.RemoveRange(start, expected.Count);
            lines.InsertRange(start, replacement);
        }
        await File.WriteAllTextAsync(currentPath, string.Join(Environment.NewLine, lines), ct);
        return new RestoreCheckpointResult(true, [normalized], []);
    }

    public async Task<RestoreCheckpointResult> AcceptHunksAsync(
        string checkpointId,
        string relativePath,
        IReadOnlyCollection<int> hunkIndexes,
        CancellationToken ct = default)
    {
        if (hunkIndexes.Count == 0)
            throw new ArgumentException("At least one hunk must be selected.", nameof(hunkIndexes));
        var checkpointDirectory = ResolveCheckpointDirectory(checkpointId);
        var manifest = await ReadManifestAsync(checkpointDirectory, ct);
        var normalized = NormalizeRelativePath(relativePath);
        var currentPath = WorkspacePathGuard.Resolve(manifest.WorkspacePath, normalized);
        var baselinePath = Path.Combine(checkpointDirectory, "files", normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(currentPath) || !File.Exists(baselinePath) || IsBinary(currentPath) || IsBinary(baselinePath))
            return new RestoreCheckpointResult(false, [], [normalized]);
        if (manifest.ComparisonHashes?.TryGetValue(normalized, out var reviewedHash) == true &&
            !string.Equals(reviewedHash, await HashFileAsync(currentPath, ct), StringComparison.OrdinalIgnoreCase))
            return new RestoreCheckpointResult(false, [], [normalized]);

        var hunks = ParseHunks(BuildUnifiedDiff(normalized, baselinePath, currentPath))
            .Where(hunk => hunkIndexes.Contains(hunk.Index))
            .OrderByDescending(hunk => hunk.OldStart)
            .ToList();
        if (hunks.Count != hunkIndexes.Distinct().Count())
            return new RestoreCheckpointResult(false, [], [normalized]);

        var lines = ReadTextLines(baselinePath);
        foreach (var hunk in hunks)
        {
            var expected = hunk.Lines.Where(line => line.Length > 0 && line[0] is ' ' or '-')
                .Select(line => line[1..]).ToList();
            var replacement = hunk.Lines.Where(line => line.Length > 0 && line[0] is ' ' or '+')
                .Select(line => line[1..]).ToList();
            var start = Math.Max(0, hunk.OldStart - 1);
            if (start + expected.Count > lines.Count || !lines.Skip(start).Take(expected.Count).SequenceEqual(expected))
                return new RestoreCheckpointResult(false, [], [normalized]);
            lines.RemoveRange(start, expected.Count);
            lines.InsertRange(start, replacement);
        }
        await File.WriteAllTextAsync(baselinePath, string.Join(Environment.NewLine, lines), ct);
        manifest.BaselineHashes[normalized] = await HashFileAsync(baselinePath, ct);
        await WriteManifestAsync(checkpointDirectory, manifest, ct);
        return new RestoreCheckpointResult(true, [normalized], []);
    }

    private static async Task<RestoreCheckpointResult> RestoreCoreAsync(
        string checkpointDirectory,
        CheckpointManifest manifest,
        IReadOnlyCollection<string> paths,
        bool force,
        CancellationToken ct)
    {
        var restored = new List<string>();
        var conflicts = new List<string>();
        foreach (var relativePath in paths)
        {
            ct.ThrowIfCancellationRequested();
            var target = WorkspacePathGuard.Resolve(manifest.WorkspacePath, relativePath);
            manifest.ComparisonHashes!.TryGetValue(relativePath, out var expectedCurrentHash);
            var actualCurrentHash = File.Exists(target)
                ? await HashFileAsync(target, ct)
                : null;
            if (!force && !string.Equals(
                    expectedCurrentHash,
                    actualCurrentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(relativePath);
                continue;
            }

            if (manifest.BaselineHashes.ContainsKey(relativePath))
            {
                var source = Path.Combine(
                    checkpointDirectory,
                    "files",
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
            restored.Add(relativePath);
        }

        return new RestoreCheckpointResult(conflicts.Count == 0, restored, conflicts);
    }

    private static IReadOnlyList<string> ValidateSelectedPaths(
        CheckpointManifest manifest,
        IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            throw new ArgumentException("At least one file must be selected.", nameof(paths));
        var known = manifest.BaselineHashes.Keys
            .Concat(manifest.ComparisonHashes?.Keys.AsEnumerable() ?? Enumerable.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths)
        {
            var normalized = NormalizeRelativePath(path);
            WorkspacePathGuard.Resolve(manifest.WorkspacePath, normalized);
            if (!known.Contains(normalized))
                throw new InvalidOperationException($"File is not part of the checkpoint: {normalized}");
            result.Add(normalized);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string ResolveCheckpointDirectory(string checkpointId)
    {
        if (checkpointId.Length != 32 || checkpointId.Any(ch => !Uri.IsHexDigit(ch)))
            throw new ArgumentException("Invalid checkpoint identifier.", nameof(checkpointId));
        var directory = Path.GetFullPath(Path.Combine(_checkpointRoot, checkpointId));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);
        return directory;
    }

    private IEnumerable<string> EnumerateWorkspaceFiles(string workspacePath)
    {
        var pending = new Stack<string>();
        pending.Push(workspacePath);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!_excludedDirectories.Contains(Path.GetFileName(child)))
                    pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
                yield return file;
        }
    }

    private static void DetectRenames(List<ChangedFile> changes)
    {
        var deleted = changes.Where(change => change.Kind == ChangedFileKind.Deleted).ToList();
        foreach (var added in changes.Where(change => change.Kind == ChangedFileKind.Added).ToList())
        {
            var source = deleted.FirstOrDefault(candidate =>
                candidate.BaselineHash is not null &&
                string.Equals(candidate.BaselineHash, added.CurrentHash, StringComparison.OrdinalIgnoreCase));
            if (source is null)
                continue;

            changes.Remove(source);
            changes.Remove(added);
            changes.Add(added with
            {
                Kind = ChangedFileKind.Renamed,
                BaselineHash = source.BaselineHash,
                OriginalPath = source.RelativePath,
            });
            deleted.Remove(source);
        }
        changes.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
    }

    private static IReadOnlyList<string> ReadExcludedDirectories(IConfiguration configuration)
    {
        var configured = configuration["ChangeSets:ExcludedDirectories"]?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return configured is { Length: > 0 }
            ? DefaultExcludedDirectories.Concat(configured).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : DefaultExcludedDirectories;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsBinary(string path)
    {
        if (!File.Exists(path))
            return false;
        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(8192, (int)Math.Min(stream.Length, int.MaxValue))];
        var count = stream.Read(buffer);
        return buffer.AsSpan(0, count).Contains((byte)0);
    }

    private static string BuildUnifiedDiff(string relativePath, string baseline, string current)
    {
        var before = File.Exists(baseline) ? File.ReadAllText(baseline) : "";
        var after = File.Exists(current) ? File.ReadAllText(current) : "";
        return UnidiffRenderer.GenerateUnidiff(before, after, $"a/{relativePath}", $"b/{relativePath}");
    }

    private static IReadOnlyList<DiffHunk> ParseHunks(string diff)
    {
        var result = new List<DiffHunk>();
        var header = new Regex("^@@ -(\\d+)(?:,(\\d+))? \\+(\\d+)(?:,(\\d+))? @@", RegexOptions.CultureInvariant);
        DiffHunk? current = null;
        foreach (var line in diff.Split('\n').Select(value => value.TrimEnd('\r')))
        {
            var match = header.Match(line);
            if (match.Success)
            {
                current = new DiffHunk(
                    result.Count,
                    int.Parse(match.Groups[1].Value),
                    match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1,
                    int.Parse(match.Groups[3].Value),
                    match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1,
                    new List<string>());
                result.Add(current);
                continue;
            }
            if (current?.Lines is List<string> lines && line.Length > 0 && line[0] is ' ' or '+' or '-')
                lines.Add(line);
        }
        return result;
    }

    private static List<string> ReadTextLines(string path) => File.ReadAllText(path)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n')
        .ToList();

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static Task WriteManifestAsync(
        string checkpointDirectory,
        CheckpointManifest manifest,
        CancellationToken ct) => File.WriteAllTextAsync(
        Path.Combine(checkpointDirectory, "manifest.json"),
        JsonSerializer.Serialize(manifest, JsonOptions),
        ct);

    private static async Task<CheckpointManifest> ReadManifestAsync(
        string checkpointDirectory,
        CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(
            Path.Combine(checkpointDirectory, "manifest.json"),
            ct);
        return JsonSerializer.Deserialize<CheckpointManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Invalid checkpoint manifest.");
    }

    private sealed class CheckpointManifest
    {
        public string Id { get; set; } = "";
        public string RunId { get; set; } = "";
        public string WorkspacePath { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public Dictionary<string, string> BaselineHashes { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string>? ComparisonHashes { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
