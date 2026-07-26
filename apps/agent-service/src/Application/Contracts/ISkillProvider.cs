using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// 提供 Modern Wingman 可讀取的指示型 Agent Skills。
///
/// Modern Wingman 不執行 Skill 內的腳本，也不提供可讓模型自行讀檔的工具。
/// 呼叫端會以有限長度讀取 SKILL.md，並把內容當成不受信任的輔助指示。
/// </summary>
public interface ISkillProvider
{
    /// <summary>列出所有可用 skills（name + description）。</summary>
    IReadOnlyList<SkillDefinition> ListSkills();

    /// <summary>重新掃描 skills 目錄（安裝/移除後呼叫）。</summary>
    void Refresh();
}
