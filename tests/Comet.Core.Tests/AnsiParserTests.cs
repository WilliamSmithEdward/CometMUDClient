using Comet.Core.Text;

namespace Comet.Core.Tests;

public class AnsiParserTests
{
    private static (List<TextLine> Lines, AnsiParser Parser) Parse(params string[] chunks)
    {
        var parser = new AnsiParser();
        var lines = new List<TextLine>();
        parser.LineCompleted += lines.Add;
        foreach (string chunk in chunks)
        {
            parser.Feed(chunk);
        }
        return (lines, parser);
    }

    [Fact]
    public void SplitsLinesOnCrLfAndLfCr()
    {
        var (lines, parser) = Parse("one\r\ntwo\n\rthree");

        Assert.Equal(["one", "two"], lines.Select(l => l.Text));
        Assert.Equal("three", parser.PartialLine.Text);
    }

    [Fact]
    public void BasicColorsProduceRuns()
    {
        var (lines, _) = Parse("a\x1b[31mb\x1b[0mc\n");

        TextLine line = Assert.Single(lines);
        Assert.Equal("abc", line.Text);
        Assert.Equal(3, line.Runs.Length);
        Assert.Equal(TextColor.Palette(1), line.Runs[1].Style.Foreground);
        Assert.Equal(TextStyle.Default, line.Runs[2].Style);
    }

    [Fact]
    public void StyleCarriesAcrossLinesAndChunks()
    {
        var (lines, _) = Parse("\x1b[1;3", "2mfoo\nbar\n");

        Assert.Equal(2, lines.Count);
        Assert.True(lines[1].Runs[0].Style.Has(TextAttributes.Bold));
        Assert.Equal(TextColor.Palette(2), lines[1].Runs[0].Style.Foreground);
    }

    [Theory]
    [InlineData("\x1b[38;5;196mx\n", TextColorKind.Palette, 196)]
    [InlineData("\x1b[38;2;1;2;3mx\n", TextColorKind.Rgb, 0x010203)]
    [InlineData("\x1b[38:2::1:2:3mx\n", TextColorKind.Rgb, 0x010203)]
    [InlineData("\x1b[38:2:1:2:3mx\n", TextColorKind.Rgb, 0x010203)]
    [InlineData("\x1b[91mx\n", TextColorKind.Palette, 9)]
    public void ExtendedForegroundColors(string input, TextColorKind kind, int value)
    {
        var (lines, _) = Parse(input);

        Assert.Equal(new TextColor(kind, value), lines[0].Runs[0].Style.Foreground);
    }

    [Fact]
    public void ExtendedColorDoesNotSwallowFollowingCodes()
    {
        var (lines, _) = Parse("\x1b[48;5;21;4mx\n");

        TextStyle style = lines[0].Runs[0].Style;
        Assert.Equal(TextColor.Palette(21), style.Background);
        Assert.True(style.Has(TextAttributes.Underline));
    }

    [Fact]
    public void NonSgrSequencesAreDropped()
    {
        var (lines, _) = Parse("a\x1b[2J\x1b[10;5Hb\x1b]0;title\x07" + "c\x1b(Bd\n");

        Assert.Equal("abcd", Assert.Single(lines).Text);
    }

    [Fact]
    public void BareCarriageReturnOverwritesLine()
    {
        var (lines, _) = Parse("loading\rdone\n");

        Assert.Equal("done", Assert.Single(lines).Text);
    }

    [Fact]
    public void TabsExpandToEightColumns()
    {
        var (lines, _) = Parse("ab\tc\n");

        Assert.Equal("ab      c", lines[0].Text);
    }

    [Fact]
    public void BellIsReportedAndRemoved()
    {
        var parser = new AnsiParser();
        int bells = 0;
        parser.Bell += () => bells++;
        parser.Feed("ding\a");

        Assert.Equal(1, bells);
        Assert.Equal("ding", parser.PartialLine.Text);
    }

    [Fact]
    public void OverlongLineIsBroken()
    {
        var (lines, _) = Parse(new string('x', AnsiParser.MaxLineLength + 5));

        Assert.Equal(AnsiParser.MaxLineLength, Assert.Single(lines).Length);
    }

    [Fact]
    public void WithStyleRestylesOnlyTheRange()
    {
        TextLine line = TextLine.Plain("hello world").WithStyle(6, 5, s => s.With(TextAttributes.Bold));

        Assert.Equal(2, line.Runs.Length);
        Assert.False(line.StyleAt(0).Has(TextAttributes.Bold));
        Assert.True(line.StyleAt(6).Has(TextAttributes.Bold));
    }

    [Fact]
    public void ReplaceKeepsSurroundingStyles()
    {
        TextLine line = AnsiParser.ParseLine("\x1b[31mred\x1b[0m mid \x1b[32mgreen");
        TextLine replaced = line.Replace(4, 3, TextLine.Plain("X"));

        Assert.Equal("red X green", replaced.Text);
        Assert.Equal(TextColor.Palette(1), replaced.StyleAt(0).Foreground);
        Assert.Equal(TextColor.Palette(2), replaced.StyleAt(7).Foreground);
    }

    [Fact]
    public void ScrollbackDropsOldestWhenFull()
    {
        var buffer = new ScrollbackBuffer(ScrollbackBuffer.MinCapacity);
        for (int i = 0; i < ScrollbackBuffer.MinCapacity + 3; i++)
        {
            buffer.Append(TextLine.Plain(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        Assert.Equal(ScrollbackBuffer.MinCapacity, buffer.Count);
        Assert.Equal(3, buffer.DroppedLines);
        Assert.Equal("3", buffer[0].Text);
        Assert.Equal("102", buffer[buffer.Count - 1].Text);
    }
}
