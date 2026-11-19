namespace Comet.Core.Text;

/// <summary>
/// A bounded ring of output lines. Not thread-safe: the owning session and its view touch it
/// from one thread.
/// </summary>
public sealed class ScrollbackBuffer
{
    public const int MinCapacity = 100;
    public const int MaxCapacity = 1_000_000;

    private TextLine[] _lines;
    private int _head;
    private int _count;

    public ScrollbackBuffer(int capacity = 20_000)
    {
        _lines = new TextLine[Math.Clamp(capacity, MinCapacity, MaxCapacity)];
    }

    public int Capacity => _lines.Length;

    public int Count => _count;

    /// <summary>Total lines ever dropped from the front. Lets a view keep its place as lines scroll out.</summary>
    public long DroppedLines { get; private set; }

    /// <summary>Incremented on every change, so a view can tell whether it is stale.</summary>
    public long Version { get; private set; }

    public event Action? Changed;

    public TextLine this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _lines[(_head + index) % _lines.Length];
        }
    }

    public void Append(TextLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (_count == _lines.Length)
        {
            _lines[_head] = line;
            _head = (_head + 1) % _lines.Length;
            DroppedLines++;
        }
        else
        {
            _lines[(_head + _count) % _lines.Length] = line;
            _count++;
        }
        Touch();
    }

    /// <summary>Replaces the newest line, or appends when the buffer is empty.</summary>
    public void ReplaceLast(TextLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (_count == 0)
        {
            Append(line);
            return;
        }
        _lines[(_head + _count - 1) % _lines.Length] = line;
        Touch();
    }

    /// <summary>Removes the newest line, if there is one.</summary>
    public void RemoveLast()
    {
        if (_count == 0)
        {
            return;
        }
        _count--;
        _lines[(_head + _count) % _lines.Length] = null!;
        Touch();
    }

    public void Clear()
    {
        DroppedLines += _count;
        Array.Clear(_lines);
        _head = 0;
        _count = 0;
        Touch();
    }

    /// <summary>Changes the capacity, keeping the newest lines that fit.</summary>
    public void Resize(int capacity)
    {
        capacity = Math.Clamp(capacity, MinCapacity, MaxCapacity);
        if (capacity == _lines.Length)
        {
            return;
        }
        int keep = Math.Min(_count, capacity);
        var resized = new TextLine[capacity];
        int skip = _count - keep;
        for (int i = 0; i < keep; i++)
        {
            resized[i] = this[skip + i];
        }
        DroppedLines += skip;
        _lines = resized;
        _head = 0;
        _count = keep;
        Touch();
    }

    private void Touch()
    {
        Version++;
        Changed?.Invoke();
    }
}
