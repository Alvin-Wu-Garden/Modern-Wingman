using AgentService.Infrastructure.Skills;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class FileSystemSkillProviderTests : IDisposable
{
    private readonly string _root;

    public FileSystemSkillProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mw-skilltest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ParseDescription_SupportsYamlBlockScalar()
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-frontmatter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "SKILL.md");
        File.WriteAllText(path, """
            ---
            name: example
            description: |-
              First line.
              Second line.
            ---
            # Example
            """);
        try
        {
            Assert.Equal(
                "First line. Second line.",
                FileSystemSkillProvider.ParseDescription(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteSkill(string name, string content)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
    }

    private FileSystemSkillProvider CreateProvider() =>
        new(_root, NullLogger<FileSystemSkillProvider>.Instance);

    [Fact]
    public void EmptyDirectory_ReturnsNoSkills()
    {
        var provider = CreateProvider();
        Assert.Empty(provider.ListSkills());
    }

    [Fact]
    public void SkillWithFrontmatter_ParsesDescription()
    {
        WriteSkill("pdf-tools", "---\nname: pdf-tools\ndescription: 處理 PDF 文件\n---\n# Body");
        var provider = CreateProvider();

        var skill = Assert.Single(provider.ListSkills());
        Assert.Equal("pdf-tools", skill.Name);
        Assert.Equal("處理 PDF 文件", skill.Description);
    }

    [Fact]
    public void SkillWithoutFrontmatter_FallsBackToName()
    {
        WriteSkill("plain-skill", "# Just markdown, no frontmatter");
        var provider = CreateProvider();

        var skill = Assert.Single(provider.ListSkills());
        Assert.Equal("plain-skill", skill.Description);
    }

    [Fact]
    public void DirectoryWithoutSkillMd_IsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "not-a-skill"));
        var provider = CreateProvider();
        Assert.Empty(provider.ListSkills());
    }

    [Fact]
    public async Task ReadSkillContent_ReturnsFullText()
    {
        WriteSkill("my-skill", "---\ndescription: x\n---\nFull body here");
        var provider = CreateProvider();

        var content = await provider.ReadSkillContentAsync("my-skill");
        Assert.NotNull(content);
        Assert.Contains("Full body here", content);
    }

    [Fact]
    public async Task ReadSkillContent_IsCaseInsensitive()
    {
        WriteSkill("My-Skill", "content");
        var provider = CreateProvider();

        var content = await provider.ReadSkillContentAsync("my-skill");
        Assert.NotNull(content);
    }

    [Fact]
    public async Task ReadSkillContent_UnknownName_ReturnsNull()
    {
        var provider = CreateProvider();
        Assert.Null(await provider.ReadSkillContentAsync("does-not-exist"));
    }

    [Fact]
    public void Refresh_PicksUpNewSkills()
    {
        var provider = CreateProvider();
        Assert.Empty(provider.ListSkills());

        WriteSkill("late-arrival", "---\ndescription: 後來新增\n---");
        provider.Refresh();

        Assert.Single(provider.ListSkills());
    }
}
