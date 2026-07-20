using System.Text;
using AgentService.Application.Contracts;

namespace AgentService.Infrastructure.Skills;

/// <summary>
/// 將 skills 轉為 progressive-disclosure 提示片段。
///
/// 原則（Claude Code / Codex 已驗證做法）：
///   - system prompt 只列出 name + description（省 context）
///   - Agent 判斷需要時呼叫 read_skill(name) 取得全文
/// </summary>
public static class SkillPromptBuilder
{
    /// <summary>產生注入 system prompt 的 skills 清單片段；無 skills 回傳空字串。</summary>
    public static string BuildSkillsPrompt(ISkillProvider provider)
    {
        var skills = provider.ListSkills();
        if (skills.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## 可用 Skills");
        sb.AppendLine("以下是你可使用的技能清單。當使用者的任務符合某個技能的描述時，" +
                      "先呼叫 read_skill 工具取得該技能的完整指示再執行。");
        foreach (var skill in skills)
        {
            sb.AppendLine($"- **{skill.Name}**: {skill.Description}");
        }
        return sb.ToString();
    }
}
