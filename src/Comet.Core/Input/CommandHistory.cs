namespace Comet.Core.Input;

/// <summary>Input-line history with Up/Down navigation. Kept in memory for the life of a session.</summary>
public sealed class CommandHistory(int capacity = 1000)
{
    private readonly List<string> _entries = [];
    private readonly int _capacity = Math.Max(1, capacity);
    private int _cursor;
    private string _draft = string.Empty;

    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Records a sent line. Blank lines and immediate repeats are not stored.</summary>
    public void Add(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!string.IsNullOrWhiteSpace(line) && (_entries.Count == 0 || _entries[^1] != line))
        {
            _entries.Add(line);
            if (_entries.Count > _capacity)
            {
                _entries.RemoveAt(0);
            }
        }
        ResetNavigation();
    }

    public void ResetNavigation()
    {
        _cursor = _entries.Count;
        _draft = string.Empty;
    }

    /// <summary>Steps back one entry. Returns null at the oldest entry.</summary>
    public string? Previous(string currentText)
    {
        if (_cursor == _entries.Count)
        {
            _draft = currentText ?? string.Empty;
        }
        if (_cursor == 0)
        {
            return null;
        }
        return _entries[--_cursor];
    }

    /// <summary>Steps forward one entry. Past the newest, returns what was being typed before navigating.</summary>
    public string? Next()
    {
        if (_cursor >= _entries.Count)
        {
            return null;
        }
        _cursor++;
        return _cursor == _entries.Count ? _draft : _entries[_cursor];
    }
}
