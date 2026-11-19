namespace Comet.Core.Text;

/// <summary>
/// Converts a decoded character stream containing ANSI/VT100 escape sequences into styled
/// lines. State is kept between calls so sequences and lines may span network reads.
/// </summary>
/// <remarks>
/// Only SGR (color and attribute) sequences change output. Cursor movement and erase
/// sequences are consumed and dropped, because a scrolling MUD view has no addressable screen.
/// </remarks>
public sealed class AnsiParser
{
    /// <summary>A line longer than this is broken, so a server cannot grow one line without bound.</summary>
    public const int MaxLineLength = 16 * 1024;

    private const int MaxSequenceLength = 256;
    private const int TabWidth = 8;

    private enum State { Text, Escape, Csi, Osc, OscEscape, SkipOne }

    private readonly TextLineBuilder _line = new();
    private readonly List<char> _sequence = [];
    private State _state = State.Text;
    private TextStyle _style = TextStyle.Default;
    private bool _pendingCarriageReturn;

    /// <summary>Raised for each line terminated by a line feed.</summary>
    public event Action<TextLine>? LineCompleted;

    /// <summary>Raised when the server sends BEL outside an escape sequence.</summary>
    public event Action? Bell;

    /// <summary>True when text has arrived since the last completed line.</summary>
    public bool HasPartialLine => _line.Length > 0;

    /// <summary>The unterminated text received so far, typically a prompt.</summary>
    public TextLine PartialLine => _line.ToLine();

    /// <summary>The style that will apply to the next character.</summary>
    public TextStyle CurrentStyle => _style;

