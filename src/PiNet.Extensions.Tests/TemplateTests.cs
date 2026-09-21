using Xunit;

namespace PiNet.Extensions.Tests;

public class TemplateExpanderTests
{
    private static PromptTemplate Template(string body) =>
        new("review", "desc", null, body);

    [Fact]
    public void PositionalArgs_Expand()
    {
        Assert.Equal("foo baz", TemplateExpander.Expand(Template("$1 $2"), ["foo", "baz"]));
    }

    [Fact]
    public void AllArgs_JoinWithSpaces()
    {
        Assert.Equal("foo bar", TemplateExpander.Expand(Template("$@"), ["foo", "bar"]));
        Assert.Equal("foo bar", TemplateExpander.Expand(Template("$ARGUMENTS"), ["foo", "bar"]));
    }

    [Fact]
    public void Default_WhenMissing()
    {
        Assert.Equal("fallback", TemplateExpander.Expand(Template("${1:-fallback}"), []));
        Assert.Equal("foo", TemplateExpander.Expand(Template("${1:-fallback}"), ["foo"]));
    }

    [Fact]
    public void AllDefault_WhenEmpty()
    {
        Assert.Equal("nothing", TemplateExpander.Expand(Template("${@:-nothing}"), []));
        Assert.Equal("a b", TemplateExpander.Expand(Template("${ARGUMENTS:-nothing}"), ["a", "b"]));
    }

    [Fact]
    public void Slice_FromIndex()
    {
        Assert.Equal("b c", TemplateExpander.Expand(Template("${@:1}"), ["a", "b", "c"]));
    }

    [Fact]
    public void Slice_FromIndexWithLength()
    {
        Assert.Equal("b c", TemplateExpander.Expand(Template("${@:1:2}"), ["a", "b", "c"]));
    }

    [Fact]
    public void MissingArgs_ExpandToEmpty()
    {
        Assert.Equal(string.Empty, TemplateExpander.Expand(Template("$2"), ["foo"]));
    }

    [Fact]
    public void Zero_ExpandsToTemplateName()
    {
        Assert.Equal("review", TemplateExpander.Expand(Template("$0"), []));
    }

    [Fact]
    public void UnknownDollar_Sequence_LeftAsIs()
    {
        Assert.Equal("$FOO", TemplateExpander.Expand(Template("$FOO"), []));
        Assert.Equal("$12", TemplateExpander.Expand(Template("$12"), ["a", "b"]));
    }

    [Fact]
    public void MultiDigit_WhenEnoughArgs()
    {
        var args = new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l" };
        Assert.Equal("k", TemplateExpander.Expand(Template("$11"), args));
    }

    [Fact]
    public void LiteralDollarKept_WhenNotFollowedByToken()
    {
        Assert.Equal("$", TemplateExpander.Expand(Template("$"), []));
        Assert.Equal("5 dollars", TemplateExpander.Expand(Template("5 dollars"), []));
    }
}

public class TemplateRegistryTests
{
    [Fact]
    public async Task Discovery_GlobalThenProjectThenPackages_FirstWins()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Home, "prompts/review.md", "---\ndescription: Global review\n---\nGlobal body");
        TestDirs.WriteFile(dirs.Cwd, ".pi/prompts/review.md", "---\ndescription: Project review\n---\nProject body");

        var templates = await TemplateRegistry.LoadAsync(dirs.Cwd);

        var template = templates.Single(t => t.Name == "review");
        Assert.Equal("Global review", template.Description);
        Assert.Equal("Global body", template.Body);
    }

    [Fact]
    public async Task Description_FallsBackToFirstBodyLine()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, ".pi/prompts/summarize.md", "# Summarize the codebase\n\nGo!");

        var templates = await TemplateRegistry.LoadAsync(dirs.Cwd);

        var template = templates.Single(t => t.Name == "summarize");
        Assert.Equal("# Summarize the codebase", template.Description);
    }

    [Fact]
    public async Task ArgumentHint_IsParsed()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, ".pi/prompts/fix.md", "---\ndescription: Fix\nargument-hint: <file>\n---\nFix $1");

        var templates = await TemplateRegistry.LoadAsync(dirs.Cwd);

        var template = templates.Single(t => t.Name == "fix");
        Assert.Equal("<file>", template.ArgumentHint);
    }

    [Fact]
    public async Task NameCollision_FirstWins()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Cwd, ".pi/prompts/dup.md", "one");
        TestDirs.WriteFile(
            Path.GetDirectoryName(dirs.Cwd)!,
            Path.Combine(".pi", "prompts", "dup.md"),
            "two");

        var templates = await TemplateRegistry.LoadAsync(dirs.Cwd);

        var template = templates.Single(t => t.Name == "dup");
        Assert.Equal("one", template.Body);
    }

    [Fact]
    public async Task PackagePrompts_AreDiscovered()
    {
        using var dirs = new TestDirs();
        TestDirs.WriteFile(dirs.Home, "packages/my-pkg/prompts/pack.md", "package body");

        var templates = await TemplateRegistry.LoadAsync(dirs.Cwd);

        Assert.Contains(templates, t => t.Name == "pack");
    }
}