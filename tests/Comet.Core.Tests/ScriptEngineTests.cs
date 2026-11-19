using Comet.Core.Scripting;
using Comet.Core.Text;

namespace Comet.Core.Tests;

internal sealed class FakeHost : IScriptHost
{
    public List<string> Sent { get; } = [];
    public List<string> Printed { get; } = [];
    public List<TextLine> PrintedLines { get; } = [];
    public int Bells { get; private set; }
    public string[] PanelText { get; } = new string[3];
    public string Root { get; init; } = Path.GetTempPath();

    public bool IsLogging => false;
    public void Send(string text) => Sent.Add(text);
    public void Print(TextLine line) { Printed.Add(line.Text); PrintedLines.Add(line); }
    public void Bell() => Bells++;
    public void ClearOutput() => Printed.Clear();
    public void Connect(string name, string host, int port, bool useTls) => Sent.Add($"<connect {name} {host} {port} {useTls}>");
    public void Disconnect() => Sent.Add("<disconnect>");
    public string? StartLog(string path, out string fullPath) { fullPath = path; return null; }
    public void StopLog() { }
    public bool SendGmcp(string package, string json) { Sent.Add($"<gmcp {package} {json}>"); return true; }
    public void SetPanel(int index, string text) => PanelText[index - 1] = text;
    public string ResolvePath(string path) => Path.GetFullPath(path, Root);
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(double seconds) => _now += TimeSpan.FromSeconds(seconds);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class ScriptEngineTests
{
    private readonly FakeHost _host = new();
    private readonly ManualTime _time = new();
    private readonly ScriptEngine _engine;

    public ScriptEngineTests()
    {
        _engine = new ScriptEngine(_host, _time) { ShowMessages = false };
    }

    [Fact]
    public void PlainTextIsSent()
    {
        _engine.Execute("look");
        Assert.Equal(["look"], _host.Sent);
    }

    [Fact]
    public void SeparatorSplitsCommandsButNotInsideBracesOrWhenEscaped()
    {
        _engine.Execute("north;say a\\;b;#3 {x;y}");
        Assert.Equal(["north", "say a;b", "x", "y", "x", "y", "x", "y"], _host.Sent);
    }

    [Fact]
    public void AliasSubstitutesArguments()
    {
        _engine.Execute("#alias {k} {kill %1;loot %2 from %0}");
        _engine.Execute("k orc corpse");
        Assert.Equal(["kill orc", "loot corpse from orc corpse"], _host.Sent);
    }

    [Fact]
    public void AliasWithoutPlaceholdersAppendsArguments()
    {
        _engine.Execute("#alias gt gtell");
        _engine.Execute("gt hello there");
        Assert.Equal(["gtell hello there"], _host.Sent);
    }

    [Fact]
    public void CommandNamesCanBeAbbreviated()
    {
        _engine.Execute("#al {x} {y};#var hp 5");
        Assert.Single(_engine.GetItems(ScriptItemKind.Alias));
        Assert.Equal("5", _engine.GetVariable("hp"));
    }

    [Fact]
    public void VariablesExpandWhenTheCommandRuns()
    {
        _engine.Execute("#alias {t} {kill $target}");
        _engine.Execute("#var target orc;t");
        _engine.Execute("#var target elf;t");
        Assert.Equal(["kill orc", "kill elf"], _host.Sent);
    }

    [Fact]
    public void UnknownVariableIsLeftAlone()
    {
        _engine.Execute("say it costs $5 or $nothing");
        Assert.Equal(["say it costs $5 or $nothing"], _host.Sent);
    }

    [Fact]
    public void SpeedwalkNeedsACount()
    {
        _engine.Execute("3n2e;news;sw");
        Assert.Equal(["n", "n", "n", "e", "e", "news", "sw"], _host.Sent);
    }

    [Fact]
    public void VerbatimSendsUntouchedUntilTurnedOff()
    {
        _engine.ExecuteUserInput("#verbatim");
        _engine.ExecuteUserInput("a;b $x #alias");
        _engine.ExecuteUserInput("#verb");
        _engine.ExecuteUserInput("c;d");
        Assert.Equal(["a;b $x #alias", "c", "d"], _host.Sent);
    }

    [Fact]
    public void LeadingBackslashSendsRaw()
    {
        _engine.ExecuteUserInput("\\#not a command;x");
        Assert.Equal(["#not a command;x"], _host.Sent);
    }

    [Fact]
    public void ActionFiresWithCaptures()
    {
        _engine.Execute("#action {^%1 tells you '%2'} {reply %1 got %2 (%0)}");
        TextLine? shown = _engine.ProcessLine(TextLine.Plain("Bob tells you 'hi'"));
        Assert.Empty(_host.Sent);
        _engine.RunPendingActions();

        Assert.NotNull(shown);
        Assert.Equal(["reply Bob got hi (Bob tells you 'hi')"], _host.Sent);
    }

    [Fact]
    public void ActionsRunInPriorityOrderAndAllFire()
    {
        _engine.Execute("#action {gold} {second} {5};#action {gold} {zero} {5};#action {You see %1} {first} {1}");
        _engine.ProcessLine(TextLine.Plain("You see gold"));
        _engine.RunPendingActions();
        Assert.Equal(["first", "zero"], _host.Sent);
    }

    [Fact]
    public void ActionPatternExpandsVariablesAtMatchTime()
    {
        _engine.Execute("#action {^$leader says go} {follow};#var leader Ann");
        _engine.ProcessLine(TextLine.Plain("Bob says go"));
        _engine.ProcessLine(TextLine.Plain("Ann says go"));
        _engine.RunPendingActions();
        Assert.Equal(["follow"], _host.Sent);
    }

    [Fact]
    public void IgnoreStopsActions()
    {
        _engine.Execute("#action {x} {y};#ignore on");
        _engine.ProcessLine(TextLine.Plain("x"));
        _engine.RunPendingActions();
        Assert.Empty(_host.Sent);
    }

    [Fact]
    public void GagHidesLineButAntisubstituteProtectsIt()
    {
        _engine.Execute("#gag {spam};#antisub {important}");
        Assert.Null(_engine.ProcessLine(TextLine.Plain("some spam here")));
        Assert.NotNull(_engine.ProcessLine(TextLine.Plain("important spam")));
    }

    [Fact]
    public void SubstituteReplacesMatchAndKeepsStyle()
    {
        _engine.Execute("#sub {%1 hits you} {HIT by %1}");
        TextLine line = AnsiParser.ParseLine("\x1b[31mAn orc hits you\x1b[0m hard");
        TextLine? shown = _engine.ProcessLine(line);

        Assert.Equal("HIT by An orc hard", shown!.Text);
        Assert.Equal(TextColor.Palette(1), shown.StyleAt(0).Foreground);
    }

    [Fact]
    public void HighlightAcceptsEitherArgumentOrder()
    {
        _engine.Execute("#highlight {light red, underline} {orc};#highlight {elf} {b blue}");
        TextLine shown = _engine.ProcessLine(TextLine.Plain("an orc and an elf"))!;

        TextStyle orc = shown.StyleAt(3);
        Assert.Equal(TextColor.Palette(9), orc.Foreground);
        Assert.True(orc.Has(TextAttributes.Underline));
        Assert.Equal(TextColor.Palette(4), shown.StyleAt(14).Background);
        Assert.Equal(TextStyle.Default, shown.StyleAt(0));
    }

    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("7 / 2", "3")]
    [InlineData("7.0 / 2", "3.5")]
    [InlineData("8 - 2 + 1", "7")]
    [InlineData("-3 + 5", "2")]
    [InlineData("10 % 4", "2")]
    [InlineData("3 > 2 && 2 >= 2", "1")]
    [InlineData("!(1 == 1) || 0", "0")]
    [InlineData("\"abc\" == \"abc\"", "1")]
    [InlineData("[one two] != [one]", "1")]
    [InlineData("T && !F", "1")]
    public void MathEvaluates(string expression, string expected)
    {
        Assert.True(ExpressionEvaluator.TryEvaluate(expression, out string result, out string? error), error);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("(1 + 2")]
    [InlineData("1 +")]
    [InlineData("1 $ 2")]
    public void MathRejectsBadExpressions(string expression)
    {
        Assert.False(ExpressionEvaluator.TryEvaluate(expression, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void IfElseAndMath()
    {
        _engine.Execute("#var hp 20;#math {hp} {$hp * 2};#if {$hp > 30} {say high} else {say low};#if {$hp > 99} {say no} {say else}");
        Assert.Equal("40", _engine.GetVariable("hp"));
        Assert.Equal(["say high", "say else"], _host.Sent);
    }

    [Fact]
    public void LoopCountsBothWays()
    {
        _engine.Execute("#loop {1,3} {get %0};#loop {2,1} {put %0}");
        Assert.Equal(["get 1", "get 2", "get 3", "put 2", "put 1"], _host.Sent);
    }

    [Fact]
    public void RunawayAliasIsStopped()
    {
        _engine.Execute("#alias {a} {a}");
        _engine.Execute("a");
        Assert.Contains(_host.Printed, line => line.Contains("nested too deeply", StringComparison.Ordinal));
        Assert.Empty(_host.Sent);
    }

    [Fact]
    public void SelfTriggeringShowmeIsStopped()
    {
        _engine.Execute("#action {ping} {#showme ping}");
        _engine.Execute("#showme ping");
        Assert.Contains(_host.Printed, line => line.Contains("too many commands", StringComparison.Ordinal));
    }

    [Fact]
    public void TickerAndDelayFireOnTime()
    {
        _engine.Execute("#ticker {t} {tick} {10};#delay {5} {late}");
        _time.Advance(4);
        _engine.Advance();
        Assert.Empty(_host.Sent);
        _time.Advance(1);
        _engine.Advance();
        _time.Advance(5);
        _engine.Advance();
        _time.Advance(10);
        _engine.Advance();
        Assert.Equal(["late", "tick", "tick"], _host.Sent);
    }

    [Fact]
    public void TickTimerWarnsThenTicksAndActionsSeeIt()
    {
        _engine.Execute("#action {#TICK!!!} {sleep};#ticksize 30;#tickon");
        _time.Advance(21);
        _engine.Advance();
        Assert.Contains("#10 SECONDS TO TICK!!!", _host.Printed);
        _time.Advance(9);
        _engine.Advance();
        Assert.Contains("#TICK!!!", _host.Printed);
        Assert.Equal(["sleep"], _host.Sent);
        Assert.Equal(30, _engine.SecondsToTick);
    }

    [Fact]
    public void MacroKeysAreNormalized()
    {
        _engine.Execute("#macro {shift + ctrl+f5} {cast heal}");
        Assert.True(_engine.TryRunMacro("Ctrl+Shift+F5"));
        Assert.False(_engine.TryRunMacro("F5"));
        Assert.Equal(["cast heal"], _host.Sent);
    }

    [Fact]
    public void PathRecordsAndReturns()
    {
        _engine.Execute("#mark;north;e;look;#return;#savepath back");
        Assert.Equal(["north", "e", "look", "w"], _host.Sent);
        Assert.Equal("n", Assert.Single(_engine.GetItems(ScriptItemKind.Alias)).Value);
    }

    [Fact]
    public void UnCommandsUseGlobs()
    {
        _engine.Execute("#alias {aa} {1};#alias {ab} {2};#alias {b} {3};#unalias {a*}");
        Assert.Equal("b", Assert.Single(_engine.GetItems(ScriptItemKind.Alias)).Key);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        _engine.Execute("#alias {k} {kill %1;loot};#action {^%1 arrives} {wave %1} {3};#sub {a} {b};#gag {spam};#antisub {keep}");
        _engine.Execute("#highlight {red} {orc};#var name Bob;#ticker {t} {save} {60};#macro {F1} {look};#tabadd {Gandalf}");
        _engine.SetVariable("gmcp.Char.Vitals", "{}");
        string saved = _engine.Save();

        var copy = new ScriptEngine(new FakeHost(), _time);
        (int commands, int skipped) = copy.Load(saved);

        Assert.Equal(0, skipped);
        Assert.True(commands >= 10);
        Assert.Equal(saved, copy.Save());
        Assert.DoesNotContain("gmcp", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadJoinsMultiLineBlocksAndNeverSendsPlainLines()
    {
        var (commands, skipped) = _engine.Load("#alias {x} {\n  north;\n  south\n}\nkill everyone\n#var a 1\n");

        Assert.Equal(2, commands);
        Assert.Equal(1, skipped);
        Assert.Empty(_host.Sent);
        Assert.Contains("north", _engine.GetItems(ScriptItemKind.Alias)[0].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPatternIsReported()
    {
        _engine.Execute("#action {a{(}b} {x}");
        Assert.Empty(_engine.GetItems(ScriptItemKind.Action));
        Assert.Contains(_host.Printed, line => line.StartsWith("#Invalid action pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownCommandIsReported()
    {
        _engine.Execute("#frobnicate");
        Assert.Contains(_host.Printed, line => line.Contains("no such command", StringComparison.Ordinal));
    }
}

public class TriggerPatternTests
{
    private static IReadOnlyList<string>? Match(string pattern, string text)
    {
        Assert.True(TriggerPattern.TryCompile(pattern, out TriggerPattern? compiled, out string? error), error);
        return compiled!.TryMatch(text, 0, out PatternMatch match) ? match.Captures : null;
    }

    [Fact]
    public void LiteralTextIsEscaped()
    {
        Assert.NotNull(Match("[HP: 5] (ok)", "x [HP: 5] (ok) y"));
        Assert.Null(Match("a.c", "abc"));
    }

    [Fact]
    public void AnchorsBindStartAndEnd()
    {
        Assert.Null(Match("^orc", "an orc"));
        Assert.Null(Match("orc$", "orcs"));
        Assert.NotNull(Match("^orc$", "orc"));
    }

    [Fact]
    public void MiddleWildcardIsLazyAndLastIsGreedy()
    {
        IReadOnlyList<string> captures = Match("%1 says %2", "Ann says go says now")!;
        Assert.Equal("Ann", captures[1]);
        Assert.Equal("go says now", captures[2]);
    }

    [Fact]
    public void TypedWildcardsAndRanges()
    {
        IReadOnlyList<string> captures = Match("HP:%d/%d %w %+2..3d!", "HP:12/340 ok 999!")!;
        Assert.Equal(["12", "340", "ok", "999"], captures.Skip(1).Take(4));
        Assert.Null(Match("^%+2d$", "123"));
    }

    [Fact]
    public void EmbeddedRegexCapturesAndInnerGroupsDoNotShiftSlots()
    {
        IReadOnlyList<string> captures = Match("{(big|small) (orc|elf)} hits %2", "big elf hits you")!;
        Assert.Equal("big elf", captures[1]);
        Assert.Equal("you", captures[2]);
    }

    [Fact]
    public void CaseInsensitiveSwitchAndNonCapturing()
    {
        IReadOnlyList<string> captures = Match("%iHELLO %!* world %1", "hello big world now")!;
        Assert.Equal("now", captures[1]);
    }

    [Fact]
    public void PercentZeroIsWholeMatchUnlessUsedAsWildcard()
    {
        Assert.Equal("a b", Match("a %1", "x a b")![0]);
        Assert.Equal("b", Match("a %0", "x a b")![0]);
    }

    [Fact]
    public void BadEmbeddedRegexFailsToCompile()
    {
        Assert.False(TriggerPattern.TryCompile("{(}", out _, out string? error));
        Assert.NotNull(error);
    }
}
