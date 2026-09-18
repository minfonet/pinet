namespace PiNet.Cli.Interact;

public sealed class LineEditor
{
    private string _buffer = string.Empty;
    private int _caret;

    public string Buffer => _buffer;
    public int Caret => _caret;
    public bool Complete { get; private set; }

    public void Apply(ConsoleKeyInfo key)
    {
        if (Complete)
            return;

        var modifiers = key.Modifiers;
        var control = modifiers.HasFlag(ConsoleModifiers.Control);

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Complete = true;
                return;
            case ConsoleKey.Backspace:
                if (control)
                    ClearBefore();
                else
                    DeleteLeft();
                return;
            case ConsoleKey.Delete:
                DeleteAtCaret();
                return;
            case ConsoleKey.LeftArrow:
                if (_caret > 0) _caret--;
                return;
            case ConsoleKey.RightArrow:
                if (_caret < _buffer.Length) _caret++;
                return;
            case ConsoleKey.Home:
                _caret = 0;
                return;
            case ConsoleKey.End:
                _caret = _buffer.Length;
                return;
            case ConsoleKey.U when control:
                ClearBefore();
                return;
            case ConsoleKey.K when control:
                DeleteToEnd();
                return;
            case ConsoleKey.A when control:
                _caret = 0;
                return;
            case ConsoleKey.E when control:
                _caret = _buffer.Length;
                return;
            case ConsoleKey.UpArrow:
            case ConsoleKey.DownArrow:
                return; // history navigation is loop policy, not editor policy
            case ConsoleKey.C when control:
            case ConsoleKey.D when control:
                return;
        }

        if (key.KeyChar >= 0x20 && key.KeyChar != 0x7f)
            Insert(key.KeyChar);
    }

    public void SetBuffer(string text)
    {
        _buffer = text ?? string.Empty;
        _caret = _buffer.Length;
        Complete = false;
    }

    public void Reset()
    {
        _buffer = string.Empty;
        _caret = 0;
        Complete = false;
    }

    public string Submit()
    {
        var line = _buffer;
        Reset();
        return line;
    }

    private void Insert(char c)
    {
        _buffer = _buffer.Insert(_caret, c.ToString());
        _caret++;
    }

    private void DeleteLeft()
    {
        if (_caret <= 0)
            return;
        _buffer = _buffer.Remove(_caret - 1, 1);
        _caret--;
    }

    private void DeleteAtCaret()
    {
        if (_caret >= _buffer.Length)
            return;
        _buffer = _buffer.Remove(_caret, 1);
    }

    private void ClearBefore()
    {
        if (_caret == 0)
            return;
        _buffer = _buffer.Substring(_caret);
        _caret = 0;
    }

    private void DeleteToEnd()
    {
        if (_caret >= _buffer.Length)
            return;
        _buffer = _buffer.Substring(0, _caret);
    }
}