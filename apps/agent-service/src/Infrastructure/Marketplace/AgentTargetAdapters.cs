using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>
/// 描述使用固定資料夾與 JSON MCP 設定檔的外部 Agent。
/// 每一種 scope 的 MCP 路徑可獨立留空；留空代表該 Agent 在該 scope 沒有可安全寫入的公開格式。
/// </summary>
public sealed class StaticAgentTargetAdapter(
    MarketplaceTargetDescriptor descriptor,
    string globalSkillsRelativePath,
    string projectSkillsRelativePath,
    string? globalMcpRelativePath,
    string? projectMcpRelativePath,
    string mcpRootProperty = "mcpServers") : IAgentTargetAdapter
{
    /// <summary>回傳前端顯示與相容性判定使用的 target 能力。</summary>
    public MarketplaceTargetDescriptor Descriptor => descriptor;

    /// <summary>JSON 設定檔中保存 MCP server map 的根屬性。</summary>
    public string McpRootProperty => mcpRootProperty;

    /// <summary>解析 Skill 的實體複製目的地，不建立資料夾也不修改檔案。</summary>
    public string ResolveSkillDirectory(MarketplaceDeploymentScope scope, string? projectPath)
    {
        if (scope == MarketplaceDeploymentScope.Global)
        {
            if (!Descriptor.SupportsGlobalScope) throw new InvalidOperationException($"{Descriptor.DisplayName} 不支援 global scope。");
            return CombineRelativePath(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                globalSkillsRelativePath);
        }
        if (!Descriptor.SupportsProjectScope) throw new InvalidOperationException($"{Descriptor.DisplayName} 不支援 project scope。");
        if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("project scope 必須選擇 project root。", nameof(projectPath));
        var root = Path.GetFullPath(projectPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到 project root：{root}");
        return CombineRelativePath(root, projectSkillsRelativePath);
    }

    /// <summary>解析 MCP JSON 設定檔位置；不支援的 scope 會明確失敗。</summary>
    public string ResolveMcpConfigPath(MarketplaceDeploymentScope scope, string? projectPath)
    {
        if (!Descriptor.SupportsMcp) throw new InvalidOperationException($"{Descriptor.DisplayName} 尚未支援 MCP 設定檔部署。");
        if (scope == MarketplaceDeploymentScope.Global)
        {
            if (!Descriptor.SupportsGlobalScope || string.IsNullOrWhiteSpace(globalMcpRelativePath)) throw new InvalidOperationException($"{Descriptor.DisplayName} 不支援 global MCP scope。");
            return CombineRelativePath(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                globalMcpRelativePath);
        }
        if (!Descriptor.SupportsProjectScope || string.IsNullOrWhiteSpace(projectMcpRelativePath)) throw new InvalidOperationException($"{Descriptor.DisplayName} 不支援 project MCP scope。");
        if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("project scope 必須選擇 project root。", nameof(projectPath));
        var root = Path.GetFullPath(projectPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到 project root：{root}");
        return CombineRelativePath(root, projectMcpRelativePath);
    }

    /// <summary>
    /// 將描述檔內跨平台一致的斜線路徑轉成目前 Windows 的實體路徑。
    /// 這可避免產生同時含有反斜線與正斜線的路徑，讓預覽、移除與測試看到相同結果。
    /// </summary>
    private static string CombineRelativePath(string root, string relativePath)
    {
        var segments = relativePath.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Path.Combine([root, .. segments]);
    }
}

public static class BuiltInAgentTargets
{
    /// <summary>
    /// 建立使用者核定的十二個部署目標。
    /// Codex 使用 TOML，Kilo 與 OpenCode 使用不同的 JSON schema，因此目前只部署 Skill；
    /// 沒有官方固定格式的 Grok 也只使用跨 Agent 的 .agents/skills，不臆造 MCP 路徑。
    /// </summary>
    public static IReadOnlyList<IAgentTargetAdapter> Create() =>
    [
        Create("codex", "Codex", ".agents/skills", ".agents/skills"),
        Create("claude-code", "Claude Code", ".claude/skills", ".claude/skills", ".claude.json", ".mcp.json"),
        Create("github-copilot", "GitHub Copilot", ".copilot/skills", ".github/skills", ".copilot/mcp-config.json", ".mcp.json"),
        Create("cursor", "Cursor", ".cursor/skills", ".cursor/skills", ".cursor/mcp.json", ".cursor/mcp.json"),
        Create("vscode", "VS Code", ".github/skills", ".github/skills", null, ".vscode/mcp.json", supportsGlobalScope: false, mcpRootProperty: "servers"),
        Create("cline", "Cline", ".cline/skills", ".cline/skills", ".cline/data/settings/cline_mcp_settings.json", ".cline/mcp.json"),
        Create("roo-code", "Roo Code", ".roo/skills", ".roo/skills", null, ".roo/mcp.json"),
        Create("kilo-code", "Kilo Code", ".kilo/skills", ".kilo/skills"),
        Create("gemini-cli", "Gemini CLI", ".gemini/skills", ".gemini/skills", ".gemini/settings.json", ".gemini/settings.json"),
        Create("opencode", "OpenCode", ".config/opencode/skills", ".opencode/skills"),
        Create("antigravity", "Antigravity", ".gemini/antigravity-cli/skills", ".agents/skills", ".gemini/config/mcp_config.json", ".agents/mcp_config.json"),
        Create("grok", "Grok", ".agents/skills", ".agents/skills"),
    ];

    /// <summary>以一致方式建立 target descriptor，避免十二個 target 重複建構樣板。</summary>
    private static IAgentTargetAdapter Create(
        string id,
        string name,
        string globalPath,
        string projectPath,
        string? globalMcpPath = null,
        string? projectMcpPath = null,
        bool supportsGlobalScope = true,
        string mcpRootProperty = "mcpServers") =>
        new StaticAgentTargetAdapter(
            new(
                id,
                name,
                SupportsSkill: true,
                SupportsMcp: globalMcpPath is not null || projectMcpPath is not null,
                SupportsGlobalScope: supportsGlobalScope,
                SupportsProjectScope: true,
                SupportsGlobalMcp: globalMcpPath is not null,
                SupportsProjectMcp: projectMcpPath is not null),
            globalPath,
            projectPath,
            globalMcpPath,
            projectMcpPath,
            mcpRootProperty);
}
