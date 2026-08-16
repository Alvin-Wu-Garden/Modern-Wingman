namespace AgentService.Infrastructure.Orchestration;

/// <summary>
/// 對話 Runtime 的整輪執行期限。一般對話與專案解析的工作量不同，
/// 因此分別設定；單一模型或工具仍應保有自己的短逾時與降級策略。
/// </summary>
public sealed class ConversationRuntimeOptions
{
    public const string SectionName = "ConversationRuntime";

    /// <summary>一般對話的整輪硬性期限，預設五分鐘。</summary>
    public int GeneralTimeoutSeconds { get; set; } = 300;

    /// <summary>專案解析對話的整輪硬性期限，預設十分鐘。</summary>
    public int ProjectAnalysisTimeoutSeconds { get; set; } = 600;

    /// <summary>依對話是否綁定專案取得整輪執行期限。</summary>
    public TimeSpan ResolveTimeout(bool isProjectConversation) =>
        TimeSpan.FromSeconds(
            isProjectConversation
                ? ProjectAnalysisTimeoutSeconds
                : GeneralTimeoutSeconds);
}