    /// <summary>Parses one self-contained string into a single styled line. Line feeds are dropped.</summary>
    public static TextLine ParseLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new AnsiParser();
        var builder = new TextLineBuilder();
        parser.LineCompleted += builder.Append;
        parser.Feed(text);
        builder.Append(parser.PartialLine);
        return builder.ToLine();
    }

    /// <summary>Completes the unterminated line, as if a line feed had arrived.</summary>
    public void FlushPartialLine()
    {
        if (_line.Length > 0)
        {
            EmitLine();
        }
    }

    /// <summary>Discards the unterminated line without emitting it.</summary>
    public void DiscardPartialLine()
    {
        _line.Clear();
        _pendingCarriageReturn = false;
    }

    public void Feed(ReadOnlySpan<char> input)
    {
        foreach (char c in input)
        {
            switch (_state)
            {
                case State.Text:
                    FeedText(c);
                    break;

                case State.Escape:
                    _state = c switch
                    {
                        '[' => State.Csi,
                        ']' => State.Osc,
                        // Charset designation: ESC ( B and friends carry one more byte.
                        '(' or ')' or '*' or '+' or '#' or '%' => State.SkipOne,
                        _ => State.Text,
                    };
                    _sequence.Clear();
                    break;

                case State.SkipOne:
                    _state = State.Text;
                    break;

                case State.Csi:
                    if (c is >= '@' and <= '~')
                    {
                        if (c == 'm')
                        {
                            ApplySgr();
                        }
                        _state = State.Text;
                    }
                    else if (c == '\x1b')
                    {
                        _state = State.Escape;
                    }
                    else if (_sequence.Count < MaxSequenceLength)
                    {
                        _sequence.Add(c);
                    }
                    else
                    {
                        _state = State.Text;
                    }
                    break;

                case State.Osc:
                    if (c == '\a')
                    {
                        _state = State.Text;
                    }
                    else if (c == '\x1b')
                    {
                        _state = State.OscEscape;
                    }
                    else if (c == '\n')
                    {
                        // An unterminated OSC must not swallow the rest of the session.
                        _state = State.Text;
                        FeedText(c);
                    }
                    break;

                case State.OscEscape:
                    _state = c == '\\' ? State.Text : State.Osc;
                    break;
            }
        }
    }

    private void FeedText(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                return;
            case '\n':
                _pendingCarriageReturn = false;
                EmitLine();
                return;
            case '\r':
                // Decide when the next character arrives: CR LF is a line end, while a bare
                // CR followed by text overwrites the line from column zero.
                _pendingCarriageReturn = true;
                return;
            case '\a':
                Bell?.Invoke();
                return;
            case '\b':
                _line.Backspace();
                return;
            case '\t':
                ClearIfCarriageReturned();
                int spaces = TabWidth - (_line.Length % TabWidth);
                _line.Append("        ".AsSpan(0, spaces), _style);
                break;
            default:
                if (c < ' ' || c == '\x7f')
                {
                    return;
                }
                ClearIfCarriageReturned();
                _line.Append(c, _style);
                break;
        }

        if (_line.Length >= MaxLineLength)
        {
            EmitLine();
        }
    }

    private void ClearIfCarriageReturned()
    {
        if (_pendingCarriageReturn)
        {
            _pendingCarriageReturn = false;
            _line.Clear();
        }
    }

    private void EmitLine()
    {
        TextLine line = _line.ToLine();
        _line.Clear();
        LineCompleted?.Invoke(line);
    }

    private void ApplySgr()
    {
        // Private-mode sequences such as ESC[?25m are not SGR.
        if (_sequence.Count > 0 && _sequence[0] is '?' or '>' or '<' or '=')
        {
            return;
        }

        Span<int> values = stackalloc int[32];
        Span<bool> colon = stackalloc bool[32];
        int count = 0;
        int current = 0;
        bool any = false;
        bool nextIsColon = false;

        foreach (char c in _sequence)
        {
            if (c is >= '0' and <= '9')
            {
                current = Math.Min((current * 10) + (c - '0'), 9999);
                any = true;
            }
            else if (c is ';' or ':')
            {
                if (count < values.Length)
                {
                    values[count] = current;
                    colon[count] = nextIsColon;
                    count++;
                }
                nextIsColon = c == ':';
                current = 0;
                any = false;
            }
        }
        if ((any || count == 0 || _sequence.Count > 0) && count < values.Length)
        {
            values[count] = current;
            colon[count] = nextIsColon;
            count++;
        }

        for (int i = 0; i < count; i++)
        {
            int code = values[i];
            switch (code)
            {
                case 0: _style = TextStyle.Default; break;
                case 1: _style = _style.With(TextAttributes.Bold); break;
                case 2: _style = _style.With(TextAttributes.Faint); break;
                case 3: _style = _style.With(TextAttributes.Italic); break;
                case 4 or 21: _style = _style.With(TextAttributes.Underline); break;
                case 5 or 6: _style = _style.With(TextAttributes.Blink); break;
                case 7: _style = _style.With(TextAttributes.Inverse); break;
                case 8: _style = _style.With(TextAttributes.Conceal); break;
                case 9: _style = _style.With(TextAttributes.Strikethrough); break;
                case 22: _style = _style.Without(TextAttributes.Bold | TextAttributes.Faint); break;
                case 23: _style = _style.Without(TextAttributes.Italic); break;
                case 24: _style = _style.Without(TextAttributes.Underline); break;
                case 25: _style = _style.Without(TextAttributes.Blink); break;
                case 27: _style = _style.Without(TextAttributes.Inverse); break;
                case 28: _style = _style.Without(TextAttributes.Conceal); break;
                case 29: _style = _style.Without(TextAttributes.Strikethrough); break;
                case >= 30 and <= 37: _style = _style with { Foreground = TextColor.Palette(code - 30) }; break;
                case 39: _style = _style with { Foreground = TextColor.Default }; break;
                case >= 40 and <= 47: _style = _style with { Background = TextColor.Palette(code - 40) }; break;
                case 49: _style = _style with { Background = TextColor.Default }; break;
                case >= 90 and <= 97: _style = _style with { Foreground = TextColor.Palette(code - 90 + 8) }; break;
                case >= 100 and <= 107: _style = _style with { Background = TextColor.Palette(code - 100 + 8) }; break;
                case 38 or 48:
                    if (TryReadExtendedColor(values[..count], colon[..count], ref i, out TextColor color))
                    {
                        _style = code == 38 ? _style with { Foreground = color } : _style with { Background = color };
                    }
                    break;
            }
        }
    }

    private static bool TryReadExtendedColor(ReadOnlySpan<int> values, ReadOnlySpan<bool> colon, ref int i, out TextColor color)
    {
        color = TextColor.Default;
        if (i + 1 >= values.Length)
        {
            i = values.Length;
            return false;
        }

        int mode = values[i + 1];
        if (mode == 5)
        {
            if (i + 2 >= values.Length)
            {
                i = values.Length;
                return false;
            }
            color = TextColor.Palette(values[i + 2]);
            i += 2;
            return true;
        }

        if (mode == 2)
        {
            // The ITU colon form carries a color-space id before the components
            // (38:2:<cs>:r:g:b). Count the colon-joined parameters to tell the forms apart.
            int joined = 0;
            for (int k = i + 1; k < values.Length && colon[k]; k++)
            {
                joined++;
            }
            int first = joined >= 5 ? i + 3 : i + 2;
            if (first + 2 >= values.Length)
            {
                i = values.Length;
                return false;
            }
            color = TextColor.Rgb(values[first], values[first + 1], values[first + 2]);
            i = first + 2;
            return true;
        }

        i = values.Length;
        return false;
    }
}
