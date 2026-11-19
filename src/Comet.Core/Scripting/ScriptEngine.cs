using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Comet.Core.Text;

namespace Comet.Core.Scripting;

/// <summary>
/// The TinTin-style interpreter for one session: aliases, actions, substitutions, gags,
/// highlights, variables, tickers and the # commands that manage them.
/// </summary>
/// <remarks>Not thread-safe. The owning session calls it from a single thread.</remarks>
public sealed partial class ScriptEngine
{
    /// <summary>Nesting limit for aliases and commands that run other commands.</summary>
    public const int MaxDepth = 50;

    /// <summary>Commands one piece of input may run before it is stopped as a runaway.</summary>
    public const int MaxOperations = 20_000;

    public const int DefaultActionPriority = 5;

    public static readonly TextStyle InfoStyle = new(TextColor.Palette(14), TextColor.Default, TextAttributes.None);
    public static readonly TextStyle ErrorStyle = new(TextColor.Palette(9), TextColor.Default, TextAttributes.None);

    private readonly IScriptHost _host;
    private readonly TimeProvider _time;
    private readonly Dictionary<ScriptItemKind, List<ScriptItem>> _items = [];
    private readonly Dictionary<string, TriggerPattern?> _patternCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HighlightStyle?> _highlightCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _tickerDue = new(StringComparer.Ordinal);
    private readonly List<(DateTimeOffset Due, string Commands)> _delays = [];
    private readonly Queue<string> _pendingActions = new();
    private readonly List<string> _path = [];

    private int _running;
    private int _operations;
    private bool _aborted;
    private int _quiet;
    private bool _tickEnabled;
    private int _tickSize = 60;
    private DateTimeOffset _nextTick;
    private bool _tickWarned;

    public ScriptEngine(IScriptHost host, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _time = timeProvider ?? TimeProvider.System;
        foreach (ScriptItemKind kind in Enum.GetValues<ScriptItemKind>())
        {
            _items[kind] = [];
        }
    }

    public char CommandChar { get; set; } = '#';
    public char Separator { get; set; } = ';';

    /// <summary>When on, typed input goes to the MUD untouched.</summary>
    public bool Verbatim { get; set; }

    public bool SpeedwalkEnabled { get; set; } = true;

    /// <summary>When on, actions do not fire.</summary>
    public bool IgnoreActions { get; set; }

    /// <summary>Confirmation messages such as "#Ok. Alias set." are shown.</summary>
    public bool ShowMessages { get; set; } = true;

    /// <summary>Raised after any script item is added, changed or removed.</summary>
    public event Action? ItemsChanged;

    // ---- Items -----------------------------------------------------------------------

    public IReadOnlyList<ScriptItem> GetItems(ScriptItemKind kind) => _items[kind];

    /// <summary>Adds an item, or replaces the one with the same kind and key.</summary>
    public void SetItem(ScriptItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        List<ScriptItem> list = _items[item.Kind];
        int index = list.FindIndex(existing => existing.Key == item.Key);
        if (index >= 0)
        {
            list[index] = item;
        }
        else
        {
            list.Add(item);
        }

        switch (item.Kind)
        {
            case ScriptItemKind.Alias or ScriptItemKind.Variable or ScriptItemKind.TabWord:
                list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                break;
            case ScriptItemKind.Action:
                // List.Sort is unstable; order by priority while keeping definition order.
                ScriptItem[] ordered = [.. list.OrderBy(static a => PriorityOf(a))];
                list.Clear();
                list.AddRange(ordered);
                break;
            case ScriptItemKind.Ticker:
                _tickerDue[item.Key] = _time.GetUtcNow() + TickerInterval(item);
                break;
        }
        ItemsChanged?.Invoke();
    }

    public bool RemoveItem(ScriptItemKind kind, string key)
    {
        int removed = _items[kind].RemoveAll(item => item.Key == key);
        if (removed == 0)
        {
            return false;
        }
        if (kind == ScriptItemKind.Ticker)
        {
            _tickerDue.Remove(key);
        }
        ItemsChanged?.Invoke();
        return true;
    }

