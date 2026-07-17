using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

public sealed class StaticAgentTargetAdapter(
    MarketplaceTargetDescriptor descriptor,
    string globalSkillsRelativePath,
    string projectSkillsRelativePath,
    string? globalMcpRelativePath,
    string? projectMcpRelativePath) : IAgentTargetAdapter
{
    public MarketplaceTargetDescriptor Descriptor => descriptor;

    public string ResolveSkillDirectory(MarketplaceDeploymentScope scope, string? projectPath)
    {
        if (scope == MarketplaceDeploymentScope.Global)
        {
            if (!Descriptor.SupportsGlobalScope) throw new InvalidOperationException($"{Descriptor.DisplayName} 不支援 global scope。");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), globalSkillsRelativePath);
        }
        if (!Descriptor.SupportsProjectScope) throw new InvalidOperationException($"{Descriptor.DisplayName} 不支援 project scope。");
        if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("project scope 必須選擇 project root。", nameof(projectPath));
        var root = Path.GetFullPath(projectPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到 project root：{root}");
        return Path.Combine(root, projectSkillsRelativePath);
    }

    public string ResolveMcpConfigPath(MarketplaceDeploymentScope scope, string? projectPath)
    {
        if (!Descriptor.SupportsMcp) throw new InvalidOperationException($"{Descriptor.DisplayName} 尚未支援 MCP 設定檔部署。");
        if (scope == MarketplaceDeploymentScope.Global)
        {
            if (!Descriptor.SupportsGlobalScope || string.IsNullOrWhiteSpace(globalMcpRelativePath)) throw new InvalidOperationException($"{Descriptor.DisplayName} 不支援 global MCP scope。");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), globalMcpRelativePath);
        }
        if (!Descriptor.SupportsProjectScope || string.IsNullOrWhiteSpace(projectMcpRelativePath)) throw new InvalidOperationException($"{Descriptor.DisplayName} 不支援 project MCP scope。");
        if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("project scope 必須選擇 project root。", nameof(projectPath));
        var root = Path.GetFullPath(projectPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到 project root：{root}");
        return Path.Combine(root, projectMcpRelativePath);
    }
}

public static class BuiltInAgentTargets
{
    public static IReadOnlyList<IAgentTargetAdapter> Create() =>
    [
        Create("codex-cli", "Codex CLI", ".codex/skills", ".codex/skills"),
        Create("codex-vscode", "Codex VS Code", ".codex/skills", ".codex/skills"),
        Create("claude-code-cli", "Claude Code", ".claude/skills", ".claude/skills", ".claude.json", ".mcp.json"),
        Create("cursor-windows", "Cursor", ".cursor/skills", ".cursor/skills", ".cursor/mcp.json", ".cursor/mcp.json"),
        Create("github-copilot-vscode", "GitHub Copilot", ".copilot/skills", ".github/skills", null, ".vscode/mcp.json", supportsGlobalScope: false),
        Create("wingman-desktop", "Modern Wingman", ".Wingman/agents/wingman/skills", ".wingman/skills"),
    ];

    private static IAgentTargetAdapter Create(string id, string name, string globalPath, string projectPath, string? globalMcpPath = null, string? projectMcpPath = null, bool supportsGlobalScope = true)
        => new StaticAgentTargetAdapter(new(id, name, SupportsSkill: true, SupportsMcp: globalMcpPath is not null || projectMcpPath is not null, SupportsGlobalScope: supportsGlobalScope, SupportsProjectScope: true), globalPath, projectPath, globalMcpPath, projectMcpPath);
}
