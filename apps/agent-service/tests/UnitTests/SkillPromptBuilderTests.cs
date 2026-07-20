using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Skills;

namespace AgentService.UnitTests;

public sealed class SkillPromptBuilderTests
{
    private sealed class FakeSkillProvider(IReadOnlyList<SkillDefinition> skills) : ISkillProvider
    {
        public IReadOnlyList<SkillDefinition> ListSkills() => skills;

        public Task<string?> ReadSkillContentAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<string?>(
                skills.Any(s => s.Name == name) ? $"content-of-{name}" : null);

        public void Refresh() { }
    }

    [Fact]
    public void NoSkills_ReturnsEmptyPrompt()
    {
        var provider = new FakeSkillProvider([]);
        Assert.Equal(string.Empty, SkillPromptBuilder.BuildSkillsPrompt(provider));
    }

    [Fact]
    public void WithSkills_PromptListsNameAndDescription()
    {
        var provider = new FakeSkillProvider([
            new SkillDefinition { Name = "pdf", Description = "PDF 處理", SkillFilePath = "x" },
            new SkillDefinition { Name = "xlsx", Description = "試算表", SkillFilePath = "y" },
        ]);

        var prompt = SkillPromptBuilder.BuildSkillsPrompt(provider);

        Assert.Contains("**pdf**: PDF 處理", prompt);
        Assert.Contains("**xlsx**: 試算表", prompt);
        Assert.Contains("read_skill", prompt);
        // Progressive disclosure：不含全文
        Assert.DoesNotContain("content-of-pdf", prompt);
    }
}
