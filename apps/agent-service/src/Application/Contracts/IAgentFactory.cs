using AgentService.Domain.Models;
using Microsoft.Agents.AI;

namespace AgentService.Application.Contracts;

/// <summary>
/// 依 ProviderKind 建立 MAF AIAgent 的工廠（Strategy 模式）。
///
/// OCP：新增供應商 = 新增一個 IAgentFactory 實作並註冊 DI，
/// 呼叫端（WingmanChatAgent）不需修改。
/// </summary>
public interface IAgentFactory
{
    /// <summary>此工廠能處理的 ProviderKind。</summary>
    ProviderKind Kind { get; }

    /// <summary>
    /// 建立 AIAgent。回傳 null 表示無法建立（例如 API Key 遺失），
    /// 由呼叫端決定錯誤處理方式。
    /// </summary>
    AIAgent? CreateAgent(AgentCreationContext context);
}

/// <summary>建立 Agent 所需的完整上下文。</summary>
public sealed class AgentCreationContext
{
    public required ModelProviderProfile Profile { get; init; }
    public string? ModelOverride { get; init; }
    /// <summary>基礎系統指示（語言、角色設定）。</summary>
    public required string Instructions { get; init; }
    /// <summary>
    /// 經過字數上限與不可信內容標記處理的 instruction-only Skill 片段；
    /// 無 Skill 時為空字串，且不包含任何可執行 script 或 MCP tool。
    /// </summary>
    public string SkillsPrompt { get; init; } = string.Empty;
}
