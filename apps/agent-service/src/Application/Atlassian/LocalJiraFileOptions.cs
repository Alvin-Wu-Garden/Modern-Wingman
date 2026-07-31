namespace AgentService.Application.Atlassian;

/// <summary>
/// 本機測試用 JIRA 檔案來源設定。
/// 僅供無法連線 JIRA 的測試環境使用；生產環境請保持 Enabled = false。
/// </summary>
public sealed class LocalJiraFileOptions
{
    public const string SectionName = "LocalJiraFiles";

    /// <summary>啟用本機檔案模式。啟用後 AnalyzeIssue 可略過 JIRA 連線直接讀取本機 JSON 檔。</summary>
    public bool Enabled { get; set; }

    /// <summary>存放測試 JSON 檔案的目錄（相對於工作目錄，或絕對路徑）。</summary>
    public string Directory { get; set; } = "temp/jira-samples";
}
