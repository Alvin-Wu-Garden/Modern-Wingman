using System.Text.RegularExpressions;

namespace AgentService.Application.Atlassian;

/// <summary>
/// JIRA Issue Key 格式驗證工具。
/// <para>
/// 使用者輸入格式（前端驗證）：<c>HD-1128</c>、<c>NR-208</c>。
/// 前端補上 <c>INNES1</c> 前綴後，後端以完整格式再驗證一次。
/// </para>
/// </summary>
public static partial class JiraKeyValidator
{
    private const string Prefix = "INNES1";

    /// <summary>
    /// 驗證使用者輸入（不含 INNES1 前綴）。
    /// 接受 HD-1128、NR-208；拒絕空值、零號、含空白或非 HD/NR 的 key。
    /// </summary>
    public static bool IsValidUserInput(string? input) =>
        input is not null && UserInputPattern().IsMatch(input.Trim().ToUpperInvariant());

    /// <summary>
    /// 將使用者輸入 trim + 轉大寫後，補上 INNES1 前綴，產生完整 JIRA Key。
    /// </summary>
    public static string ToFullKey(string userInput) =>
        Prefix + userInput.Trim().ToUpperInvariant();

    /// <summary>
    /// 驗證完整 JIRA Key（含 INNES1 前綴）。
    /// 後端 API 收到的 issueKey 必須通過此驗證。
    /// </summary>
    public static bool IsValidFullKey(string? fullKey) =>
        fullKey is not null && FullKeyPattern().IsMatch(fullKey);

    // 使用者輸入：HD-1128 或 NR-208，不接受 0 開頭號碼
    [GeneratedRegex(@"^(HD|NR)-[1-9][0-9]*$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex UserInputPattern();

    // 完整 JIRA Key：INNES1HD-1128 或 INNES1NR-208
    [GeneratedRegex(@"^INNES1(HD|NR)-[1-9][0-9]*$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex FullKeyPattern();
}
