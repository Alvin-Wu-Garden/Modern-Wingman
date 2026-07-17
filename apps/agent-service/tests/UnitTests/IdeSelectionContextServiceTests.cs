using AgentService.Infrastructure.Context;

namespace AgentService.UnitTests;

public sealed class IdeSelectionContextServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wingman-ide-selection-" + Guid.NewGuid().ToString("N"));

    public IdeSelectionContextServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReadsServerSideLinesAndMarksThemUntrusted()
    {
        await File.WriteAllLinesAsync(Path.Combine(_root, "source.cs"), ["one", "two", "three"]);
        var result = await new IdeSelectionContextService().ReadAsync(_root, "source.cs", 2, 3);

        Assert.Equal("two" + Environment.NewLine + "three", result.Content);
        Assert.Contains("<external-content trust=\"untrusted\"", result.UntrustedContent);
        Assert.Contains("two", result.UntrustedContent);
    }

    [Fact]
    public async Task RejectsTraversalAndOversizedRanges()
    {
        var service = new IdeSelectionContextService();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(_root, "../secret.txt", 1, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadAsync(_root, "source.cs", 1, 501));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
