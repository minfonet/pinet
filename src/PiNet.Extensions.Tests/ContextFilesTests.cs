using Xunit;

namespace PiNet.Extensions.Tests;

public class ContextFilesTests
{
    [Fact]
    public void SystemMd_ReplacesBasePrompt()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, "SYSTEM.md", "You are the system.\n");

        var built = SystemPromptBuilder.Build("base", ContextFiles.Load(dirs.Cwd), []);

        Assert.Equal("You are the system.", built);
    }

    [Fact]
    public void AppendSystemMd_IsAppended()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, "APPEND_SYSTEM.md", "Remember to be kind.");

        var built = SystemPromptBuilder.Build("base", ContextFiles.Load(dirs.Cwd), []);

        Assert.StartsWith("base", built);
        Assert.Contains("Remember to be kind.", built);
    }

    [Fact]
    public void NearestAgentsMd_IsUsedAsProjectInstructions()
    {
        using var dirs = new TestDirs();
        var parent = Path.GetDirectoryName(dirs.Cwd)!;
        TestDirs.WriteFile(parent, "AGENTS.md", "Parent instructions.");
        TestDirs.WriteFile(dirs.Cwd, "AGENTS.md", "Project instructions.");

        var built = SystemPromptBuilder.Build("base", ContextFiles.Load(dirs.Cwd), []);

        Assert.Contains("# Project instructions", built);
        Assert.Contains("Project instructions.", built);
        Assert.DoesNotContain("Parent instructions.", built);
    }

    [Fact]
    public void ClaudeMd_IsFallback()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, "CLAUDE.md", "Claude instructions.");

        var built = SystemPromptBuilder.Build("base", ContextFiles.Load(dirs.Cwd), []);

        Assert.Contains("Claude instructions.", built);
    }

    [Fact]
    public void BothInSameDirectory_AgentsMdWins()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, "CLAUDE.md", "Claude instructions.");
        TestDirs.WriteFile(dirs.Cwd, "AGENTS.md", "Agents instructions.");

        var built = SystemPromptBuilder.Build("base", ContextFiles.Load(dirs.Cwd), []);

        Assert.Contains("Agents instructions.", built);
        Assert.DoesNotContain("Claude instructions.", built);
    }

    [Fact]
    public void AssemblyOrder_Base_Append_Instructions_Skills()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, "APPEND_SYSTEM.md", "append");
        TestDirs.WriteFile(dirs.Cwd, "AGENTS.md", "instructions");
        var skills = new[] { new Skill("alpha", "Alpha", "/s/alpha/SKILL.md", "/s/alpha", false) };

        var built = SystemPromptBuilder.Build("base", ContextFiles.Load(dirs.Cwd), skills);

        Assert.StartsWith("base", built);
        Assert.True(built.IndexOf("base") < built.IndexOf("append"));
        Assert.True(built.IndexOf("append") < built.IndexOf("instructions"));
        Assert.True(built.IndexOf("instructions") < built.IndexOf("# Skills"));
    }

    [Fact]
    public void EmptySections_AreSkipped_BaseOnly()
    {
        using var dirs = new TestDirs();

        var built = SystemPromptBuilder.Build("base", ContextFiles.Load(dirs.Cwd), []);

        Assert.Equal("base", built);
    }
}