using AgentService.Infrastructure.CodeGraph;

namespace AgentService.UnitTests;

public sealed class ProjectIndexWatcherServiceTests
{
    [Theory]
    [InlineData(@"C:\repo\src\OrderService.cs")]
    [InlineData(@"C:\repo\src\com\acme\OrderService.java")]
    [InlineData(@"C:\repo\src\appsettings.json")]
    [InlineData(@"C:\repo\db\V001__orders.sql")]
    [InlineData(@"C:\repo\pom.xml")]
    public void RelevantSourcePath_AcceptsSupportedSourceFiles(string path) =>
        Assert.True(ProjectIndexWatcherService.IsRelevantSourcePath(path));

    [Theory]
    [InlineData(@"C:\repo\src\readme.md")]
    [InlineData(@"C:\repo\bin\Debug\App.cs")]
    [InlineData(@"C:\repo\node_modules\package\index.java")]
    [InlineData(@"C:\repo\.git\hooks\hook.cs")]
    public void RelevantSourcePath_RejectsIgnoredOrUnsupportedFiles(string path) =>
        Assert.False(ProjectIndexWatcherService.IsRelevantSourcePath(path));

    [Fact]
    public void RelevantPath_IsNotRejectedWhenProjectRootContainsIgnoredSegmentName()
    {
        var root = Path.Combine(Path.GetTempPath(), "build", "wingman-project");
        var path = Path.Combine(root, "src", "Service.cs");

        Assert.True(ProjectIndexWatcherService.IsRelevantSourcePath(path, root));
    }
}
