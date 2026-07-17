using AgentService.Infrastructure.Marketplace;
using Wingman.Marketplace.Domain;

namespace AgentService.UnitTests.Marketplace;

public sealed class FolderArtifactResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wingman-marketplace-resolver-" + Guid.NewGuid().ToString("N"));
    public FolderArtifactResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ResolveFolder_FindsIndependentSkillAndMcpArtifacts()
    {
        var skill = Directory.CreateDirectory(Path.Combine(_root, "skills", "review"));
        await File.WriteAllTextAsync(Path.Combine(skill.FullName, "SKILL.md"), "---\nname: review\n---\n# Review");
        var mcp = Directory.CreateDirectory(Path.Combine(_root, "mcp"));
        await File.WriteAllTextAsync(Path.Combine(mcp.FullName, ".mcp.json"), "{\"mcpServers\":{\"demo\":{\"command\":\"demo\"}}}");

        var candidates = await new FolderArtifactResolver().ResolveFolderAsync(_root);

        Assert.Contains(candidates, candidate => candidate.Kind == MarketplaceArtifactKind.Skill && candidate.Status == MarketplaceDiscoveryStatus.Resolved);
        Assert.Contains(candidates, candidate => candidate.Kind == MarketplaceArtifactKind.McpServer && candidate.Status == MarketplaceDiscoveryStatus.Resolved);
    }

    [Fact]
    public async Task ResolveFolder_LeavesInvalidMcpForManualSetup()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".mcp.json"), "{\"unexpected\":true}");

        var candidates = await new FolderArtifactResolver().ResolveFolderAsync(_root);

        var candidate = Assert.Single(candidates);
        Assert.Equal(MarketplaceArtifactKind.McpServer, candidate.Kind);
        Assert.Equal(MarketplaceDiscoveryStatus.ManualSetupRequired, candidate.Status);
    }

    [Fact]
    public async Task ResolveFolder_RejectsSkillWithoutFrontmatter()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "SKILL.md"), "# Missing metadata");

        var candidate = Assert.Single(await new FolderArtifactResolver().ResolveFolderAsync(_root), item => item.Kind == MarketplaceArtifactKind.Skill);

        Assert.Equal(MarketplaceDiscoveryStatus.Invalid, candidate.Status);
        Assert.Contains("frontmatter", candidate.ValidationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveFolder_ValidatesCodexPluginNameVersionAndRelativeComponents()
    {
        var manifest = Directory.CreateDirectory(Path.Combine(_root, ".codex-plugin"));
        var skills = Directory.CreateDirectory(Path.Combine(_root, "skills", "hello"));
        await File.WriteAllTextAsync(Path.Combine(manifest.FullName, "plugin.json"), "{\"name\":\"demo-plugin\",\"version\":\"1.0.0\",\"skills\":\"./skills\"}");
        await File.WriteAllTextAsync(Path.Combine(_root, "wingman.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(skills.FullName, "SKILL.md"), "# Hello");

        var candidates = await new FolderArtifactResolver().ResolveFolderAsync(_root);

        Assert.Contains(candidates, candidate => candidate.Kind == MarketplaceArtifactKind.WingmanPlugin && candidate.Status == MarketplaceDiscoveryStatus.Resolved);
    }

    [Fact]
    public async Task ResolveFolder_RejectsPluginBinaryExtension()
    {
        var manifest = Directory.CreateDirectory(Path.Combine(_root, ".codex-plugin"));
        await File.WriteAllTextAsync(Path.Combine(manifest.FullName, "plugin.json"), "{\"name\":\"demo-plugin\",\"version\":\"1.0.0\"}");
        await File.WriteAllTextAsync(Path.Combine(_root, "wingman.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_root, "unsafe.dll"), "not-a-real-dll");

        var candidate = Assert.Single(await new FolderArtifactResolver().ResolveFolderAsync(_root), item => item.Kind == MarketplaceArtifactKind.WingmanPlugin);

        Assert.Equal(MarketplaceDiscoveryStatus.Invalid, candidate.Status);
        Assert.Contains("binary", candidate.ValidationMessage, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
