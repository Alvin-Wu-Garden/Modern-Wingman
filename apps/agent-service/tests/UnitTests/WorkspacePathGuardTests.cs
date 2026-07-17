using AgentService.Infrastructure.Tools;

namespace AgentService.UnitTests;

public sealed class WorkspacePathGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wingman-path-test-" + Guid.NewGuid().ToString("N"));

    public WorkspacePathGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Resolve_AllowsRelativePathWithinWorkspace()
    {
        var result = WorkspacePathGuard.Resolve(_root, Path.Combine("src", "file.cs"));
        Assert.StartsWith(Path.GetFullPath(_root), result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_RejectsTraversalOutsideWorkspace()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            WorkspacePathGuard.Resolve(_root, Path.Combine("..", "secret.txt")));
    }

    [Theory]
    [InlineData(".git-credentials")]
    [InlineData(".npmrc")]
    [InlineData("id_rsa")]
    [InlineData("Login Data")]
    public void ResolveReadable_RejectsCredentialFiles(string fileName)
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            WorkspacePathGuard.ResolveReadable(_root, fileName));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
