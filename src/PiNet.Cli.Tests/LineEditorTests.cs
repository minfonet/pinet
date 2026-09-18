using PiNet.Cli.Interact;
using Xunit;

namespace PiNet.Cli.Tests;

/// <summary>SR-04.02 — line editor editing keys (pure).</summary>
public class LineEditorTests
{
    private static LineEditor Editor(string buffer, int? caret = null)
    {
        var editor = new LineEditor();
        editor.SetBuffer(buffer);
        if (caret is not null && caret != buffer.Length)
        {
            for (var i = buffer.Length; i > caret; i--)
                editor.Apply(Keys.Key(ConsoleKey.LeftArrow));
        }
        return editor;
    }

    [Fact]
    public void Typing_InsertsAtCaret()
    {
        var editor = Editor("abc", caret: 2);
        editor.Apply(Keys.Char('X'));
        Assert.Equal("abXc", editor.Buffer);
        Assert.Equal(3, editor.Caret);
    }

    [Fact]
    public void Backspace_DeletesLeftOfCaret()
    {
        var editor = Editor("abc", caret: 2);
        editor.Apply(Keys.Key(ConsoleKey.Backspace));
        Assert.Equal("ac", editor.Buffer);
        Assert.Equal(1, editor.Caret);
    }

    [Fact]
    public void Backspace_AtStart_IsNoOp()
    {
        var editor = Editor("abc", caret: 0);
        editor.Apply(Keys.Key(ConsoleKey.Backspace));
        Assert.Equal("abc", editor.Buffer);
        Assert.Equal(0, editor.Caret);
    }

    [Fact]
    public void Delete_RemovesAtCaret()
    {
        var editor = Editor("abc", caret: 2);
        editor.Apply(Keys.Key(ConsoleKey.Delete));
        Assert.Equal("ab", editor.Buffer);
        Assert.Equal(2, editor.Caret);
    }

    [Fact]
    public void Delete_AtEnd_IsNoOp()
    {
        var editor = Editor("abc");
        editor.Apply(Keys.Key(ConsoleKey.Delete));
        Assert.Equal("abc", editor.Buffer);
    }

    [Theory]
    [InlineData(0, -1, 0)]   // left at start clamps
    [InlineData(0, 1, 1)]
    [InlineData(3, 1, 3)]    // right at end clamps
    [InlineData(2, 1, 3)]
    public void Arrows_MoveCaret_Clamped(int startCaret, int moves, int expected)
    {
        var editor = Editor("abc", caret: startCaret);
        for (var i = 0; i < Math.Abs(moves); i++)
            editor.Apply(Keys.Key(moves > 0 ? ConsoleKey.RightArrow : ConsoleKey.LeftArrow));
        Assert.Equal(expected, editor.Caret);
        Assert.Equal("abc", editor.Buffer);
    }

    [Fact]
    public void Home_End_JumpToBoundaries()
    {
        var editor = Editor("abc", caret: 2);
        editor.Apply(Keys.Key(ConsoleKey.Home));
        Assert.Equal(0, editor.Caret);
        editor.Apply(Keys.Key(ConsoleKey.End));
        Assert.Equal(3, editor.Caret);
    }

    [Fact]
    public void CtrlA_CtrlE_MatchHome_End()
    {
        var editor = Editor("abc", caret: 2);
        editor.Apply(Keys.Ctrl(ConsoleKey.A));
        Assert.Equal(0, editor.Caret);
        editor.Apply(Keys.Ctrl(ConsoleKey.E));
        Assert.Equal(3, editor.Caret);
    }

    [Fact]
    public void CtrlU_DeletesBeforeCaret()
    {
        var editor = Editor("abc", caret: 2);
        editor.Apply(Keys.Ctrl(ConsoleKey.U));
        Assert.Equal("c", editor.Buffer);
        Assert.Equal(0, editor.Caret);
    }

    [Fact]
    public void CtrlU_AtStart_ClearsNothing()
    {
        var editor = Editor("abc", caret: 0);
        editor.Apply(Keys.Ctrl(ConsoleKey.U));
        Assert.Equal("abc", editor.Buffer);
    }

    [Fact]
    public void CtrlK_DeletesToEnd()
    {
        var editor = Editor("abc", caret: 2);
        editor.Apply(Keys.Ctrl(ConsoleKey.K));
        Assert.Equal("ab", editor.Buffer);
        Assert.Equal(2, editor.Caret);
    }

    [Fact]
    public void CtrlK_AtEnd_IsNoOp()
    {
        var editor = Editor("abc");
        editor.Apply(Keys.Ctrl(ConsoleKey.K));
        Assert.Equal("abc", editor.Buffer);
    }

    [Fact]
    public void Enter_Completes_And_SubmitReturnsLine()
    {
        var editor = Editor("hello");
        editor.Apply(Keys.Enter);
        Assert.True(editor.Complete);
        Assert.Equal("hello", editor.Submit());
        Assert.Equal("", editor.Buffer);
        Assert.False(editor.Complete);
    }

    [Fact]
    public void Keys_AreIgnored_AfterCompletion()
    {
        var editor = Editor("abc");
        editor.Apply(Keys.Enter);
        editor.Apply(Keys.Char('X'));
        Assert.Equal("abc", editor.Buffer);
        Assert.True(editor.Complete);
    }

    [Fact]
    public void CtrlC_IsIgnoredByEditor_TerminationIsLoopPolicy()
    {
        var editor = Editor("abc");
        editor.Apply(Keys.CtrlC);
        Assert.Equal("abc", editor.Buffer);
        Assert.False(editor.Complete);
    }

    [Fact]
    public void UpDown_AreIgnoredByEditor_HistoryIsLoopPolicy()
    {
        var editor = Editor("abc");
        editor.Apply(Keys.Up);
        editor.Apply(Keys.Down);
        Assert.Equal("abc", editor.Buffer);
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var editor = Editor("abc");
        editor.Reset();
        Assert.Equal("", editor.Buffer);
        Assert.Equal(0, editor.Caret);
        Assert.False(editor.Complete);
    }
}
