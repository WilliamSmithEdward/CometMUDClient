using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Comet.App.Rendering;
using Comet.Core.Configuration;
using Comet.Core.Text;
using TextLine = Comet.Core.Text.TextLine;

namespace Comet.App.Controls;

/// <summary>
/// Draws a <see cref="ScrollbackBuffer"/> as wrapped, colored terminal text, newest line at
/// the bottom, with scrollback, mouse selection and clickable links.
/// </summary>
public sealed partial class OutputView : Control
{
    private const double PaddingX = 6;
    private const double PaddingY = 4;
    private const int MaxCachedLayouts = 600;

    private static readonly IImmutableSolidColorBrush SelectionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x33, 0x66, 0xCC));
    private static readonly Cursor LinkCursor = new(StandardCursorType.Hand);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    private readonly Dictionary<TextLine, CachedLine> _layouts = new(ReferenceEqualityComparer.Instance);
    private readonly List<Row> _rows = [];

    private ScrollbackBuffer? _buffer;
    private TerminalPalette _palette = new(new AppSettings());
    private Typeface _typeface = new(AppSettings.DefaultFontFamily);
    private Typeface _boldTypeface;
    private Typeface _italicTypeface;
    private Typeface _boldItalicTypeface;
    private double _fontSize = 13;
    private bool _wordWrap = true;
    private double _layoutWidth = -1;
    private Size _cellSize;

    private long? _anchorLine;
    private TextPoint? _selectionAnchor;
    private TextPoint? _selectionEnd;
    private bool _selecting;
    private Point _pressPoint;

    public OutputView()
    {
        ClipToBounds = true;
        Cursor = TextCursor;
        _boldTypeface = _italicTypeface = _boldItalicTypeface = _typeface;
    }

    /// <summary>When true the view always shows the newest lines and cannot be scrolled.</summary>
    public bool AlwaysFollow { get; set; }

    /// <summary>The scroll position changed, or lines were added while scrolled back.</summary>
    public event Action? ScrollChanged;

    /// <summary>The user clicked a link in the output.</summary>
    public event Action<Uri>? LinkActivated;

    public event Action? SelectionChanged;

    public ScrollbackBuffer? Buffer
    {
        get => _buffer;
        set
        {
            if (ReferenceEquals(_buffer, value))
            {
                return;
            }
            _buffer = value;
            _layouts.Clear();
            _anchorLine = null;
            ClearSelection();
            InvalidateVisual();
        }
    }

    public bool IsScrolledBack => _anchorLine is not null;

    public bool HasSelection => _selectionAnchor is { } a && _selectionEnd is { } b && a != b;

    /// <summary>Index of the line drawn at the bottom of the view.</summary>
    public int BottomIndex
    {
        get
        {
            if (_buffer is null || _buffer.Count == 0)
            {
                return 0;
            }
            return _anchorLine is { } anchor
                ? (int)Math.Clamp(anchor - _buffer.DroppedLines, 0, _buffer.Count - 1)
                : _buffer.Count - 1;
        }
    }

    /// <summary>Number of buffer lines on screen, for sizing a scroll bar thumb.</summary>
    public int VisibleLineCount => Math.Max(1, _rows.Count);

    /// <summary>Size of the view in character cells, as reported to the MUD.</summary>
    public (int Columns, int Rows) CellDimensions
    {
        get
        {
            if (_cellSize.Width <= 0 || _cellSize.Height <= 0)
            {
                return (80, 24);
            }
            int columns = (int)((Bounds.Width - (2 * PaddingX)) / _cellSize.Width);
            int rows = (int)((Bounds.Height - (2 * PaddingY)) / _cellSize.Height);
            return (Math.Clamp(columns, 20, 1000), Math.Clamp(rows, 4, 1000));
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _palette = new TerminalPalette(settings);
        var family = new FontFamily(settings.FontFamily);
        FontWeight boldWeight = _palette.BoldUsesWeight ? FontWeight.Bold : FontWeight.Normal;
        _typeface = new Typeface(family);
        _boldTypeface = new Typeface(family, FontStyle.Normal, boldWeight);
        _italicTypeface = new Typeface(family, FontStyle.Italic);
        _boldItalicTypeface = new Typeface(family, FontStyle.Italic, boldWeight);
        _fontSize = settings.FontSize;
        _wordWrap = settings.WordWrap;

        using var probe = new TextLayout("0000000000", _typeface, _fontSize, _palette.ForegroundBrush);
        _cellSize = new Size(probe.Width / 10, probe.Height);

        _layouts.Clear();
        InvalidateVisual();
    }

    /// <summary>Call when the buffer's contents changed.</summary>
    public void NotifyBufferChanged()
    {
        if (_layouts.Count > MaxCachedLayouts)
        {
            _layouts.Clear();
        }
        InvalidateVisual();
        if (IsScrolledBack)
        {
            ScrollChanged?.Invoke();
        }
    }

    // ---- Scrolling -------------------------------------------------------------------

    /// <summary>Scrolls by buffer lines. Positive values go back in history.</summary>
    public void ScrollLines(int lines) => SetBottomIndex(BottomIndex - lines);

    public void ScrollPages(int pages) => ScrollLines(pages * Math.Max(1, VisibleLineCount - 1));

    public void ScrollToTop() => SetBottomIndex(0);

    public void ScrollToBottom() => SetBottomIndex(int.MaxValue);

    public void SetBottomIndex(int index)
    {
        if (_buffer is null || AlwaysFollow)
        {
            return;
        }
        long? anchor = index >= _buffer.Count - 1 ? null : _buffer.DroppedLines + Math.Max(0, index);
        if (anchor != _anchorLine)
        {
            _anchorLine = anchor;
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    // ---- Selection and search --------------------------------------------------------

    public void ClearSelection()
    {
        if (_selectionAnchor is null && _selectionEnd is null)
        {
            return;
        }
        _selectionAnchor = null;
        _selectionEnd = null;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void SelectAll()
    {
        if (_buffer is null || _buffer.Count == 0)
        {
            return;
        }
        _selectionAnchor = new TextPoint(_buffer.DroppedLines, 0);
        _selectionEnd = new TextPoint(_buffer.DroppedLines + _buffer.Count - 1, _buffer[_buffer.Count - 1].Length);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public string GetSelectedText()
    {
        if (_buffer is null || !TryGetSelection(out TextPoint start, out TextPoint end))
        {
            return string.Empty;
        }
        var text = new StringBuilder();
        long first = Math.Max(start.Line, _buffer.DroppedLines);
        long last = Math.Min(end.Line, _buffer.DroppedLines + _buffer.Count - 1);
        for (long absolute = first; absolute <= last; absolute++)
        {
            string line = _buffer[(int)(absolute - _buffer.DroppedLines)].Text;
            int from = absolute == start.Line ? Math.Min(start.Column, line.Length) : 0;
            int to = absolute == end.Line ? Math.Min(end.Column, line.Length) : line.Length;
            text.Append(line, from, Math.Max(0, to - from));
            if (absolute < last)
            {
                text.Append(Environment.NewLine);
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// Searches backwards from the current match (or the bottom) for <paramref name="text"/>,
    /// ignoring case. Selects and scrolls to the match. Returns false when there is none.
    /// </summary>
    public bool FindPrevious(string text) => Find(text, forward: false);

    public bool FindNext(string text) => Find(text, forward: true);

    private bool Find(string text, bool forward)
    {
        if (_buffer is null || _buffer.Count == 0 || string.IsNullOrEmpty(text))
        {
            return false;
        }

        long dropped = _buffer.DroppedLines;
        int line;
        int column;
        if (TryGetSelection(out TextPoint start, out TextPoint end) && start.Line >= dropped)
        {
            line = (int)((forward ? end.Line : start.Line) - dropped);
            column = forward ? end.Column : start.Column - 1;
        }
        else
        {
            line = forward ? 0 : _buffer.Count - 1;
            column = forward ? 0 : int.MaxValue;
        }

        for (; line >= 0 && line < _buffer.Count; line += forward ? 1 : -1)
        {
            string candidate = _buffer[line].Text;
            int found = -1;
            if (forward)
            {
                if (column <= candidate.Length)
                {
                    found = candidate.IndexOf(text, Math.Max(0, column), StringComparison.OrdinalIgnoreCase);
                }
                column = 0;
            }
            else
            {
                int startAt = Math.Min(column, candidate.Length - 1);
                if (startAt >= 0 && candidate.Length >= text.Length)
                {
                    found = candidate.LastIndexOf(text, Math.Min(candidate.Length - 1, startAt + text.Length - 1), StringComparison.OrdinalIgnoreCase);
                    if (found > startAt)
                    {
                        found = -1;
                    }
                }
                column = int.MaxValue;
            }

            if (found >= 0)
            {
                _selectionAnchor = new TextPoint(dropped + line, found);
                _selectionEnd = new TextPoint(dropped + line, found + text.Length);
                SelectionChanged?.Invoke();
                if (!_rows.Exists(row => row.Index == line))
                {
                    SetBottomIndex(Math.Min(_buffer.Count - 1, line + Math.Max(1, VisibleLineCount / 2)));
                }
                InvalidateVisual();
                return true;
            }
        }
        return false;
    }

    private bool TryGetSelection(out TextPoint start, out TextPoint end)
    {
        start = default;
        end = default;
        if (_selectionAnchor is not { } a || _selectionEnd is not { } b || a == b)
        {
            return false;
        }
        (start, end) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        return true;
    }

    // ---- Rendering -------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.FillRectangle(_palette.BackgroundBrush, new Rect(Bounds.Size));
        _rows.Clear();
        if (_buffer is null || _buffer.Count == 0)
        {
            return;
        }

        double width = Math.Max(40, Bounds.Width - (2 * PaddingX));
        if (Math.Abs(width - _layoutWidth) > 0.5)
        {
            _layoutWidth = width;
            _layouts.Clear();
        }

        double y = Bounds.Height - PaddingY;
        for (int i = BottomIndex; i >= 0 && y > 0; i--)
        {
            CachedLine cached = GetLayout(_buffer[i]);
            y -= cached.Layout.Height;
            _rows.Add(new Row(i, y, cached));
        }

        // A buffer shorter than the view starts at the top, as a terminal does.
        double shift = y > PaddingY ? y - PaddingY : 0;
        bool hasSelection = TryGetSelection(out TextPoint selectionStart, out TextPoint selectionEnd);
        long dropped = _buffer.DroppedLines;

        for (int r = 0; r < _rows.Count; r++)
        {
            Row row = _rows[r] with { Top = _rows[r].Top - shift };
            _rows[r] = row;
            var origin = new Point(PaddingX, row.Top);
            TextLine line = _buffer[row.Index];
            TextLayout layout = row.Line.Layout;

            foreach (StyleRun run in line.Runs)
            {
                if (_palette.Resolve(run.Style).Background is { } background)
                {
                    FillRange(context, layout, origin, run.Start, run.Length, background);
                }
            }

            if (hasSelection)
            {
                long absolute = dropped + row.Index;
                if (absolute >= selectionStart.Line && absolute <= selectionEnd.Line)
                {
                    int from = absolute == selectionStart.Line ? Math.Min(selectionStart.Column, line.Length) : 0;
                    int to = absolute == selectionEnd.Line ? Math.Min(selectionEnd.Column, line.Length) : line.Length;
                    if (to > from)
                    {
                        FillRange(context, layout, origin, from, to - from, SelectionBrush);
                    }
                    else if (line.Length == 0 && absolute != selectionEnd.Line)
                    {
                        context.FillRectangle(SelectionBrush, new Rect(origin.X, origin.Y, _cellSize.Width, layout.Height));
                    }
                }
            }

            layout.Draw(context, origin);

            foreach ((int start, int length, _) in row.Line.Links)
            {
                foreach (Rect rect in layout.HitTestTextRange(start, length))
                {
                    double underlineY = Math.Round(origin.Y + rect.Bottom - 1) + 0.5;
                    context.DrawLine(new ImmutablePen(_palette.ForegroundBrush, 1), new Point(origin.X + rect.Left, underlineY), new Point(origin.X + rect.Right, underlineY));
                }
            }
        }
    }

    private static void FillRange(DrawingContext context, TextLayout layout, Point origin, int start, int length, IBrush brush)
    {
        foreach (Rect rect in layout.HitTestTextRange(start, length))
        {
            context.FillRectangle(brush, rect.Translate(origin));
        }
    }

    private CachedLine GetLayout(TextLine line)
    {
        if (_layouts.TryGetValue(line, out CachedLine? cached))
        {
            return cached;
        }

        List<ValueSpan<TextRunProperties>>? overrides = null;
        if (line.Runs.Length > 1 || (line.Runs.Length == 1 && line.Runs[0].Style != TextStyle.Default))
        {
            overrides = new List<ValueSpan<TextRunProperties>>(line.Runs.Length);
            foreach (StyleRun run in line.Runs)
            {
                TextStyle style = run.Style;
                bool bold = style.Has(TextAttributes.Bold);
                bool italic = style.Has(TextAttributes.Italic);
                Typeface typeface = bold ? (italic ? _boldItalicTypeface : _boldTypeface) : (italic ? _italicTypeface : _typeface);
                TextDecorationCollection? decorations = null;
                if (style.Has(TextAttributes.Underline) || style.Has(TextAttributes.Strikethrough))
                {
                    decorations = [];
                    if (style.Has(TextAttributes.Underline))
                    {
                        decorations.AddRange(TextDecorations.Underline);
                    }
                    if (style.Has(TextAttributes.Strikethrough))
                    {
                        decorations.AddRange(TextDecorations.Strikethrough);
                    }
                }
                overrides.Add(new ValueSpan<TextRunProperties>(run.Start, run.Length,
                    new GenericTextRunProperties(typeface, _fontSize, decorations, _palette.Resolve(style).Foreground)));
            }
        }

        // An empty line still needs the height of one row.
        string text = line.Length == 0 ? " " : line.Text;
        var layout = new TextLayout(
            text,
            _typeface,
            _fontSize,
            _palette.ForegroundBrush,
            TextAlignment.Left,
            _wordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming.None,
            null,
            FlowDirection.LeftToRight,
            _layoutWidth,
            double.PositiveInfinity,
            double.NaN,
            0,
            0,
            null,
            overrides);

        List<(int Start, int Length, Uri Target)> links = [];
        if (line.Text.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in LinkPattern().Matches(line.Text))
            {
                string candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    links.Add((match.Index, candidate.Length, uri));
                }
            }
        }

        cached = new CachedLine(layout, links);
        _layouts[line] = cached;
        return cached;
    }

    // Only http and https links are recognized, so text from a server can never launch
    // another kind of handler.
    [GeneratedRegex(@"\bhttps?://[^\s<>""'\)\]\}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex LinkPattern();

    // ---- Pointer ---------------------------------------------------------------------

    private bool TryHit(Point point, out Row row, out int column)
    {
        row = default;
        column = 0;
        if (_buffer is null || _rows.Count == 0)
        {
            return false;
        }

        // Rows are stored bottom-up. Points above or below the text clamp to the nearest row.
        row = _rows[^1];
        foreach (Row candidate in _rows)
        {
            if (point.Y >= candidate.Top)
            {
                row = candidate;
                break;
            }
        }
        if (row.Index >= _buffer.Count)
        {
            return false;
        }

        TextHitTestResult hit = row.Line.Layout.HitTestPoint(new Point(point.X - PaddingX, Math.Max(0, point.Y - row.Top)));
        column = Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, _buffer[row.Index].Length);
        return true;
    }

    private Uri? LinkAt(Point point)
    {
        if (!TryHit(point, out Row row, out int column) || point.Y > row.Top + row.Line.Layout.Height)
        {
            return null;
        }
        foreach ((int start, int length, Uri target) in row.Line.Links)
        {
            if (column >= start && column < start + length)
            {
                return target;
            }
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || _buffer is null || !TryHit(point.Position, out Row row, out int column))
        {
            return;
        }

        long absolute = _buffer.DroppedLines + row.Index;
        string text = _buffer[row.Index].Text;
        _pressPoint = point.Position;

        if (e.ClickCount == 2)
        {
            int start = Math.Min(column, Math.Max(0, text.Length - 1));
            int end = start;
            while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            {
                start--;
            }
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }
            _selectionAnchor = new TextPoint(absolute, start);
            _selectionEnd = new TextPoint(absolute, end);
        }
        else if (e.ClickCount >= 3)
        {
            _selectionAnchor = new TextPoint(absolute, 0);
            _selectionEnd = new TextPoint(absolute, text.Length);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selectionAnchor is not null)
        {
            _selectionEnd = new TextPoint(absolute, column);
            _selecting = true;
        }
        else
        {
            _selectionAnchor = new TextPoint(absolute, column);
            _selectionEnd = _selectionAnchor;
            _selecting = true;
        }

        if (_selecting)
        {
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
        SelectionChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (_selecting && _buffer is not null)
        {
            // Dragging past an edge scrolls, so a selection can extend beyond the view.
            if (position.Y < 0)
            {
                ScrollLines(1);
            }
            else if (position.Y > Bounds.Height)
            {
                ScrollLines(-1);
            }
            if (TryHit(position, out Row row, out int column))
            {
                _selectionEnd = new TextPoint(_buffer.DroppedLines + row.Index, column);
                InvalidateVisual();
                SelectionChanged?.Invoke();
            }
            return;
        }
        Cursor = LinkAt(position) is null ? TextCursor : LinkCursor;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (!_selecting)
        {
            return;
        }
        _selecting = false;
        e.Pointer.Capture(null);

        Point position = e.GetPosition(this);
        bool wasClick = Math.Abs(position.X - _pressPoint.X) < 3 && Math.Abs(position.Y - _pressPoint.Y) < 3;
        if (wasClick && !HasSelection)
        {
            ClearSelection();
            if (LinkAt(position) is { } link)
            {
                LinkActivated?.Invoke(link);
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (AlwaysFollow || e.Delta.Y == 0)
        {
            return;
        }
        ScrollLines((int)Math.Round(e.Delta.Y * 3, MidpointRounding.AwayFromZero));
        e.Handled = true;
    }

    private readonly record struct TextPoint(long Line, int Column) : IComparable<TextPoint>
    {
        public int CompareTo(TextPoint other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);
    }

    private sealed record CachedLine(TextLayout Layout, List<(int Start, int Length, Uri Target)> Links);

    private readonly record struct Row(int Index, double Top, CachedLine Line);
}
