using AgentService.Application.Models;
using AgentService.Infrastructure.Changes;

namespace AgentService.UnitTests;

public sealed class FileSystemChangeSetServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wingman-changeset-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _checkpoints;

    public FileSystemChangeSetServiceTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _checkpoints = Path.Combine(_root, "checkpoints");
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public async Task ChangeSet_DetectsAndRestoresAddedModifiedDeletedFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "modified.txt"), "before\n");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "deleted.txt"), "restore me\n");
        var service = new FileSystemChangeSetService(_checkpoints);
        var checkpoint = await service.CreateCheckpointAsync("run-1", _workspace);

        await File.WriteAllTextAsync(Path.Combine(_workspace, "modified.txt"), "after\n");
        File.Delete(Path.Combine(_workspace, "deleted.txt"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, "added.txt"), "new\n");

        var changes = await service.GetChangeSetAsync(checkpoint);
        Assert.Equal(3, changes.Files.Count);
        Assert.Contains(changes.Files, x => x.RelativePath == "modified.txt" && x.Kind == ChangedFileKind.Modified);
        Assert.Contains(changes.Files, x => x.RelativePath == "deleted.txt" && x.Kind == ChangedFileKind.Deleted);
        Assert.Contains(changes.Files, x => x.RelativePath == "added.txt" && x.Kind == ChangedFileKind.Added);

        var restored = await service.RestoreAsync(checkpoint);
        Assert.True(restored.Success);
        Assert.Equal("before\n", await File.ReadAllTextAsync(Path.Combine(_workspace, "modified.txt")));
        Assert.Equal("restore me\n", await File.ReadAllTextAsync(Path.Combine(_workspace, "deleted.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace, "added.txt")));
    }

    [Fact]
    public async Task Restore_RefusesToOverwriteChangesMadeAfterReview()
    {
        var path = Path.Combine(_workspace, "file.txt");
        await File.WriteAllTextAsync(path, "baseline");
        var service = new FileSystemChangeSetService(_checkpoints);
        var checkpoint = await service.CreateCheckpointAsync("run-1", _workspace);
        await File.WriteAllTextAsync(path, "agent change");
        await service.GetChangeSetAsync(checkpoint);
        await File.WriteAllTextAsync(path, "user change after review");

        var result = await service.RestoreAsync(checkpoint);

        Assert.False(result.Success);
        Assert.Contains("file.txt", result.Conflicts);
        Assert.Equal("user change after review", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Checkpoint_ExcludesVersionControlAndBuildDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, ".git"));
        Directory.CreateDirectory(Path.Combine(_workspace, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".git", "config"), "ignored");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "node_modules", "x.js"), "ignored");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "source.cs"), "tracked");
        var service = new FileSystemChangeSetService(_checkpoints);
        var checkpoint = await service.CreateCheckpointAsync("run-1", _workspace);

        await File.WriteAllTextAsync(Path.Combine(_workspace, ".git", "config"), "changed");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "source.cs"), "changed");
        var changes = await service.GetChangeSetAsync(checkpoint);

        Assert.Single(changes.Files);
        Assert.Equal("source.cs", changes.Files[0].RelativePath);
    }

    [Fact]
    public async Task ChangeSet_AcceptsOneFileAndRestoresAnother()
    {
        var kept = Path.Combine(_workspace, "kept.txt");
        var discarded = Path.Combine(_workspace, "discarded.txt");
        await File.WriteAllTextAsync(kept, "before");
        await File.WriteAllTextAsync(discarded, "before");
        var service = new FileSystemChangeSetService(_checkpoints);
        var checkpoint = await service.CreateCheckpointAsync("run-1", _workspace);
        await File.WriteAllTextAsync(kept, "accepted");
        await File.WriteAllTextAsync(discarded, "discarded");
        await service.GetChangeSetAsync(checkpoint);

        Assert.True((await service.AcceptFilesAsync(checkpoint, ["kept.txt"])).Success);
        Assert.True((await service.RestoreFilesAsync(checkpoint, ["discarded.txt"])).Success);
        var remaining = await service.GetChangeSetAsync(checkpoint);

        Assert.Empty(remaining.Files);
        Assert.Equal("accepted", await File.ReadAllTextAsync(kept));
        Assert.Equal("before", await File.ReadAllTextAsync(discarded));
    }

    [Fact]
    public async Task ChangeSet_RecognizesRenameByContentHash()
    {
        var original = Path.Combine(_workspace, "before.txt");
        var renamed = Path.Combine(_workspace, "after.txt");
        await File.WriteAllTextAsync(original, "same content");
        var service = new FileSystemChangeSetService(_checkpoints);
        var checkpoint = await service.CreateCheckpointAsync("run-rename", _workspace);

        File.Move(original, renamed);
        var change = Assert.Single((await service.GetChangeSetAsync(checkpoint)).Files);

        Assert.Equal(ChangedFileKind.Renamed, change.Kind);
        Assert.Equal("before.txt", change.OriginalPath);
        Assert.Equal("after.txt", change.RelativePath);
    }

    [Fact]
    public async Task Checkpoint_RejectsOversizedWorkspaceBeforeCreatingSnapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "large.txt"), "12345");
        var service = new FileSystemChangeSetService(
            _checkpoints,
            [],
            maxSnapshotBytes: 4,
            maxFileCount: 100);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateCheckpointAsync("run-large", _workspace));

        Assert.Contains("ChangeSets:MaxSnapshotBytes", error.Message);
        Assert.False(Directory.Exists(_checkpoints));
    }

    [Fact]
    public async Task ApplyToWorkspace_CopiesOnlyAgentDeltaOverMatchingBaseline()
    {
        var target = Path.Combine(_root, "source");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "existing.txt"), "dirty baseline");
        await File.WriteAllTextAsync(Path.Combine(target, "existing.txt"), "dirty baseline");
        var service = new FileSystemChangeSetService(_checkpoints);
        var checkpoint = await service.CreateCheckpointAsync("run-apply", _workspace);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "existing.txt"), "agent result");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "new.txt"), "new agent file");

        var result = await service.ApplyToWorkspaceAsync(checkpoint, target);

        Assert.True(result.Success);
        Assert.Equal("agent result", await File.ReadAllTextAsync(Path.Combine(target, "existing.txt")));
        Assert.Equal("new agent file", await File.ReadAllTextAsync(Path.Combine(target, "new.txt")));
    }

    [Fact]
    public async Task ApplyToWorkspace_RefusesFilesChangedInSourceAfterBaseline()
    {
        var target = Path.Combine(_root, "source-conflict");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "file.txt"), "baseline");
        await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "baseline");
        var service = new FileSystemChangeSetService(_checkpoints);
        var checkpoint = await service.CreateCheckpointAsync("run-conflict", _workspace);
        await File.WriteAllTextAsync(Path.Combine(_workspace, "file.txt"), "agent result");
        await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "user changed again");

        var result = await service.ApplyToWorkspaceAsync(checkpoint, target);

        Assert.False(result.Success);
        Assert.Contains("file.txt", result.Conflicts);
        Assert.Equal("user changed again", await File.ReadAllTextAsync(Path.Combine(target, "file.txt")));
    }

    [Fact]
    public async Task ChangeSet_AcceptsAndRestoresIndividualHunks()
    {
        var path = Path.Combine(_workspace, "multi.txt");
        var baseline = Enumerable.Range(1, 20).Select(index => $"line {index}").ToArray();
        await File.WriteAllLinesAsync(path, baseline);
        var service = new FileSystemChangeSetService(_checkpoints);
        var checkpoint = await service.CreateCheckpointAsync("run-hunks", _workspace);
        var changed = baseline.ToArray();
        changed[1] = "agent first";
        changed[17] = "agent second";
        await File.WriteAllLinesAsync(path, changed);

        var file = Assert.Single((await service.GetChangeSetAsync(checkpoint)).Files);
        Assert.Equal(2, file.Hunks?.Count);
        Assert.True((await service.AcceptHunksAsync(checkpoint, "multi.txt", [0])).Success);
        file = Assert.Single((await service.GetChangeSetAsync(checkpoint)).Files);
        Assert.Single(file.Hunks!);
        var restoredHunk = await service.RestoreHunksAsync(checkpoint, "multi.txt", [0]);
        Assert.True(restoredHunk.Success, $"Hunk restore conflicts: {string.Join(", ", restoredHunk.Conflicts)}\n{file.UnifiedDiff}");

        Assert.Empty((await service.GetChangeSetAsync(checkpoint)).Files);
        var final = await File.ReadAllLinesAsync(path);
        Assert.Equal("agent first", final[1]);
        Assert.Equal("line 18", final[17]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
