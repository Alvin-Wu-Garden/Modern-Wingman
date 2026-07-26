using AgentService.Infrastructure.Workflow;

namespace AgentService.UnitTests;

public sealed class VerificationServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"mw-verify-{Guid.NewGuid():N}");

    public VerificationServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void DetectVerifyCommands_DotnetProject()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");
        Assert.Contains(
            VerificationService.DetectVerifyCommands(_root),
            command => command.StartsWith("dotnet build", StringComparison.Ordinal));
    }

    [Fact]
    public void DetectVerifyCommands_MavenProject()
    {
        File.WriteAllText(Path.Combine(_root, "pom.xml"), "<project />");
        Assert.Contains(
            VerificationService.DetectVerifyCommands(_root),
            command => command.Contains("mvn", StringComparison.Ordinal));
    }

    [Fact]
    public void DetectVerifyCommands_EmptyDir_ReturnsNone() =>
        Assert.Empty(VerificationService.DetectVerifyCommands(_root));

    [Fact]
    public void TruncateOutput_FailureKeepsTail()
    {
        var error = string.Join(
            '\n', Enumerable.Range(0, 500).Select(index => $"error line {index}"));
        var output = VerificationService.TruncateOutput("", error, success: false);
        Assert.True(output.Length <= 4_001);
        Assert.Contains("error line 499", output);
    }
}
