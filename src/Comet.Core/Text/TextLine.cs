using System.Text;

namespace Comet.Core.Text;

/// <summary>
/// One immutable line of output: plain text plus the style runs that cover it end to end.
/// </summary>
public sealed class TextLine
{
    public static readonly TextLine Empty = new(string.Empty, []);

    private readonly StyleRun[] _runs;

    internal TextLine(string text, StyleRun[] runs)
    {
        Text = text;
        _runs = runs;
    }

    public string Text { get; }

    /// <summary>Contiguous, non-empty, ordered runs covering all of <see cref="Text"/>.</summary>
    public ReadOnlySpan<StyleRun> Runs => _runs;

    public int Length => Text.Length;

    public static TextLine Plain(string text) => Styled(text, TextStyle.Default);

    public static TextLine Styled(string text, TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? Empty : new TextLine(text, [new StyleRun(0, text.Length, style)]);
    }

    public TextStyle StyleAt(int index)
    {
        foreach (StyleRun run in _runs)
        {
            if (index < run.End)
            {
                return run.Style;
            }
        }
        return _runs.Length > 0 ? _runs[^1].Style : TextStyle.Default;
    }

    /// <summary>Returns a copy with <paramref name="restyle"/> applied to the given range.</summary>
    public TextLine WithStyle(int start, int length, Func<TextStyle, TextStyle> restyle)
    {
        ArgumentNullException.ThrowIfNull(restyle);
        start = Math.Clamp(start, 0, Text.Length);
        int end = Math.Clamp(start + length, start, Text.Length);
        if (end == start)
        {
            return this;
        }

        var builder = new TextLineBuilder();
        foreach (StyleRun run in _runs)
        {
            int a = Math.Max(run.Start, start);
            int b = Math.Min(run.End, end);
            if (a >= b)
            {
                builder.Append(Text.AsSpan(run.Start, run.Length), run.Style);
                continue;
            }
            builder.Append(Text.AsSpan(run.Start, a - run.Start), run.Style);
            builder.Append(Text.AsSpan(a, b - a), restyle(run.Style));
            builder.Append(Text.AsSpan(b, run.End - b), run.Style);
        }
        return builder.ToLine();
    }

    /// <summary>Returns a copy with the range replaced by <paramref name="replacement"/>.</summary>
    public TextLine Replace(int start, int length, TextLine replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        start = Math.Clamp(start, 0, Text.Length);
        int end = Math.Clamp(start + length, start, Text.Length);
        var builder = new TextLineBuilder();
        builder.Append(Slice(0, start));
        builder.Append(replacement);
        builder.Append(Slice(end, Text.Length - end));
        return builder.ToLine();
    }

    public TextLine Slice(int start, int length)
    {
        start = Math.Clamp(start, 0, Text.Length);
        int end = Math.Clamp(start + length, start, Text.Length);
        if (start == 0 && end == Text.Length)
        {
            return this;
        }
        var builder = new TextLineBuilder();
        foreach (StyleRun run in _runs)
        {
            int a = Math.Max(run.Start, start);
            int b = Math.Min(run.End, end);
            if (a < b)
            {
                builder.Append(Text.AsSpan(a, b - a), run.Style);
            }
        }
        return builder.ToLine();
    }

    public TextLine Append(TextLine other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Length == 0)
        {
            return this;
        }
        if (Length == 0)
        {
            return other;
        }
        var builder = new TextLineBuilder();
        builder.Append(this);
        builder.Append(other);
        return builder.ToLine();
    }

    public override string ToString() => Text;
}

/// <summary>Accumulates styled text and merges adjacent runs that share a style.</summary>
public sealed class TextLineBuilder
{
    private readonly StringBuilder _text = new();
    private readonly List<StyleRun> _runs = [];

    public int Length => _text.Length;

    public void Append(char c, TextStyle style) => Append(new ReadOnlySpan<char>(in c), style);

    public void Append(ReadOnlySpan<char> text, TextStyle style)
    {
        if (text.IsEmpty)
        {
            return;
        }
        int start = _text.Length;
        _text.Append(text);
        if (_runs.Count > 0 && _runs[^1].Style == style)
        {
            StyleRun last = _runs[^1];
            _runs[^1] = last with { Length = last.Length + text.Length };
        }
        else
        {
            _runs.Add(new StyleRun(start, text.Length, style));
        }
    }

    public void Append(TextLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        foreach (StyleRun run in line.Runs)
        {
            Append(line.Text.AsSpan(run.Start, run.Length), run.Style);
        }
    }

    /// <summary>Removes the last character, if there is one.</summary>
    public void Backspace()
    {
        if (_text.Length == 0)
        {
            return;
        }
        _text.Length--;
        StyleRun last = _runs[^1];
        if (last.Length == 1)
        {
            _runs.RemoveAt(_runs.Count - 1);
        }
        else
        {
            _runs[^1] = last with { Length = last.Length - 1 };
        }
    }

    public void Clear()
    {
        _text.Clear();
        _runs.Clear();
    }

    public TextLine ToLine() => _text.Length == 0 ? TextLine.Empty : new TextLine(_text.ToString(), [.. _runs]);
}
