using AgentService.Infrastructure.Marketplace;
using Wingman.Marketplace.Domain;

namespace AgentService.UnitTests.Marketplace;

/// <summary>鎖定十二個核定 target 的名稱、scope 與官方設定路徑，避免日後悄悄漂移。</summary>
public sealed class AgentTargetAdaptersTests : IDisposable
{
    private readonly string _projectRoot =
        Path.Combine(Path.GetTempPath(), $"wingman-targets-{Guid.NewGuid():N}");

    public AgentTargetAdaptersTests() => Directory.CreateDirectory(_projectRoot);

    [Fact]
    public void Create_ReturnsExactlyTheApprovedTwelveTargets()
    {
        var targets = BuiltInAgentTargets.Create();

        Assert.Equal(
            [
                "antigravity",
                "claude-code",
                "cline",
                "codex",
                "cursor",
                "gemini-cli",
                "github-copilot",
                "grok",
                "kilo-code",
                "opencode",
                "roo-code",
                "vscode",
            ],
            targets.Select(target => target.Descriptor.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
        Assert.All(targets, target => Assert.True(target.Descriptor.SupportsSkill));
    }

    [Theory]
    [InlineData("codex", ".agents/skills")]
    [InlineData("github-copilot", ".github/skills")]
    [InlineData("vscode", ".github/skills")]
    [InlineData("opencode", ".opencode/skills")]
    [InlineData("antigravity", ".agents/skills")]
    public void ProjectSkillPath_UsesVerifiedDirectory(string targetId, string suffix)
    {
        var target = BuiltInAgentTargets.Create()
            .Single(item => item.Descriptor.Id == targetId);

        var actual = target.ResolveSkillDirectory(
            MarketplaceDeploymentScope.Project,
            _projectRoot);

        Assert.EndsWith(
            suffix.Replace('/', Path.DirectorySeparatorChar),
            actual,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("vscode", ".vscode/mcp.json", "servers")]
    [InlineData("github-copilot", ".mcp.json", "mcpServers")]
    [InlineData("cline", ".cline/mcp.json", "mcpServers")]
    [InlineData("roo-code", ".roo/mcp.json", "mcpServers")]
    [InlineData("antigravity", ".agents/mcp_config.json", "mcpServers")]
    public void ProjectMcpPath_UsesVerifiedJsonFormat(
        string targetId,
        string suffix,
        string rootProperty)
    {
        var target = BuiltInAgentTargets.Create()
            .Single(item => item.Descriptor.Id == targetId);

        var actual = target.ResolveMcpConfigPath(
            MarketplaceDeploymentScope.Project,
            _projectRoot);

        Assert.EndsWith(
            suffix.Replace('/', Path.DirectorySeparatorChar),
            actual,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(rootProperty, target.McpRootProperty);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("kilo-code")]
    [InlineData("opencode")]
    [InlineData("grok")]
    public void NonStandardOrUnverifiedMcpFormat_IsNotAdvertised(string targetId)
    {
        var target = BuiltInAgentTargets.Create()
            .Single(item => item.Descriptor.Id == targetId);

        Assert.False(target.Descriptor.SupportsMcp);
    }

    [Fact]
    public void McpScopeCapabilities_DoNotAdvertiseMissingConfigurationPath()
    {
        var targets = BuiltInAgentTargets.Create()
            .ToDictionary(item => item.Descriptor.Id, StringComparer.Ordinal);

        Assert.False(targets["roo-code"].Descriptor.SupportsGlobalMcp);
        Assert.True(targets["roo-code"].Descriptor.SupportsProjectMcp);
        Assert.False(targets["vscode"].Descriptor.SupportsGlobalMcp);
        Assert.True(targets["vscode"].Descriptor.SupportsProjectMcp);
        Assert.True(targets["gemini-cli"].Descriptor.SupportsGlobalMcp);
        Assert.True(targets["gemini-cli"].Descriptor.SupportsProjectMcp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot))
            Directory.Delete(_projectRoot, recursive: true);
    }
}
