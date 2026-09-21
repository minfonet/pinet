using Xunit;

namespace PiNet.Extensions.Tests;

public class SkillFrontmatterTests
{
    private const string ValidSkill = """
        ---
        name: my-skill
        description: A test skill
        ---
        Step one.
        Step two.
        """;

    [Fact]
    public void ValidFrontmatter_ParsesSkill()
    {
        var path = Path.Combine("skills", "my-skill", "SKILL.md");
        var result = SkillFrontmatter.Parse(path, ValidSkill);

        Assert.Null(result.Warning);
        Assert.NotNull(result.Skill);
        Assert.Equal("my-skill", result.Skill.Name);
        Assert.Equal("A test skill", result.Skill.Description);
        Assert.Equal(path, result.Skill.Path);
        Assert.Equal(Path.Combine("skills", "my-skill"), result.Skill.Directory);
        Assert.False(result.Skill.DisableModelInvocation);
    }

    [Fact]
    public void MissingName_ReturnsWarning_NoSkill()
    {
        var result = SkillFrontmatter.Parse("x.md", "---\ndescription: hi\n---\nbody");

        Assert.NotNull(result.Warning);
        Assert.Null(result.Skill);
    }

    [Fact]
    public void MissingDescription_ReturnsWarning_NoSkill()
    {
        var result = SkillFrontmatter.Parse("x.md", "---\nname: abc\n---\nbody");

        Assert.NotNull(result.Warning);
        Assert.Null(result.Skill);
    }

    [Fact]
    public void UpperCaseName_Rejected()
    {
        var result = SkillFrontmatter.Parse("x.md", "---\nname: MySkill\ndescription: hi\n---\nbody");
        Assert.NotNull(result.Warning);
        Assert.Null(result.Skill);
    }

    [Fact]
    public void LeadingHyphenName_Rejected()
    {
        var result = SkillFrontmatter.Parse("x.md", "---\nname: -skill\ndescription: hi\n---\nbody");
        Assert.NotNull(result.Warning);
        Assert.Null(result.Skill);
    }

    [Fact]
    public void ConsecutiveHyphens_Rejected()
    {
        var result = SkillFrontmatter.Parse("x.md", "---\nname: my--skill\ndescription: hi\n---\nbody");
        Assert.NotNull(result.Warning);
        Assert.Null(result.Skill);
    }

    [Fact]
    public void NameLongerThan64_Rejected()
    {
        var name = new string('a', 65);
        var result = SkillFrontmatter.Parse("x.md", $"---\nname: {name}\ndescription: hi\n---\nbody");
        Assert.NotNull(result.Warning);
        Assert.Null(result.Skill);
    }

    [Fact]
    public void QuotedValues_AreStripped()
    {
        var result = SkillFrontmatter.Parse("x.md", "---\nname: \"my-skill\"\ndescription: 'quoted desc'\n---\nbody");

        Assert.NotNull(result.Skill);
        Assert.Equal("my-skill", result.Skill.Name);
        Assert.Equal("quoted desc", result.Skill.Description);
    }

    [Fact]
    public void DisableModelInvocation_True_IsParsed()
    {
        var result = SkillFrontmatter.Parse("x.md", "---\nname: my-skill\ndescription: hi\ndisable-model-invocation: true\n---\nbody");

        Assert.NotNull(result.Skill);
        Assert.True(result.Skill.DisableModelInvocation);
    }

    [Fact]
    public void NoFrontmatter_IsSilentlyIgnored()
    {
        var result = SkillFrontmatter.Parse("x.md", "just a markdown file");

        Assert.Null(result.Warning);
        Assert.Null(result.Skill);
    }
}