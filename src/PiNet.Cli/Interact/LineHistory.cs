namespace PiNet.Cli.Interact;

public sealed class LineHistory
{
    private readonly List<string> _entries = [];
    private readonly int _maxEntries;
    private int _pointer = -1;
    private string _draft = string.Empty;

    public LineHistory(int maxEntries = 100)
    {
        _maxEntries = Math.Max(1, maxEntries);
    }

    public int Count => _entries.Count;

    public bool IsNavigating => _pointer >= 0;

    public string Current => _pointer >= 0 ? _entries[_pointer] : _draft;

    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        if (_entries.Count > 0 && _entries[^1] == line)
            return;

        _entries.Add(line);
        if (_entries.Count > _maxEntries)
            _entries.RemoveAt(0);
        _pointer = -1;
        _draft = string.Empty;
    }

    public void SetDraft(string draft) => _draft = draft;

    public bool MoveBack()
    {
        if (_entries.Count == 0)
            return false;
        if (_pointer < 0)
        {
            _draft = string.Empty;
            _pointer = _entries.Count - 1;
            return true;
        }
        if (_pointer == 0)
            return false;
        _pointer--;
        return true;
    }

    public bool MoveForward()
    {
        if (_pointer < 0)
            return false;
        _pointer++;
        if (_pointer >= _entries.Count)
        {
            _pointer = -1;
            _draft = string.Empty;
        }
        return true;
    }
}