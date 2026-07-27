using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace AgentService.Infrastructure.Skills;

/// <summary>
/// 從檔案系統載入 Modern Wingman 可使用的指示型 Agent Skills。
///
/// 目錄來源：~/.wingman/agents/wingman/skills/&lt;skill-name&gt;/SKILL.md
/// （由 Marketplace 部署流程以實體複製方式寫入）
///
/// 執行緒安全：清單以 volatile snapshot 保存，Refresh() 原子替換。
/// </summary>
public sealed class FileSystemSkillProvider : ISkillProvider
{
    private readonly string _skillsRoot;
    private readonly ILogger<FileSystemSkillProvider> _logger;
    private volatile IReadOnlyList<SkillDefinition> _snapshot = [];

    public FileSystemSkillProvider(ILogger<FileSystemSkillProvider> logger)
        : this(DefaultSkillsRoot(), logger)
    {
    }

    /// <summary>測試用建構子：可注入自訂 skills 目錄。</summary>
    public FileSystemSkillProvider(string skillsRoot, ILogger<FileSystemSkillProvider> logger)
    {
        _skillsRoot = skillsRoot;
        _logger = logger;
        Refresh();
    }

    private static string DefaultSkillsRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wingman", "agents", "wingman", "skills");

    public IReadOnlyList<SkillDefinition> ListSkills() => _snapshot;

    public void Refresh()
    {
        var skills = new List<SkillDefinition>();

        try
        {
            if (Directory.Exists(_skillsRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(_skillsRoot))
                {
                    var skillMd = Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(skillMd))
                        continue;

                    var name = Path.GetFileName(dir);
                    var description = ParseDescription(skillMd) ?? name;
                    skills.Add(new SkillDefinition
                    {
                        Name = name,
                        Description = description,
                        SkillFilePath = skillMd,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "掃描 skills 目錄失敗: {Root}", _skillsRoot);
        }

        _snapshot = skills;
        _logger.LogInformation("SkillProvider 載入 {Count} 個 skills（{Root}）", skills.Count, _skillsRoot);
    }

    /// <summary>從 SKILL.md YAML frontmatter 解析 description。</summary>
    internal static string? ParseDescription(string skillMdPath)
    {
        try
        {
            var content = File.ReadAllText(skillMdPath);
            using var reader = new StringReader(content);
            if (reader.ReadLine()?.Trim() != "---")
                return null;

            var yaml = new System.Text.StringBuilder();
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim() == "---")
                    break;
                yaml.AppendLine(line);
            }

            var values = new DeserializerBuilder().Build()
                .Deserialize<Dictionary<string, object?>>(yaml.ToString());
            if (values.TryGetValue("description", out var description) &&
                description is not null)
            {
                return string.Join(
                    " ",
                    description.ToString()!
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(part => part.Trim()))
                    .Trim();
            }
        }
        catch
        {
            // 解析失敗回退 null，由呼叫端 fallback
        }
        return null;
    }
}
