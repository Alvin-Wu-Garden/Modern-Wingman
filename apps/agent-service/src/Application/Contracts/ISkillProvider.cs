using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// 提供 Wingman Agent 可用的 Skills（progressive disclosure 模式）。
///
/// 設計原則：
///   - system prompt 只注入 name + description 清單（節省 context）
///   - Agent 需要時透過 read_skill 工具展開全文
/// </summary>
public interface ISkillProvider
{
    /// <summary>列出所有可用 skills（name + description）。</summary>
    IReadOnlyList<SkillDefinition> ListSkills();

    /// <summary>讀取指定 skill 的 SKILL.md 全文；不存在時回傳 null。</summary>
    Task<string?> ReadSkillContentAsync(string name, CancellationToken ct = default);

    /// <summary>重新掃描 skills 目錄（安裝/移除後呼叫）。</summary>
    void Refresh();
}
