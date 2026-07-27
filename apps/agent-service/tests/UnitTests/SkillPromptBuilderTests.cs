using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Skills;

namespace AgentService.UnitTests;

public sealed class SkillPromptBuilderTests
{
    private sealed class FakeSkillProvider(IReadOnlyList<SkillDefinition> skills) : ISkillProvider
    {
        public IReadOnlyList<SkillDefinition> ListSkills() => skills;

        public void Refresh() { }
    }

    [Fact]
    public void NoSkills_ReturnsEmptyPrompt()
    {
        var provider = new FakeSkillProvider([]);
        Assert.Equal(string.Empty, SkillPromptBuilder.BuildSkillsPrompt(provider));
    }

    [Fact]
    public void WithSkills_PromptIncludesBoundedUntrustedInstructions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mw-skill-prompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var skillFile = Path.Combine(root, "SKILL.md");
        File.WriteAllText(skillFile, "# PDF\ncontent-of-pdf");
        var provider = new FakeSkillProvider([
            new SkillDefinition { Name = "pdf", Description = "PDF 處理", SkillFilePath = skillFile },
        ]);

        try
        {
            var prompt = SkillPromptBuilder.BuildSkillsPrompt(provider);

            Assert.Contains("name=\"pdf\"", prompt);
            Assert.Contains("description=\"PDF 處理\"", prompt);
            Assert.Contains("trust=\"untrusted\"", prompt);
            Assert.Contains("content-of-pdf", prompt);
            Assert.DoesNotContain("read_skill", prompt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