    /// <summary>Removes every script item, pending delay and recorded path.</summary>
    public void ClearAll()
    {
        foreach (List<ScriptItem> list in _items.Values)
        {
            list.Clear();
        }
        _tickerDue.Clear();
        _delays.Clear();
        _pendingActions.Clear();
        _path.Clear();
        _patternCache.Clear();
        _highlightCache.Clear();
        ItemsChanged?.Invoke();
    }

    public string? GetVariable(string name)
    {
        foreach (ScriptItem item in _items[ScriptItemKind.Variable])
        {
            if (item.Key == name)
            {
                return item.Value;
            }
        }
        return null;
    }

    public void SetVariable(string name, string value) =>
        SetItem(new ScriptItem(ScriptItemKind.Variable, name, value));

    /// <summary>Canonical form of a key gesture such as "ctrl + f1": upper case, modifiers first.</summary>
    public static string NormalizeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string[] parts = key.ToUpperInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }
        string[] modifierOrder = ["CTRL", "ALT", "SHIFT", "META"];
        var result = new List<string>();
        foreach (string modifier in modifierOrder)
        {
            if (parts.AsSpan(0, parts.Length - 1).Contains(modifier) || (modifier == "CTRL" && parts.AsSpan(0, parts.Length - 1).Contains("CONTROL")))
            {
                result.Add(modifier);
            }
        }
        result.Add(parts[^1]);
        return string.Join('+', result);
    }

    // ---- Execution -------------------------------------------------------------------

    /// <summary>Runs a line the user typed. Honors verbatim mode and the leading-backslash raw prefix.</summary>
    public void ExecuteUserInput(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (Verbatim && !IsCommand(line, "verbatim"))
        {
            _host.Send(line);
            return;
        }
        if (line.StartsWith('\\'))
        {
            _host.Send(line[1..]);
            return;
        }
        Execute(line);
    }

    /// <summary>Runs one or more separator-joined commands.</summary>
    public void Execute(string commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        Enter(() => Run(commands, 0));
    }

    /// <summary>Runs the macro bound to <paramref name="key"/>. Returns false when none is bound.</summary>
    public bool TryRunMacro(string key)
    {
        string normalized = NormalizeKey(key);
        foreach (ScriptItem macro in _items[ScriptItemKind.Macro])
        {
            if (NormalizeKey(macro.Key) == normalized)
            {
                Execute(macro.Value);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Applies actions, gags, substitutions and highlights to a line from the MUD. Returns the
    /// line to display, or null when it is gagged. Triggered commands are queued; call
    /// <see cref="RunPendingActions"/> after showing the line.
    /// </summary>
    public TextLine? ProcessLine(TextLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        QueueActions(line.Text);

        if (MatchesAny(ScriptItemKind.AntiSubstitute, line.Text))
        {
            return ApplyHighlights(line);
        }
        if (MatchesAny(ScriptItemKind.Gag, line.Text))
        {
            return null;
        }

        foreach (ScriptItem substitute in _items[ScriptItemKind.Substitute])
        {
            if (GetPattern(substitute.Key) is not { } pattern)
            {
                continue;
            }
            int position = 0;
            for (int guard = 0; guard < 100 && pattern.TryMatch(line.Text, position, out PatternMatch match); guard++)
            {
                string replacementText = ExpandAll(ScriptText.SubstituteArguments(substitute.Value, match.Captures));
                TextLine replacement = replacementText.Contains('\x1b', StringComparison.Ordinal)
                    ? AnsiParser.ParseLine(replacementText)
                    : TextLine.Styled(ScriptText.Unescape(replacementText), line.StyleAt(match.Index));
                line = line.Replace(match.Index, match.Length, replacement);
                // Resume after the replacement so it is never matched again. An empty match
                // must still advance, or the same position would match forever.
                position = match.Index + replacement.Length + (match.Length == 0 ? 1 : 0);
            }
        }
        return ApplyHighlights(line);
    }

    /// <summary>Runs commands queued by actions since the last call.</summary>
    public void RunPendingActions() => Enter(static () => { });

    /// <summary>Fires tickers, delays and the tick timer that have come due.</summary>
    public void Advance()
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (_tickEnabled)
        {
            if (!_tickWarned && _tickSize > 10 && now >= _nextTick - TimeSpan.FromSeconds(10))
            {
                _tickWarned = true;
                ShowLocal(TextLine.Styled("#10 SECONDS TO TICK!!!", InfoStyle));
            }
            if (now >= _nextTick)
            {
                ResetTick(now);
                ShowLocal(TextLine.Styled("#TICK!!!", InfoStyle));
            }
        }

        for (int i = _delays.Count - 1; i >= 0; i--)
        {
            if (now >= _delays[i].Due)
            {
                string commands = _delays[i].Commands;
                _delays.RemoveAt(i);
                Execute(commands);
            }
        }

        foreach (ScriptItem ticker in _items[ScriptItemKind.Ticker].ToArray())
        {
            if (_tickerDue.TryGetValue(ticker.Key, out DateTimeOffset due) && now >= due)
            {
                _tickerDue[ticker.Key] = now + TickerInterval(ticker);
                Execute(ticker.Value);
            }
        }
    }

    /// <summary>Seconds until the next tick, or null when the tick timer is off.</summary>
    public int? SecondsToTick =>
        _tickEnabled ? Math.Max(0, (int)Math.Ceiling((_nextTick - _time.GetUtcNow()).TotalSeconds)) : null;

    // ---- Persistence -----------------------------------------------------------------

    /// <summary>Runs the # commands in a script. Lines that are not commands are skipped, never sent.</summary>
    /// <returns>The number of commands run and of lines skipped.</returns>
    public (int Commands, int Skipped) Load(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        int commands = 0;
        int skipped = 0;
        var pending = new StringBuilder();
        _quiet++;
        try
        {
            foreach (string rawLine in script.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (pending.Length > 0)
                {
                    pending.Append('\n');
                }
                pending.Append(line);
                string candidate = pending.ToString();
                if (!ScriptText.BracesBalanced(candidate))
                {
                    continue;
                }
                pending.Clear();

                string trimmed = candidate.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                if (trimmed[0] == CommandChar)
                {
                    commands++;
                    Execute(trimmed);
                }
                else
                {
                    skipped++;
                }
            }
            if (pending.ToString().Trim().Length > 0)
            {
                skipped++;
            }
        }
        finally
        {
            _quiet--;
        }
        return (commands, skipped);
    }

    /// <summary>Serializes every script item as # commands that <see cref="Load"/> reads back.</summary>
    public string Save()
    {
        var output = new StringBuilder();
        char c = CommandChar;
        output.Append(c).Append("nop Comet MUD Client script\n");

        void Section(ScriptItemKind kind, Func<ScriptItem, string?> format)
        {
            bool first = true;
            foreach (ScriptItem item in _items[kind])
            {
                if (format(item) is not { } text)
                {
                    continue;
                }
                if (first)
                {
                    output.Append('\n');
                    first = false;
                }
                output.Append(c).Append(text).Append('\n');
            }
        }

        Section(ScriptItemKind.Variable, v => IsTransientVariable(v.Key) ? null : $"variable {{{v.Key}}} {{{v.Value}}}");
        Section(ScriptItemKind.Alias, a => $"alias {{{a.Key}}} {{{a.Value}}}");
        Section(ScriptItemKind.Action, a => $"action {{{a.Key}}} {{{a.Value}}} {{{PriorityOf(a).ToString(CultureInfo.InvariantCulture)}}}");
        Section(ScriptItemKind.AntiSubstitute, a => $"antisubstitute {{{a.Key}}}");
        Section(ScriptItemKind.Substitute, s => $"substitute {{{s.Key}}} {{{s.Value}}}");
        Section(ScriptItemKind.Gag, g => $"gag {{{g.Key}}}");
        Section(ScriptItemKind.Highlight, h => $"highlight {{{h.Value}}} {{{h.Key}}}");
        Section(ScriptItemKind.Ticker, t => $"ticker {{{t.Key}}} {{{t.Value}}} {{{t.Extra}}}");
        Section(ScriptItemKind.Macro, m => $"macro {{{m.Key}}} {{{m.Value}}}");
        Section(ScriptItemKind.TabWord, w => $"tabadd {{{w.Key}}}");
        return output.ToString();
    }

    // ---- Internals -------------------------------------------------------------------

    /// <summary>Variables set by the client itself (GMCP data) are not written to scripts.</summary>
    public static bool IsTransientVariable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith("gmcp.", StringComparison.Ordinal);
    }

    private static int PriorityOf(ScriptItem action) =>
        int.TryParse(action.Extra, NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority) ? priority : DefaultActionPriority;

    private static TimeSpan TickerInterval(ScriptItem ticker)
    {
        double seconds = double.TryParse(ticker.Extra, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 60;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.1, 86_400));
    }

    private void Enter(Action body)
    {
        bool outermost = _running == 0;
        if (outermost)
        {
            _operations = 0;
            _aborted = false;
        }
        _running++;
        try
        {
            body();
            if (outermost)
            {
                // Actions triggered by lines the commands printed run here, under the same budget.
                while (!_aborted && _pendingActions.TryDequeue(out string? commands))
                {
                    Run(commands, 0);
                }
            }
        }
        finally
        {
            _running--;
            if (outermost)
            {
                _pendingActions.Clear();
            }
        }
    }

    private void Abort(string message)
    {
        if (!_aborted)
        {
            _aborted = true;
            PrintError(message);
        }
    }

    private void Run(string text, int depth)
    {
        if (_aborted)
        {
            return;
        }
        if (depth > MaxDepth)
        {
            Abort("#Stopped: commands are nested too deeply. An alias or action may be calling itself.");
            return;
        }
        foreach (string command in ScriptText.SplitCommands(text, Separator))
        {
            if (_aborted)
            {
                return;
            }
            if (++_operations > MaxOperations)
            {
                Abort("#Stopped: too many commands from one input. A loop or trigger may be running away.");
                return;
            }
            RunOne(command, depth);
        }
    }

    private void RunOne(string rawCommand, int depth)
    {
        string command = ScriptText.SubstituteVariables(rawCommand, GetVariable, topLevelOnly: true).TrimStart();
        if (command.Length == 0)
        {
            SendToMud(string.Empty);
            return;
        }

        if (command[0] == CommandChar)
        {
            RunHashCommand(command, depth);
            return;
        }

        ReadOnlySpan<char> rest = command;
        string word = ScriptText.TakeArgument(ref rest, restOfLine: false);
        string arguments = rest.Trim().ToString();

        foreach (ScriptItem alias in _items[ScriptItemKind.Alias])
        {
            if (alias.Key == word)
            {
                var values = new List<string> { arguments };
                values.AddRange(ScriptText.SplitArguments(arguments));
                while (values.Count < 10)
                {
                    values.Add(string.Empty);
                }
                string body = ScriptText.HasArgumentReference(alias.Value)
                    ? ScriptText.SubstituteArguments(alias.Value, values)
                    : arguments.Length > 0 ? alias.Value + " " + arguments : alias.Value;
                Run(body, depth + 1);
                return;
            }
        }

        if (SpeedwalkEnabled && arguments.Length == 0 && ScriptText.TryExpandSpeedwalk(word, out List<string> steps))
        {
            foreach (string step in steps)
            {
                SendToMud(step);
            }
            return;
        }

        SendToMud(ScriptText.Unescape(command));
    }

    private void SendToMud(string text)
    {
        RecordPathStep(text);
        _host.Send(text);
    }

    private bool IsCommand(string line, string name)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != CommandChar)
        {
            return false;
        }
        int end = 1;
        while (end < trimmed.Length && char.IsAsciiLetter(trimmed[end]))
        {
            end++;
        }
        return end > 1 && name.StartsWith(trimmed[1..end], StringComparison.OrdinalIgnoreCase);
    }

    private string ExpandAll(string text) => ScriptText.SubstituteVariables(text, GetVariable, topLevelOnly: false);

    private void PrintInfo(string message)
    {
        if (ShowMessages && _quiet == 0)
        {
            _host.Print(TextLine.Styled(message, InfoStyle));
        }
    }

    /// <summary>Output the user asked for directly (a listing, #help). Shown even when messages are off.</summary>
    private void PrintAlways(string message) => _host.Print(TextLine.Styled(message, InfoStyle));

    private void PrintError(string message) => _host.Print(TextLine.Styled(message, ErrorStyle));

    /// <summary>Prints a line locally and lets actions react to it, as they would to MUD text.</summary>
    private void ShowLocal(TextLine line)
    {
        Enter(() =>
        {
            _host.Print(ApplyHighlights(line));
            QueueActions(line.Text);
        });
    }

    private void QueueActions(string text)
    {
        if (IgnoreActions)
        {
            return;
        }
        foreach (ScriptItem action in _items[ScriptItemKind.Action])
        {
            if (GetPattern(action.Key) is { } pattern && pattern.TryMatch(text, 0, out PatternMatch match))
            {
                _pendingActions.Enqueue(ScriptText.SubstituteArguments(action.Value, match.Captures));
            }
        }
    }

    private bool MatchesAny(ScriptItemKind kind, string text)
    {
        foreach (ScriptItem item in _items[kind])
        {
            if (GetPattern(item.Key) is { } pattern && pattern.IsMatch(text))
            {
                return true;
            }
        }
        return false;
    }

    private TextLine ApplyHighlights(TextLine line)
    {
        foreach (ScriptItem highlight in _items[ScriptItemKind.Highlight])
        {
            if (GetPattern(highlight.Key) is not { } pattern || GetHighlight(highlight.Value) is not { } style)
            {
                continue;
            }
            int position = 0;
            for (int guard = 0; guard < 200 && pattern.TryMatch(line.Text, position, out PatternMatch match); guard++)
            {
                if (match.Length == 0)
                {
                    position = match.Index + 1;
                    continue;
                }
                line = line.WithStyle(match.Index, match.Length, style.Apply);
                position = match.Index + match.Length;
            }
        }
        return line;
    }

    private HighlightStyle? GetHighlight(string spec)
    {
        if (!_highlightCache.TryGetValue(spec, out HighlightStyle? style))
        {
            style = HighlightStyle.TryParse(spec, out HighlightStyle? parsed) ? parsed : null;
            _highlightCache[spec] = style;
        }
        return style;
    }

    /// <summary>Compiles a stored pattern, expanding any variables it names at match time.</summary>
    private TriggerPattern? GetPattern(string source)
    {
        string expanded = source.Contains('$', StringComparison.Ordinal) ? ExpandPatternVariables(source) : source;
        if (_patternCache.TryGetValue(expanded, out TriggerPattern? cached))
        {
            return cached;
        }
        if (_patternCache.Count > 4096)
        {
            _patternCache.Clear();
        }
        TriggerPattern.TryCompile(expanded, out TriggerPattern? compiled, out _);
        _patternCache[expanded] = compiled;
        return compiled;
    }

    private string ExpandPatternVariables(string source)
    {
        // A trailing $ is the end-of-line anchor, not a variable.
        return ScriptText.SubstituteVariables(source, GetVariable, topLevelOnly: true);
    }

    private static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));

    private void ResetTick(DateTimeOffset now)
    {
        _nextTick = now + TimeSpan.FromSeconds(_tickSize);
        _tickWarned = false;
    }
}
