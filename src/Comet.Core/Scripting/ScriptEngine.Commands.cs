using System.Globalization;
using System.Text.RegularExpressions;
using Comet.Core.Text;

namespace Comet.Core.Scripting;

public sealed partial class ScriptEngine
{
    private const int MaxRepeat = 1000;
    private const int MaxDelays = 500;
    private const int MaxPathLength = 500;
    private const long MaxScriptFileBytes = 8 * 1024 * 1024;

    private static readonly Dictionary<string, string> PathReverse = new(StringComparer.Ordinal)
    {
        ["n"] = "s", ["s"] = "n", ["e"] = "w", ["w"] = "e", ["u"] = "d", ["d"] = "u",
        ["ne"] = "sw", ["sw"] = "ne", ["nw"] = "se", ["se"] = "nw",
    };

    private static readonly Dictionary<string, string> PathShortForm = new(StringComparer.OrdinalIgnoreCase)
    {
        ["north"] = "n", ["south"] = "s", ["east"] = "e", ["west"] = "w", ["up"] = "u", ["down"] = "d",
        ["northeast"] = "ne", ["northwest"] = "nw", ["southeast"] = "se", ["southwest"] = "sw",
    };

    private sealed record CommandInfo(string Name, string Usage, string Summary, Action<ScriptEngine, ArgumentReader, int> Run);

