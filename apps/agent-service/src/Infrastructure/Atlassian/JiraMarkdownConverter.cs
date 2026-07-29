using System.Text;
using System.Text.RegularExpressions;

namespace AgentService.Infrastructure.Atlassian;

/// <summary>
/// 將 JIRA Server/Data Center 的 Wiki Markup 轉換為 Markdown。
/// 只處理在需求描述、問題分析、測試案例和留言中常見的標記；
/// 未識別的 markup 會原樣保留，不阻斷轉換。
/// </summary>
public static partial class JiraMarkdownConverter
{
    /// <summary>
    /// 將 JIRA Wiki Markup 字串轉換為 Markdown。
    /// 如果輸入為 null 或空白，回傳空字串。
    /// </summary>
    public static string Convert(string? wikiMarkup)
    {
        if (string.IsNullOrWhiteSpace(wikiMarkup))
            return string.Empty;

        var text = wikiMarkup;

        // ── 多行區塊（先處理，避免行處理破壞區塊邊界） ─────────────────────────
        text = ConvertCodeBlocks(text);
        text = ConvertNoformat(text);
        text = ConvertPanel(text);
        text = ConvertQuote(text);
        text = ConvertColorStrip(text);
        text = ConvertNoteWarningInfo(text);

        // ── 按行處理 ───────────────────────────────────────────────────────────
        var lines = text.Split('\n');
        var result = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            line = ConvertHeading(line);
            line = ConvertTable(line);
            line = ConvertBulletList(line);
            line = ConvertNumberedList(line);
            line = ConvertInlineMarkup(line);
            result.AppendLine(line);
        }

