using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.CodeGraph;
using AgentService.Infrastructure.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class ImpactAnalysisHelperTests
{
    [Theory]
    [InlineData("Acme.Orders.OrderService.CalculateTotal(int)", "OrderService")]
    [InlineData("Acme.Orders.OrderService", "OrderService")]
    [InlineData("OrderService", "OrderService")]
    public void ExtractTypeName_Works(string key, string expected)
    {
        Assert.Equal(expected, ImpactAnalysisService.ExtractTypeName(key));
    }

    [Fact]
    public async Task Analyze_RetriesWhenPublishWouldMixManifestVersions()
    {
        var searches = 0;
        var graph = new MemoryGraphStore("v2")
        {
            SearchHandler = (_, _, _, _) =>
            {
                var version = Interlocked.Increment(ref searches) == 1 ? "v1" : "v2";
                return Task.FromResult<IReadOnlyList<GraphSearchHit>>(
                    [Hit("Acme.Service.Run()", version)]);
            },
            ReverseCallChainHandler = (_, _, _, _) => Task.FromResult<IReadOnlyList<ImpactPath>>(
                [new ImpactPath([Hit("Acme.Service.Run()", "v2")], ManifestVersion: "v2")]),
            NeighborhoodHandler = (_, _, depth, _) => Task.FromResult(
                new GraphNeighborhood(Hit("Acme.Service.Run()", "v2"), [], Depth: depth)),
        };
        var repository = new MemoryProjectRepository(new ProjectEntity
        {
            Id = "project-1",
            Name = "Project",
            RootPath = "C:/repo",
            IndexManifestVersion = "v1",
            IndexStatus = ProjectIndexStatus.Indexed,
        });
        var service = new ImpactAnalysisService(
            graph,
            repository,
            NullLogger<ImpactAnalysisService>.Instance);

        var result = await service.AnalyzeAsync("project-1", "Run");

        Assert.Equal(2, searches);
        Assert.Equal("v2", result.ManifestVersion);
        Assert.Equal("v2", result.Target?.ManifestVersion);
    }

    private static GraphSearchHit Hit(string key, string version) => new(
        key,
        "Method",
        "Run",
        "void Run()",
        "Service.cs",
        1,
        1,
        ManifestVersion: version);
}

public sealed class VerificationServiceTests : IDisposable
{
    private readonly string _root;

    public VerificationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mw-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void DetectVerifyCommands_DotnetProject()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");
        var commands = VerificationService.DetectVerifyCommands(_root);
        Assert.Contains(commands, c => c.StartsWith("dotnet build"));
    }

    [Fact]
    public void DetectVerifyCommands_MavenProject()
    {
        File.WriteAllText(Path.Combine(_root, "pom.xml"), "<project />");
        var commands = VerificationService.DetectVerifyCommands(_root);
        Assert.Contains(commands, c => c.Contains("mvn"));
    }

    [Fact]
    public void DetectVerifyCommands_EmptyDir_ReturnsNone()
    {
        Assert.Empty(VerificationService.DetectVerifyCommands(_root));
    }

    [Fact]
    public void TruncateOutput_FailureKeepsTail()
    {
        var longError = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"error line {i}"));
        var output = VerificationService.TruncateOutput("", longError, success: false);
        Assert.True(output.Length <= 4001);
        Assert.Contains("error line 499", output); // 尾部保留（最相關的錯誤）
    }

    [Fact]
    public void TruncateOutput_SuccessKeepsSummary()
    {
        var stdout = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"line {i}"));
        var output = VerificationService.TruncateOutput(stdout, "", success: true);
        Assert.Contains("line 99", output);
        Assert.DoesNotContain("line 0\n", output);
    }
}

public sealed class AgentsMdFactsTests : IDisposable
{
    private readonly string _root;

    public AgentsMdFactsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mw-facts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void DetectsDotnetAndDocker()
    {
        File.WriteAllText(Path.Combine(_root, "App.sln"), "");
        File.WriteAllText(Path.Combine(_root, "Dockerfile"), "");
        var facts = AgentsMdGenerator.DetectProjectFacts(_root);
        Assert.Contains(".NET solution", facts);
        Assert.Contains("Dockerfile", facts);
    }

    [Fact]
    public void EmptyDirectory_ReportsNothing()
    {
        var facts = AgentsMdGenerator.DetectProjectFacts(_root);
        Assert.Contains("未偵測到", facts);
    }
}