    /// <summary>
    /// Commands in lookup order. A typed name may be shortened to any prefix; the first
    /// command in this list that starts with it wins, so order decides what "#a" means.
    /// </summary>
    private static readonly CommandInfo[] Commands =
    [
        new("action", "{pattern} {commands} [{priority}]", "Run commands when a line from the MUD matches.", (e, a, _) => e.DefinePatternItem(ScriptItemKind.Action, a, "action")),
        new("alias", "{name} {commands}", "Define a shortcut for one or more commands.", (e, a, _) => e.DefineAlias(a)),
        new("antisubstitute", "{pattern}", "Exempt matching lines from substitutions and gags.", (e, a, _) => e.DefinePatternItem(ScriptItemKind.AntiSubstitute, a, "antisubstitute")),
        new("bell", "", "Ring the bell.", (e, _, _) => e._host.Bell()),
        new("char", "{character}", "Change the command character.", (e, a, _) => e.SetCommandChar(a)),
        new("cls", "", "Clear the output window.", (e, _, _) => e._host.ClearOutput()),
        new("cr", "", "Send an empty line.", (e, _, _) => e.SendToMud(string.Empty)),
        new("delay", "{seconds} {commands}", "Run commands once after a delay.", (e, a, _) => e.AddDelay(a)),
        new("echo", "{text}", "Same as #showme.", (e, a, _) => e.ShowMe(a)),
        new("gag", "{pattern}", "Hide lines that match.", (e, a, _) => e.DefinePatternItem(ScriptItemKind.Gag, a, "gag")),
        new("gmcp", "{package} [{json}]", "Send a GMCP message to the MUD.", (e, a, _) => e.SendGmcp(a)),
        new("help", "[command]", "List commands, or describe one.", (e, a, _) => e.Help(a)),
        new("highlight", "{colors} {pattern}", "Color text that matches.", (e, a, _) => e.DefineHighlight(a)),
        new("if", "{expression} {commands} [else {commands}]", "Run commands when an expression is true.", (e, a, d) => e.If(a, d)),
        new("ignore", "[on|off]", "Stop or resume actions.", (e, a, _) => e.IgnoreActions = e.Toggle(a, e.IgnoreActions, "#Actions are now ignored.", "#Actions are active again.")),
        new("killall", "", "Remove every alias, action and other script item.", (e, _, _) => { e.ClearAll(); e.PrintInfo("#Ok. All script items removed."); }),
        new("log", "[{file}|off]", "Start or stop logging the session to a file.", (e, a, _) => e.Log(a)),
        new("loop", "{from,to} {commands}", "Repeat commands with %0 counting from one number to another.", (e, a, d) => e.Loop(a, d)),
        new("macro", "{key} {commands}", "Bind commands to a key such as F5 or Ctrl+NumPad8.", (e, a, _) => e.DefineMacro(a)),
        new("map", "{direction}", "Add a direction to the recorded path.", (e, a, _) => e.MapStep(a)),
        new("mark", "", "Start recording the path from here.", (e, _, _) => { e._path.Clear(); e.PrintInfo("#Ok. Path cleared."); }),
        new("math", "{variable} {expression}", "Set a variable to the result of an expression.", (e, a, _) => e.MathCommand(a)),
        new("message", "[on|off]", "Show or hide confirmation messages.", (e, a, _) => e.ShowMessages = e.Toggle(a, e.ShowMessages, "#Messages are on.", null)),
        new("nop", "[text]", "A comment. Does nothing.", static (_, _, _) => { }),
        new("panel", "{1-3} {text}", "Set the text of a status bar panel.", (e, a, _) => e.Panel(a)),
        new("path", "", "Show the recorded path.", (e, _, _) => e.PrintAlways(e._path.Count == 0 ? "#Path is empty." : "#Path: " + string.Join(' ', e._path))),
        new("read", "{file}", "Load a script file.", (e, a, _) => e.ReadFile(a)),
        new("return", "", "Walk back one step along the recorded path.", (e, _, _) => e.ReturnStep()),
        new("rnd", "{variable} {max}", "Set a variable to a random number from 1 to max.", (e, a, _) => e.RandomCommand(a)),
        new("savepath", "{alias}", "Save the recorded path as an alias.", (e, a, _) => e.SavePath(a)),
        new("send", "{text}", "Send text to the MUD without alias or speedwalk expansion.", (e, a, _) => e._host.Send(ScriptText.Unescape(e.ExpandAll(a.Rest())))),
        new("session", "{name} {host port}", "Connect this tab to a MUD.", (e, a, _) => e.Session(a)),
        new("showme", "{text}", "Show text in the output as if the MUD had sent it.", (e, a, _) => e.ShowMe(a)),
        new("speedwalk", "[on|off]", "Turn speedwalk (3n2e) on or off.", (e, a, _) => e.SpeedwalkEnabled = e.Toggle(a, e.SpeedwalkEnabled, "#Speedwalk is on.", "#Speedwalk is off.")),
        new("substitute", "{pattern} {replacement}", "Replace matching text.", (e, a, _) => e.DefinePatternItem(ScriptItemKind.Substitute, a, "substitute")),
        new("tabadd", "{word}", "Add a word to tab completion.", (e, a, _) => e.TabAdd(a)),
        new("tabdelete", "{word}", "Remove a word from tab completion.", (e, a, _) => e.Remove(ScriptItemKind.TabWord, a, "tab word")),
        new("tablist", "", "List tab completion words.", (e, _, _) => e.List(ScriptItemKind.TabWord, null)),
        new("tick", "", "Show the seconds left until the next tick.", (e, _, _) => e.PrintAlways(e.SecondsToTick is { } s ? $"#THERE'S NOW {s.ToString(CultureInfo.InvariantCulture)} SECONDS TO NEXT TICK." : "#The tick timer is off. Use #tickon.")),
        new("tickoff", "", "Turn the tick timer off.", (e, _, _) => { e._tickEnabled = false; e.PrintInfo("#Ok. Tick timer is off."); }),
        new("tickon", "", "Turn the tick timer on.", (e, _, _) => { e._tickEnabled = true; e.ResetTick(e._time.GetUtcNow()); e.PrintInfo("#Ok. Tick timer is on."); }),
        new("tickset", "", "Restart the tick countdown now.", (e, _, _) => { e.ResetTick(e._time.GetUtcNow()); e.PrintInfo("#Ok. Tick countdown restarted."); }),
        new("ticksize", "{seconds}", "Set the seconds between ticks.", (e, a, _) => e.TickSize(a)),
        new("ticker", "{name} {commands} {seconds}", "Run commands repeatedly at an interval.", (e, a, _) => e.DefineTicker(a)),
        new("tolower", "{variable} {text}", "Set a variable to text in lower case.", (e, a, _) => e.SetCase(a, upper: false)),
        new("toupper", "{variable} {text}", "Set a variable to text in upper case.", (e, a, _) => e.SetCase(a, upper: true)),
        new("unaction", "{pattern}", "Remove actions. * matches any text.", (e, a, _) => e.Remove(ScriptItemKind.Action, a, "action")),
        new("unalias", "{name}", "Remove aliases.", (e, a, _) => e.Remove(ScriptItemKind.Alias, a, "alias")),
        new("unantisubstitute", "{pattern}", "Remove antisubstitutes.", (e, a, _) => e.Remove(ScriptItemKind.AntiSubstitute, a, "antisubstitute")),
        new("ungag", "{pattern}", "Remove gags.", (e, a, _) => e.Remove(ScriptItemKind.Gag, a, "gag")),
        new("unhighlight", "{pattern}", "Remove highlights.", (e, a, _) => e.Remove(ScriptItemKind.Highlight, a, "highlight")),
        new("unmacro", "{key}", "Remove key macros.", (e, a, _) => e.Remove(ScriptItemKind.Macro, a, "macro")),
        new("unpath", "", "Drop the last step of the recorded path.", (e, _, _) => { if (e._path.Count > 0) { e._path.RemoveAt(e._path.Count - 1); } }),
        new("unsubstitute", "{pattern}", "Remove substitutions.", (e, a, _) => e.Remove(ScriptItemKind.Substitute, a, "substitute")),
        new("unticker", "{name}", "Remove tickers.", (e, a, _) => e.Remove(ScriptItemKind.Ticker, a, "ticker")),
        new("unvariable", "{name}", "Remove variables.", (e, a, _) => e.Remove(ScriptItemKind.Variable, a, "variable")),
        new("variable", "{name} {value}", "Set a variable. Use it as $name.", (e, a, _) => e.DefineVariable(a)),
        new("verbatim", "[on|off]", "Send typed text to the MUD untouched.", (e, a, _) => e.Verbatim = e.Toggle(a, e.Verbatim, "#Verbatim mode is on. Type #verbatim to leave it.", "#Verbatim mode is off.")),
        new("version", "", "Show the client version.", (e, _, _) => e.PrintAlways("#Comet MUD Client " + (typeof(ScriptEngine).Assembly.GetName().Version?.ToString(3) ?? "dev"))),
        new("write", "{file}", "Save every script item to a file.", (e, a, _) => e.WriteFile(a)),
        new("zap", "", "Disconnect from the MUD.", (e, _, _) => e._host.Disconnect()),
    ];

