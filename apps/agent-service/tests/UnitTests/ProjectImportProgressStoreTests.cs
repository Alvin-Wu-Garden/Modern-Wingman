using AgentService.Infrastructure.VersionControl;

namespace AgentService.UnitTests;

public sealed class ProjectImportProgressStoreTests
{
    [Fact]
    public void TracksOutputAndTerminalStates()
    {
        var store = new ProjectImportProgressStore();
        var started = store.Begin("operation-1", "git");

        Assert.Equal("running", started.Status);
        store.Report("operation-1", true, "remote: receiving objects");
        Assert.True(store.Get("operation-1")!.IsError);

        store.Complete("operation-1", "done");
        var completed = store.Get("operation-1")!;
        Assert.Equal("completed", completed.Status);
        Assert.False(completed.IsError);
        Assert.Equal("done", completed.Message);
    }

    [Fact]
    public void TracksCancellation()
    {
        var store = new ProjectImportProgressStore();
        store.Begin("operation-2", "svn");
        store.Cancel("operation-2");

        Assert.Equal("cancelled", store.Get("operation-2")!.Status);
    }
}
