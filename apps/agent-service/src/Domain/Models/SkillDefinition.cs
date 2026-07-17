namespace AgentService.Domain.Models;

/// <summary>
/// 一個已安裝、可供 Wingman Agent 使用的 Skill。
/// 來源：中央 Skill Library 同步到 ~/.wingman/agents/wingman/skills 的目錄。
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>Skill 識別名稱（目錄名，例如 "pdf-processing"）。</summary>
    public required string Name { get; init; }

    /// <summary>來自 SKILL.md frontmatter 的描述；Agent 據此判斷何時使用。</summary>
    public required string Description { get; init; }

    /// <summary>SKILL.md 的完整檔案路徑。</summary>
    public required string SkillFilePath { get; init; }
}
