using PiNet.Cli.Interact;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>SR-04.03 — command history.</summary>
public class LineHistoryTests
{
    [Fact]
    public void Add_RecordsLines()
    {
        var history = new LineHistory();
        history.Add("foo");
        history.Add("bar");
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void Add_IgnoresBlankLines()
    {
        var history = new LineHistory();
        history.Add("");
        history.Add("   ");
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Add_IgnoresDuplicateLast()
    {
        var history = new LineHistory();
        history.Add("foo");
        history.Add("foo");
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void MoveBack_WalksTowardOlder_ThenClamps()
    {
        var history = new LineHistory();
        history.Add("foo");
        history.Add("bar");

        Assert.True(history.MoveBack());
        Assert.Equal("bar", history.Current);
        Assert.True(history.MoveBack());
        Assert.Equal("foo", history.Current);
        Assert.False(history.MoveBack());
        Assert.Equal("foo", history.Current); // older-boundary clamped
    }

    [Fact]
    public void MoveForward_ReturnsToDraft()
    {
        var history = new LineHistory();
        history.Add("foo");
        history.Add("bar");

        history.MoveBack(); // bar
        history.MoveBack(); // foo
        Assert.True(history.MoveForward());
        Assert.Equal("bar", history.Current);
        Assert.True(history.MoveForward());
        Assert.False(history.IsNavigating);
        Assert.Equal("", history.Current); // back to in-progress draft
    }

    [Fact]
    public void MoveForward_WithoutNavigation_IsNoOp()
    {
        var history = new LineHistory();
        history.Add("foo");
        Assert.False(history.MoveForward());
    }

    [Fact]
    public void MoveBack_WithEmptyHistory_IsNoOp()
    {
        var history = new LineHistory();
        Assert.False(history.MoveBack());
    }

    [Fact]
    public void SetDraft_OverridesDraftValue()
    {
        var history = new LineHistory();
        history.Add("foo");
        history.SetDraft("draft");
        Assert.Equal("draft", history.Current);
        history.MoveBack();
        Assert.Equal("foo", history.Current);
    }

    [Fact]
    public void Add_EvictsOldest_BeyondMaxEntries()
    {
        var history = new LineHistory(maxEntries: 2);
        history.Add("a");
        history.Add("b");
        history.Add("c");
        Assert.Equal(2, history.Count);
        Assert.True(history.MoveBack());
        Assert.Equal("c", history.Current);
        Assert.True(history.MoveBack());
        Assert.Equal("b", history.Current);
        Assert.False(history.MoveBack());
    }

    [Fact]
    public void Add_ResetsNavigation()
    {
        var history = new LineHistory();
        history.Add("foo");
        history.Add("bar");
        history.MoveBack();
        Assert.True(history.IsNavigating);
        history.Add("baz");
        Assert.False(history.IsNavigating);
    }
}
