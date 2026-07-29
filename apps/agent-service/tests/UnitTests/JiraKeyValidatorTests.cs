using AgentService.Application.Atlassian;

namespace AgentService.UnitTests;

/// <summary>驗證 JIRA Key 格式規則（含前綴補齊與邊界值）。</summary>
public sealed class JiraKeyValidatorTests
{
    // ── IsValidUserInput ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("HD-1")]
    [InlineData("HD-1128")]
    [InlineData("NR-208")]
    [InlineData("NR-1")]
    public void IsValidUserInput_ValidKeys_ReturnsTrue(string input) =>
        Assert.True(JiraKeyValidator.IsValidUserInput(input));

    [Theory]
    [InlineData("hd-1128")]           // 小寫也接受（內部轉大寫）
    [InlineData("  HD-1128  ")]       // 前後空白（trim）
    public void IsValidUserInput_CaseInsensitiveAndTrimmed_ReturnsTrue(string input) =>
        Assert.True(JiraKeyValidator.IsValidUserInput(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HD-0")]              // 零號不允許
    [InlineData("HD-00")]
    [InlineData("HD-01")]             // 前導零
    [InlineData("AB-1")]              // 非 HD/NR 前綴
    [InlineData("INNES1HD-1")]        // 不應輸入完整 Key（含前綴）
    [InlineData("HD1128")]            // 缺少連字號
    [InlineData("HD-")]               // 號碼部分空
    [InlineData("HD-abc")]            // 非數字
    [InlineData("HD -1128")]          // 中間有空白
    [InlineData(null)]
    public void IsValidUserInput_InvalidKeys_ReturnsFalse(string? input) =>
        Assert.False(JiraKeyValidator.IsValidUserInput(input));

    // ── ToFullKey ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("HD-1128", "INNES1HD-1128")]
    [InlineData("NR-208", "INNES1NR-208")]
    [InlineData("hd-1128", "INNES1HD-1128")]    // 大小寫正規化
    [InlineData("  NR-5  ", "INNES1NR-5")]       // trim
    public void ToFullKey_AppendsCorrectPrefix(string input, string expected) =>
        Assert.Equal(expected, JiraKeyValidator.ToFullKey(input));

    // ── IsValidFullKey ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("INNES1HD-1")]
    [InlineData("INNES1HD-1128")]
    [InlineData("INNES1NR-208")]
    public void IsValidFullKey_ValidFullKeys_ReturnsTrue(string key) =>
        Assert.True(JiraKeyValidator.IsValidFullKey(key));

    [Theory]
    [InlineData("")]
    [InlineData("HD-1128")]           // 缺 INNES1 前綴
    [InlineData("INNES1AB-1")]        // 非 HD/NR
    [InlineData("INNES1HD-0")]        // 零號
    [InlineData("innes1hd-1128")]     // 小寫不接受
    [InlineData("INNES1HD-01")]       // 前導零
    [InlineData(null)]
    public void IsValidFullKey_InvalidFullKeys_ReturnsFalse(string? key) =>
        Assert.False(JiraKeyValidator.IsValidFullKey(key));

    // ── 流程整合：input -> ToFullKey -> IsValidFullKey ────────────────────────

    [Theory]
    [InlineData("HD-1128")]
    [InlineData("NR-208")]
    public void ToFullKey_ThenValidate_RoundTrip(string userInput)
    {
        Assert.True(JiraKeyValidator.IsValidUserInput(userInput));
        var fullKey = JiraKeyValidator.ToFullKey(userInput);
        Assert.True(JiraKeyValidator.IsValidFullKey(fullKey));
    }
}