        return result.ToString().TrimEnd();
    }

    // ── Code blocks ──────────────────────────────────────────────────────────

    private static string ConvertCodeBlocks(string text)
    {
        // {code:java} ... {code}  →  ```java\n...\n```
        return CodeBlockPattern().Replace(text, match =>
        {
            var lang = match.Groups[1].Value.Trim();
            var content = match.Groups[2].Value.Trim();
            return $"```{lang}\n{content}\n```";
        });
    }

    private static string ConvertNoformat(string text) =>
        NoformatPattern().Replace(text, match =>
            $"```\n{match.Groups[1].Value.Trim()}\n```");

    // ── Panel / Quote ─────────────────────────────────────────────────────────

    private static string ConvertPanel(string text) =>
        PanelPattern().Replace(text, match =>
        {
            var title = match.Groups[1].Success ? $"**{match.Groups[1].Value.Trim()}**\n" : "";
            var content = match.Groups[2].Value.Trim().Replace("\n", "\n> ");
            return $"> {title}> {content}";
        });

    private static string ConvertQuote(string text) =>
        QuotePattern().Replace(text, match =>
        {
            var content = match.Groups[1].Value.Trim().Replace("\n", "\n> ");
            return $"> {content}";
        });

    // ── Color strip ──────────────────────────────────────────────────────────

    private static string ConvertColorStrip(string text) =>
        ColorPattern().Replace(text, match => match.Groups[1].Value);

    // ── Note / Warning / Info ────────────────────────────────────────────────

    private static string ConvertNoteWarningInfo(string text)
    {
        text = NotePattern().Replace(text, match =>
            $"> ⚠️ **注意：** {match.Groups[1].Value.Trim()}");
        text = WarningPattern().Replace(text, match =>
            $"> ❌ **警告：** {match.Groups[1].Value.Trim()}");
        text = InfoPattern().Replace(text, match =>
            $"> ℹ️ **說明：** {match.Groups[1].Value.Trim()}");
        text = TipPattern().Replace(text, match =>
            $"> 💡 **提示：** {match.Groups[1].Value.Trim()}");
        return text;
    }

    // ── 逐行轉換 ─────────────────────────────────────────────────────────────

    private static string ConvertHeading(string line)
    {
        var m = HeadingPattern().Match(line);
        if (!m.Success) return line;
        var level = m.Groups[1].Value.Length;
        var hashes = new string('#', Math.Min(level, 6));
        return $"{hashes} {m.Groups[2].Value.Trim()}";
    }

    private static string ConvertTable(string line)
    {
        if (!line.TrimStart().StartsWith('|') && !line.TrimStart().StartsWith("||"))
            return line;

        // 表頭：||col1||col2|| → | col1 | col2 |
        if (line.TrimStart().StartsWith("||"))
        {
            var cols = line.Split("||", StringSplitOptions.RemoveEmptyEntries);
            var header = "| " + string.Join(" | ", cols.Select(c => c.Trim())) + " |";
            var separator = "| " + string.Join(" | ", cols.Select(_ => "---")) + " |";
            return header + "\n" + separator;
        }

        // 表格內容：|cell1|cell2| → | cell1 | cell2 |
        var cells = line.Trim().TrimStart('|').TrimEnd('|')
            .Split('|').Select(c => c.Trim());
        return "| " + string.Join(" | ", cells) + " |";
    }

    private static string ConvertBulletList(string line)
    {
        var m = BulletPattern().Match(line);
        if (!m.Success) return line;
        var depth = m.Groups[1].Value.Length;
        var indent = new string(' ', (depth - 1) * 2);
        return $"{indent}- {m.Groups[2].Value.Trim()}";
    }

    private static string ConvertNumberedList(string line)
    {
        var m = NumberedPattern().Match(line);
        if (!m.Success) return line;
        var depth = m.Groups[1].Value.Length;
        var indent = new string(' ', (depth - 1) * 2);
        return $"{indent}1. {m.Groups[2].Value.Trim()}";
    }

    private static string ConvertInlineMarkup(string line)
    {
        // 連結：[text|url] → [text](url)
        line = LinkPattern().Replace(line, m => $"[{m.Groups[1].Value}]({m.Groups[2].Value})");
        // URL without text：[url] → <url>
        line = BareUrlPattern().Replace(line, m => $"<{m.Groups[1].Value}>");
        // 圖片：!image.png|params! → [圖片: image.png]
        line = ImagePattern().Replace(line, m => $"[圖片: {m.Groups[1].Value}]");
        // 粗體：*text* → **text**
        line = BoldPattern().Replace(line, m => $"**{m.Groups[1].Value}**");
        // 斜體：_text_ → *text*
        line = ItalicPattern().Replace(line, m => $"*{m.Groups[1].Value}*");
        // 等寬字：{{text}} → `text`
        line = MonoPattern().Replace(line, m => $"`{m.Groups[1].Value}`");
        // 刪除線：-text- → ~~text~~
        line = StrikePattern().Replace(line, m => $"~~{m.Groups[1].Value}~~");
        // 底線：+text+ → <u>text</u>
        line = UnderlinePattern().Replace(line, m => $"<u>{m.Groups[1].Value}</u>");
        return line;
    }

    // ── Generated Regex ───────────────────────────────────────────────────────

    [GeneratedRegex(@"\{code(?::([^}]*))?\}(.*?)\{code\}", RegexOptions.Singleline, 500)]
    private static partial Regex CodeBlockPattern();

    [GeneratedRegex(@"\{noformat[^}]*\}(.*?)\{noformat\}", RegexOptions.Singleline, 500)]
    private static partial Regex NoformatPattern();

    [GeneratedRegex(@"\{panel(?::title=([^|}]+))?\}(.*?)\{panel\}", RegexOptions.Singleline, 500)]
    private static partial Regex PanelPattern();

    [GeneratedRegex(@"\{quote\}(.*?)\{quote\}", RegexOptions.Singleline, 500)]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"\{color:[^}]+\}(.*?)\{color\}", RegexOptions.Singleline, 500)]
    private static partial Regex ColorPattern();

    [GeneratedRegex(@"\{note[^}]*\}(.*?)\{note\}", RegexOptions.Singleline, 500)]
    private static partial Regex NotePattern();

    [GeneratedRegex(@"\{warning[^}]*\}(.*?)\{warning\}", RegexOptions.Singleline, 500)]
    private static partial Regex WarningPattern();

    [GeneratedRegex(@"\{info[^}]*\}(.*?)\{info\}", RegexOptions.Singleline, 500)]
    private static partial Regex InfoPattern();

    [GeneratedRegex(@"\{tip[^}]*\}(.*?)\{tip\}", RegexOptions.Singleline, 500)]
    private static partial Regex TipPattern();

    [GeneratedRegex(@"^h([1-6])\.\s+(.+)$", RegexOptions.Multiline, 200)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(\*+)\s(.+)$", RegexOptions.Multiline, 200)]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^(#+)\s(.+)$", RegexOptions.Multiline, 200)]
    private static partial Regex NumberedPattern();

    [GeneratedRegex(@"\[([^\|]+)\|([^\]]+)\]", RegexOptions.None, 200)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\[(https?://[^\]]+)\]", RegexOptions.None, 200)]
    private static partial Regex BareUrlPattern();

    [GeneratedRegex(@"!([^!|\n]+)(?:\|[^!]*)?\!", RegexOptions.None, 200)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"(?<!\*)\*([^*\n]+)\*(?!\*)", RegexOptions.None, 200)]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"(?<!_)_([^_\n]+)_(?!_)", RegexOptions.None, 200)]
    private static partial Regex ItalicPattern();

    [GeneratedRegex(@"\{\{([^}]+)\}\}", RegexOptions.None, 200)]
    private static partial Regex MonoPattern();

    [GeneratedRegex(@"(?<!\-)\-([^-\n]+)\-(?!\-)", RegexOptions.None, 200)]
    private static partial Regex StrikePattern();

    [GeneratedRegex(@"\+([^+\n]+)\+", RegexOptions.None, 200)]
    private static partial Regex UnderlinePattern();
}