    /// <summary>Name, usage and one-line description of every # command, for help screens.</summary>
    public static IEnumerable<(string Name, string Usage, string Summary)> CommandReference =>
        Commands.OrderBy(static c => c.Name, StringComparer.Ordinal).Select(static c => (c.Name, c.Usage, c.Summary));

    /// <summary>Reads arguments from the text after a command name.</summary>
    private sealed class ArgumentReader(string text)
    {
        private string _rest = text;

        public bool IsEmpty => string.IsNullOrWhiteSpace(_rest);

        /// <summary>A braced group, or one word.</summary>
        public string Next() => Take(restOfLine: false);

        /// <summary>A braced group, or everything left on the line.</summary>
        public string Rest() => Take(restOfLine: true);

        public string Remaining => _rest.Trim();

        public void Skip(int characters) => _rest = _rest.TrimStart()[characters..];

        private string Take(bool restOfLine)
        {
            ReadOnlySpan<char> span = _rest;
            string argument = ScriptText.TakeArgument(ref span, restOfLine);
            _rest = span.ToString();
            return argument;
        }
    }

    private void RunHashCommand(string command, int depth)
    {
        int end = 1;
        while (end < command.Length && !char.IsWhiteSpace(command[end]) && command[end] != '{')
        {
            end++;
        }
        string name = command[1..end];
        var arguments = new ArgumentReader(command[end..]);

        if (name.Length == 0)
        {
            PrintError($"#Type {CommandChar}help for a list of commands.");
            return;
        }

        if (name.All(char.IsAsciiDigit))
        {
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1 || count > MaxRepeat)
            {
                PrintError($"#Repeat count must be from 1 to {MaxRepeat.ToString(CultureInfo.InvariantCulture)}.");
                return;
            }
            string body = arguments.Rest();
            for (int i = 0; i < count && !_aborted; i++)
            {
                Run(body, depth + 1);
            }
            return;
        }

