using Xunit;

namespace PiNet.Extensions.Tests;

public class SkillRegistryTests
{
    [Fact]
    public async Task RootMdFiles_InPinetSkills_AreDiscovered()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Home, "skills/alpha.md", "---\nname: alpha\ndescription: Alpha skill\n---\nDo alpha.");

        var registry = await SkillRegistry.LoadAsync(dirs.Cwd);

        Assert.Contains(registry.Skills, s => s.Name == "alpha" && s.Path.EndsWith(Path.Combine("skills", "alpha.md")));
    }

    [Fact]
    public async Task NestedSkillMd_Directories_AreDiscovered()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, ".pi/skills/my-skill/SKILL.md", "---\nname: my-skill\ndescription: Nested\n---\nbody");

        var registry = await SkillRegistry.LoadAsync(dirs.Cwd);

        var skill = Assert.Single(registry.Skills);
        Assert.Equal("my-skill", skill.Name);
        Assert.Equal(Path.Combine(dirs.Cwd, ".pi", "skills", "my-skill"), skill.Directory);
    }

    [Fact]
    public async Task AgentsLocation_RootMd_Ignored_NestedPicked()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, ".agents/skills/root.md", "---\nname: root-skill\ndescription: Should be ignored\n---\nbody");
        TestDirs.WriteFile(dirs.Cwd, ".agents/skills/deep/nested.md", "---\nname: nested-skill\ndescription: Picked\n---\nbody");

        var registry = await SkillRegistry.LoadAsync(dirs.Cwd);

        Assert.DoesNotContain(registry.Skills, s => s.Name == "root-skill");
        Assert.Contains(registry.Skills, s => s.Name == "nested-skill");
    }

    [Fact]
    public async Task ProjectSkills_NestedDirWithSkillMd_IsDiscovered()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, ".pi/skills/tooling/SKILL.md", "---\nname: tooling\ndescription: Tooling\n---\nbody");
        TestDirs.WriteFile(dirs.Cwd, ".pi/skills/tooling/snippets/extra.md", "no frontmatter, ignored");

        var registry = await SkillRegistry.LoadAsync(dirs.Cwd);

        Assert.Contains(registry.Skills, s => s.Name == "tooling");
    }

    [Fact]
    public async Task AncestorWalk_NearestWins()
    {
        using var dirs = new TestDirs();
        var parent = Path.GetDirectoryName(dirs.Cwd)!;
        TestDirs.WriteFile(parent, ".pi/skills/nearest.md", "---\nname: nearest\ndescription: Parent level\n---\nbody");
        TestDirs.WriteFile(dirs.Cwd, ".pi/skills/nearest.md", "---\nname: nearest\ndescription: Cwd level\n---\nbody");

        var registry = await SkillRegistry.LoadAsync(dirs.Cwd);

        var skill = Assert.Single(registry.Skills, s => s.Name == "nearest");
        Assert.StartsWith(dirs.Cwd, skill.Path);
    }

    [Fact]
    public async Task NameCollision_FirstWins_WithWarning()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Home, "skills/dup.md", "---\nname: dup\ndescription: Global\n---\nbody");
        TestDirs.WriteFile(dirs.Cwd, ".pi/skills/dup.md", "---\nname: dup\ndescription: Project\n---\nbody");

        var registry = await SkillRegistry.LoadAsync(dirs.Cwd);

        var skill = Assert.Single(registry.Skills, s => s.Name == "dup");
        Assert.Equal("Global", skill.Description);
        Assert.Contains(registry.Warnings, w => w.Contains("dup"));
    }

    [Fact]
    public async Task PackageSkills_AreDiscovered()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Home, "packages/tools/skills/pkg-skill.md", "---\nname: pkg-skill\ndescription: From package\n---\nbody");

        var registry = await SkillRegistry.LoadAsync(dirs.Cwd);

        Assert.Contains(registry.Skills, s => s.Name == "pkg-skill");
    }

    [Fact]
    public async Task InvalidSkill_FilesProduceWarnings_ButOthersLoad()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, ".pi/skills/bad.md", "---\nname: BAD\ndescription: hi\n---\nbody");
        TestDirs.WriteFile(dirs.Cwd, ".pi/skills/good.md", "---\nname: good\ndescription: hi\n---\nbody");

        var registry = await SkillRegistry.LoadAsync(dirs.Cwd);

        Assert.DoesNotContain(registry.Skills, s => s.Name == "bad");
        Assert.Contains(registry.Skills, s => s.Name == "good");
        Assert.NotEmpty(registry.Warnings);
    }
}

public class SkillCatalogTests
{
    private static Skill Skill(string name, bool disabled = false) =>
        new(name, $"{name} description", $"C:/skills/{name}/SKILL.md", $"C:/skills/{name}", disabled);

    [Fact]
    public void EmitsXml_WithNameDescriptionLocation()
    {
        var section = SkillCatalog.ToSystemPromptSection([Skill("alpha"), Skill("beta")]);

        Assert.NotNull(section);
        Assert.Contains("# Skills", section);
        Assert.Contains("<name>alpha</name>", section);
        Assert.Contains("<description>alpha description</description>", section);
        Assert.Contains("<location>C:/skills/alpha/SKILL.md</location>", section);
        Assert.Contains("<name>beta</name>", section);
    }

    [Fact]
    public void DisableModelInvocation_IsOmitted()
    {
        var section = SkillCatalog.ToSystemPromptSection([Skill("visible"), Skill("hidden", disabled: true)]);

        Assert.NotNull(section);
        Assert.Contains("<name>visible</name>", section);
        Assert.DoesNotContain("hidden", section);
    }

    [Fact]
    public void EmptyList_ReturnsNull()
    {
        Assert.Null(SkillCatalog.ToSystemPromptSection([]));
    }

    [Fact]
    public void AllDisabled_ReturnsNull()
    {
        Assert.Null(SkillCatalog.ToSystemPromptSection([Skill("a", disabled: true)]));
    }
}