        foreach (CommandInfo info in Commands)
        {
            if (info.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                info.Run(this, arguments, depth);
                return;
            }
        }
        PrintError($"#There's no such command as '{name}'. Type {CommandChar}help for a list.");
    }

    // ---- Definitions -----------------------------------------------------------------

    private void DefineAlias(ArgumentReader args)
    {
        if (args.IsEmpty)
        {
            List(ScriptItemKind.Alias, null);
            return;
        }
        string name = args.Next();
        if (args.IsEmpty)
        {
            List(ScriptItemKind.Alias, name);
            return;
        }
        string body = args.Rest();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
        {
            PrintError("#An alias name must be one word.");
            return;
        }
        SetItem(new ScriptItem(ScriptItemKind.Alias, name, body));
        PrintInfo($"#Ok. {{{name}}} aliases {{{body}}}.");
    }

    private void DefinePatternItem(ScriptItemKind kind, ArgumentReader args, string label)
    {
        if (args.IsEmpty)
        {
            List(kind, null);
            return;
        }
        string pattern = args.Next();
        bool needsBody = kind is ScriptItemKind.Action or ScriptItemKind.Substitute;
        if (needsBody && args.IsEmpty)
        {
            List(kind, pattern);
            return;
        }
        string body = needsBody ? args.Next() : string.Empty;
        string extra = string.Empty;
        if (kind == ScriptItemKind.Action)
        {
            string priority = ExpandAll(args.Next());
            extra = int.TryParse(priority, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed.ToString(CultureInfo.InvariantCulture)
                : DefaultActionPriority.ToString(CultureInfo.InvariantCulture);
        }

        if (pattern.Length == 0)
        {
            PrintError($"#The {label} pattern is empty.");
            return;
        }
        if (!TriggerPattern.TryCompile(pattern, out _, out string? error))
        {
            PrintError($"#Invalid {label} pattern: {error}");
            return;
        }
        SetItem(new ScriptItem(kind, pattern, body, extra));
        PrintInfo(needsBody ? $"#Ok. {{{pattern}}} now triggers {{{body}}}." : $"#Ok. {{{pattern}}} added as {label}.");
    }

    private void DefineHighlight(ArgumentReader args)
    {
        if (args.IsEmpty)
        {
            List(ScriptItemKind.Highlight, null);
            return;
        }
        string first = args.Next();
        string second = args.Next();
        if (second.Length == 0)
        {
            PrintError($"#Usage: {CommandChar}highlight {{colors}} {{pattern}}. Colors: {string.Join(", ", HighlightStyle.ColorNames)}; add 'b ' for background; {string.Join(", ", HighlightStyle.AttributeNames)}.");
            return;
        }

        // Classic TinTin puts the colors first, TinTin++ the pattern first. Accept both.
        string colors;
        string pattern;
        if (HighlightStyle.TryParse(first, out _))
        {
            (colors, pattern) = (first, second);
        }
        else if (HighlightStyle.TryParse(second, out _))
        {
            (colors, pattern) = (second, first);
        }
        else
        {
            PrintError($"#Unknown color in '{first}'. Colors: {string.Join(", ", HighlightStyle.ColorNames)}, 0-255 or #rrggbb.");
            return;
        }
        if (!TriggerPattern.TryCompile(pattern, out _, out string? error))
        {
            PrintError($"#Invalid highlight pattern: {error}");
            return;
        }
        SetItem(new ScriptItem(ScriptItemKind.Highlight, pattern, colors));
        PrintInfo($"#Ok. {{{pattern}}} is now highlighted {{{colors}}}.");
    }

    private void DefineVariable(ArgumentReader args)
    {
        if (args.IsEmpty)
        {
            List(ScriptItemKind.Variable, null);
            return;
        }
        string name = ExpandAll(args.Next());
        if (args.IsEmpty)
        {
            List(ScriptItemKind.Variable, name);
            return;
        }
        string value = ExpandAll(args.Rest());
        if (!IsValidVariableName(name))
        {
            PrintError("#A variable name may hold letters, digits and underscores, and must not start with a digit.");
            return;
        }
        SetVariable(name, value);
        PrintInfo($"#Ok. ${name} is now {{{value}}}.");
    }

    private static bool IsValidVariableName(string name) =>
        name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private void DefineTicker(ArgumentReader args)
    {
        if (args.IsEmpty)
        {
            List(ScriptItemKind.Ticker, null);
            return;
        }
        string name = args.Next();
        string body = args.Next();
        string seconds = ExpandAll(args.Next());
        if (name.Length == 0 || body.Length == 0
            || !double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double interval)
            || interval < 0.1 || interval > 86_400)
        {
            PrintError($"#Usage: {CommandChar}ticker {{name}} {{commands}} {{seconds}}. Seconds must be from 0.1 to 86400.");
            return;
        }
        SetItem(new ScriptItem(ScriptItemKind.Ticker, name, body, interval.ToString(CultureInfo.InvariantCulture)));
        PrintInfo($"#Ok. Ticker {{{name}}} runs {{{body}}} every {interval.ToString(CultureInfo.InvariantCulture)} seconds.");
    }

    private void DefineMacro(ArgumentReader args)
    {
        if (args.IsEmpty)
        {
            List(ScriptItemKind.Macro, null);
            return;
        }
        string key = NormalizeKey(args.Next());
        string body = args.Rest();
        if (key.Length == 0 || body.Length == 0)
        {
            PrintError($"#Usage: {CommandChar}macro {{key}} {{commands}}. Example: {CommandChar}macro {{Ctrl+F5}} {{cast heal}}");
            return;
        }
        SetItem(new ScriptItem(ScriptItemKind.Macro, key, body));
        PrintInfo($"#Ok. {key} now runs {{{body}}}.");
    }

    private void TabAdd(ArgumentReader args)
    {
        string word = args.Rest().Trim();
        if (word.Length == 0 || word.Any(char.IsWhiteSpace))
        {
            PrintError("#A tab completion entry must be one word.");
            return;
        }
        SetItem(new ScriptItem(ScriptItemKind.TabWord, word));
        PrintInfo($"#Ok. {{{word}}} added to tab completion.");
    }

    private void Remove(ScriptItemKind kind, ArgumentReader args, string label)
    {
        string glob = args.Rest();
        if (glob.Length == 0)
        {
            PrintError($"#Say which {label} to remove. * matches any text.");
            return;
        }
        if (kind == ScriptItemKind.Macro)
        {
            glob = NormalizeKey(glob);
        }
        Regex matcher = GlobToRegex(glob);
        string[] keys = [.. _items[kind].Where(item => matcher.IsMatch(item.Key)).Select(static item => item.Key)];
        foreach (string key in keys)
        {
            RemoveItem(kind, key);
        }
        PrintInfo(keys.Length == 0
            ? $"#No {label} matches {{{glob}}}."
            : $"#Ok. Removed {keys.Length.ToString(CultureInfo.InvariantCulture)} {label}{(keys.Length == 1 ? string.Empty : "s")}.");
    }

    private void List(ScriptItemKind kind, string? glob)
    {
        Regex? matcher = glob is null ? null : GlobToRegex(glob);
        string heading = kind switch
        {
            ScriptItemKind.Alias => "aliases",
            ScriptItemKind.Action => "actions",
            ScriptItemKind.Substitute => "substitutions",
            ScriptItemKind.Gag => "gags",
            ScriptItemKind.AntiSubstitute => "antisubstitutes",
            ScriptItemKind.Highlight => "highlights",
            ScriptItemKind.Variable => "variables",
            ScriptItemKind.Ticker => "tickers",
            ScriptItemKind.Macro => "macros",
            _ => "tab words",
        };

        int shown = 0;
        foreach (ScriptItem item in _items[kind])
        {
            if (matcher is not null && !matcher.IsMatch(item.Key))
            {
                continue;
            }
            if (shown++ == 0)
            {
                PrintAlways($"#Defined {heading}:");
            }
            string line = $"  {{{item.Key}}}";
            if (item.Value.Length > 0)
            {
                line += $" = {{{item.Value}}}";
            }
            if (item.Extra.Length > 0)
            {
                line += kind == ScriptItemKind.Ticker ? $" every {item.Extra}s" : $" @ {item.Extra}";
            }
            PrintAlways(line);
        }
        if (shown == 0)
        {
            PrintAlways(glob is null ? $"#No {heading} defined." : $"#No {heading} match {{{glob}}}.");
        }
    }

    // ---- Control flow ----------------------------------------------------------------

    private void If(ArgumentReader args, int depth)
    {
        string condition = ExpandAll(args.Next());
        string whenTrue = args.Next();
        if (condition.Length == 0 || whenTrue.Length == 0)
        {
            PrintError($"#Usage: {CommandChar}if {{expression}} {{commands}} [else {{commands}}]");
            return;
        }

        string remaining = args.Remaining;
        foreach (string keyword in (ReadOnlySpan<string>)[CommandChar + "else", "else"])
        {
            if (remaining.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                args.Skip(keyword.Length);
                break;
            }
        }
        string whenFalse = args.Rest();

        bool result = ExpressionEvaluator.IsTrue(condition, out string? error);
        if (error is not null)
        {
            PrintError($"#Invalid expression {{{condition}}}: {error}");
            return;
        }
        if (result)
        {
            Run(whenTrue, depth + 1);
        }
        else if (whenFalse.Length > 0)
        {
            Run(whenFalse, depth + 1);
        }
    }

    private void Loop(ArgumentReader args, int depth)
    {
        string range = ExpandAll(args.Next());
        string body = args.Rest();
        string[] bounds = range.Split(',', StringSplitOptions.TrimEntries);
        if (bounds.Length != 2
            || !int.TryParse(bounds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int from)
            || !int.TryParse(bounds[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int to)
            || body.Length == 0)
        {
            PrintError($"#Usage: {CommandChar}loop {{from,to}} {{commands}}. %0 holds the counter.");
            return;
        }

        int step = from <= to ? 1 : -1;
        for (long i = from; !_aborted && (step > 0 ? i <= to : i >= to); i += step)
        {
            Run(ScriptText.SubstituteArguments(body, [i.ToString(CultureInfo.InvariantCulture)]), depth + 1);
        }
    }

    private void MathCommand(ArgumentReader args)
    {
        string name = ExpandAll(args.Next());
        string expression = ExpandAll(args.Rest());
        if (!IsValidVariableName(name) || expression.Length == 0)
        {
            PrintError($"#Usage: {CommandChar}math {{variable}} {{expression}}");
            return;
        }
        if (!ExpressionEvaluator.TryEvaluate(expression, out string result, out string? error))
        {
            PrintError($"#Invalid expression {{{expression}}}: {error}");
            return;
        }
        SetVariable(name, result);
    }

    private void RandomCommand(ArgumentReader args)
    {
        string name = ExpandAll(args.Next());
        if (!IsValidVariableName(name)
            || !int.TryParse(ExpandAll(args.Rest()), NumberStyles.None, CultureInfo.InvariantCulture, out int max) || max < 1)
        {
            PrintError($"#Usage: {CommandChar}rnd {{variable}} {{max}}");
            return;
        }
#pragma warning disable CA5394 // Game dice, not a security decision.
        SetVariable(name, System.Random.Shared.Next(1, max == int.MaxValue ? max : max + 1).ToString(CultureInfo.InvariantCulture));
#pragma warning restore CA5394
    }

    private void SetCase(ArgumentReader args, bool upper)
    {
        string name = ExpandAll(args.Next());
        string text = ExpandAll(args.Rest());
        if (!IsValidVariableName(name))
        {
            PrintError($"#Usage: {CommandChar}{(upper ? "toupper" : "tolower")} {{variable}} {{text}}");
            return;
        }
        SetVariable(name, upper ? text.ToUpperInvariant() : text.ToLowerInvariant());
    }

    private void AddDelay(ArgumentReader args)
    {
        string seconds = ExpandAll(args.Next());
        string body = args.Rest();
        if (!double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double delay)
            || delay < 0 || delay > 86_400 || body.Length == 0)
        {
            PrintError($"#Usage: {CommandChar}delay {{seconds}} {{commands}}");
            return;
        }
        if (_delays.Count >= MaxDelays)
        {
            PrintError("#Too many delayed commands are waiting.");
            return;
        }
        _delays.Add((_time.GetUtcNow() + TimeSpan.FromSeconds(delay), body));
    }

    // ---- Settings and output ---------------------------------------------------------

    private bool Toggle(ArgumentReader args, bool current, string onMessage, string? offMessage)
    {
        string argument = args.Rest().Trim();
        bool value = argument.ToUpperInvariant() switch
        {
            "ON" => true,
            "OFF" => false,
            _ => !current,
        };
        if (value)
        {
            PrintInfo(onMessage);
        }
        else if (offMessage is not null)
        {
            PrintInfo(offMessage);
        }
        return value;
    }

    private void SetCommandChar(ArgumentReader args)
    {
        string value = args.Rest().Trim();
        if (value.Length != 1 || !char.IsPunctuation(value[0]) && !char.IsSymbol(value[0]) || value[0] == Separator || value[0] is '{' or '}' or '\\' or '$' or '%')
        {
            PrintError("#The command character must be one punctuation character.");
            return;
        }
        CommandChar = value[0];
        PrintInfo($"#Ok. Commands now start with {CommandChar}");
    }

    private void ShowMe(ArgumentReader args)
    {
        string text = ScriptText.Unescape(ExpandAll(args.Rest()));
        ShowLocal(AnsiParser.ParseLine(text));
    }

    private void Panel(ArgumentReader args)
    {
        if (!int.TryParse(args.Next(), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 1 || index > 3)
        {
            PrintError($"#Usage: {CommandChar}panel {{1-3}} {{text}}");
            return;
        }
        _host.SetPanel(index, ScriptText.Unescape(ExpandAll(args.Rest())));
    }

    private void TickSize(ArgumentReader args)
    {
        if (!int.TryParse(ExpandAll(args.Rest()), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds < 1 || seconds > 86_400)
        {
            PrintError($"#Usage: {CommandChar}ticksize {{seconds}}");
            return;
        }
        _tickSize = seconds;
        ResetTick(_time.GetUtcNow());
        PrintInfo($"#Ok. Tick size is {seconds.ToString(CultureInfo.InvariantCulture)} seconds.");
    }

    private void SendGmcp(ArgumentReader args)
    {
        string package = ExpandAll(args.Next());
        string json = ExpandAll(args.Rest());
        if (package.Length == 0)
        {
            PrintError($"#Usage: {CommandChar}gmcp {{package}} [{{json}}]");
        }
        else if (!_host.SendGmcp(package, json))
        {
            PrintError("#GMCP is not active on this connection.");
        }
    }

    private void Session(ArgumentReader args)
    {
        string name = ExpandAll(args.Next());
        string[] address = ExpandAll(args.Rest()).Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries);
        bool tls = address.Length == 3 && address[2].Equals("tls", StringComparison.OrdinalIgnoreCase);
        if (name.Length == 0 || address.Length < 2 || address.Length > 3 || (address.Length == 3 && !tls)
            || !int.TryParse(address[1], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65_535)
        {
            PrintError($"#Usage: {CommandChar}session {{name}} {{host port}}. Add 'tls' after the port for an encrypted connection.");
            return;
        }
        _host.Connect(name, address[0], port, tls);
    }

    private void Log(ArgumentReader args)
    {
        string path = ExpandAll(args.Rest()).Trim();
        if (path.Length == 0 || path.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (_host.IsLogging)
            {
                _host.StopLog();
                PrintInfo("#Ok. Logging stopped.");
            }
            else
            {
                PrintError($"#Not logging. Use {CommandChar}log {{file}} to start.");
            }
            return;
        }
        string? error = _host.StartLog(path, out string full);
        if (error is null)
        {
            PrintInfo($"#Ok. Logging to {full}");
        }
        else
        {
            PrintError($"#Could not start the log: {error}");
        }
    }

    private void Help(ArgumentReader args)
    {
        string topic = args.Rest().Trim().TrimStart(CommandChar);
        if (topic.Length > 0)
        {
            CommandInfo? info = Commands.FirstOrDefault(c => c.Name.StartsWith(topic, StringComparison.OrdinalIgnoreCase));
            if (info is null)
            {
                PrintError($"#There's no such command as '{topic}'.");
                return;
            }
            PrintAlways($"{CommandChar}{info.Name} {info.Usage}".TrimEnd());
            PrintAlways("  " + info.Summary);
            return;
        }

        PrintAlways($"#Commands. Type {CommandChar}help {{name}} for one. Names may be shortened.");
        foreach ((string name, _, string summary) in CommandReference)
        {
            PrintAlways($"  {CommandChar}{name,-17}{summary}");
        }
        PrintAlways($"#Also: {CommandChar}5 {{cmd}} repeats a command; 3n2e walks; a;b sends two commands; \\text sends text as typed.");
    }

    // ---- Files -----------------------------------------------------------------------

    private void ReadFile(ArgumentReader args)
    {
        string path = ExpandAll(args.Rest()).Trim();
        if (path.Length == 0)
        {
            PrintError($"#Usage: {CommandChar}read {{file}}");
            return;
        }
        string full = _host.ResolvePath(path);
        try
        {
            var file = new FileInfo(full);
            if (!file.Exists)
            {
                PrintError($"#File not found: {full}");
                return;
            }
            if (file.Length > MaxScriptFileBytes)
            {
                PrintError("#That file is too large to be a script.");
                return;
            }
            (int commands, int skipped) = Load(File.ReadAllText(full));
            PrintInfo($"#Ok. Read {commands.ToString(CultureInfo.InvariantCulture)} commands from {full}"
                + (skipped > 0 ? $" ({skipped.ToString(CultureInfo.InvariantCulture)} lines that are not commands were skipped)." : "."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            PrintError($"#Could not read {full}: {ex.Message}");
        }
    }

    private void WriteFile(ArgumentReader args)
    {
        string path = ExpandAll(args.Rest()).Trim();
        if (path.Length == 0)
        {
            PrintError($"#Usage: {CommandChar}write {{file}}");
            return;
        }
        string full = _host.ResolvePath(path);
        try
        {
            AtomicFile.WriteAllText(full, Save());
            PrintInfo($"#Ok. Script written to {full}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            PrintError($"#Could not write {full}: {ex.Message}");
        }
    }

    // ---- Path ------------------------------------------------------------------------

    private void RecordPathStep(string sent)
    {
        string step = sent.Trim();
        if (PathShortForm.TryGetValue(step, out string? shortened))
        {
            step = shortened;
        }
        if (PathReverse.ContainsKey(step) && _path.Count < MaxPathLength)
        {
            _path.Add(step);
        }
    }

    private void MapStep(ArgumentReader args)
    {
        string step = args.Rest().Trim().ToLowerInvariant();
        if (PathShortForm.TryGetValue(step, out string? shortened))
        {
            step = shortened;
        }
        if (!PathReverse.ContainsKey(step))
        {
            PrintError("#Directions are n, s, e, w, u, d, ne, nw, se and sw.");
            return;
        }
        if (_path.Count < MaxPathLength)
        {
            _path.Add(step);
        }
    }

    private void ReturnStep()
    {
        if (_path.Count == 0)
        {
            PrintError("#The path is empty.");
            return;
        }
        string last = _path[^1];
        _path.RemoveAt(_path.Count - 1);
        _host.Send(PathReverse[last]);
    }

    private void SavePath(ArgumentReader args)
    {
        string name = args.Rest().Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace) || _path.Count == 0)
        {
            PrintError(_path.Count == 0 ? "#The path is empty." : $"#Usage: {CommandChar}savepath {{alias}}");
            return;
        }
        string body = string.Join(Separator, _path);
        SetItem(new ScriptItem(ScriptItemKind.Alias, name, body));
        PrintInfo($"#Ok. {{{name}}} aliases {{{body}}}.");
    }
}
