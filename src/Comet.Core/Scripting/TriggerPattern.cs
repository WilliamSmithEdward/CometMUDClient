using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Comet.Core.Scripting;

/// <summary>
/// A TinTin-style text pattern compiled to a regular expression.
/// </summary>
/// <remarks>
/// Literal text matches itself. A leading ^ anchors to the start of the line and a trailing $
/// to the end. %0-%99 capture any text into that slot. %* %+ %? %. %d %D %w %W %s %S capture
/// character classes into the next free slot, %!x matches without capturing, %+1..3d sets a
/// repeat range, %i and %I switch case sensitivity, and {regex} embeds a raw regular
/// expression. A wildcard at the end of the pattern takes as much as it can; elsewhere it
/// takes as little as it can.
/// </remarks>
public sealed class TriggerPattern
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex _regex;
    private readonly int[] _slotOfGroup;
    private readonly int _slotCount;
    private readonly bool _usesSlotZero;

    private TriggerPattern(string source, Regex regex, int[] slotOfGroup, int slotCount, bool usesSlotZero)
    {
        Source = source;
        _regex = regex;
        _slotOfGroup = slotOfGroup;
        _slotCount = slotCount;
        _usesSlotZero = usesSlotZero;
    }

    public string Source { get; }

    public static bool TryCompile(string pattern, out TriggerPattern? compiled, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        compiled = null;
        error = null;

        var regex = new StringBuilder();
        var slots = new List<int> { 0 };
        int lastSlot = 0;
        bool usesSlotZero = false;
        int i = 0;

        if (pattern.StartsWith('^'))
        {
            regex.Append('^');
            i = 1;
        }

        int end = pattern.Length;
        bool anchorEnd = end > i && pattern[end - 1] == '$' && (end - 2 < i || pattern[end - 2] != '\\');
        if (anchorEnd)
        {
            end--;
        }

        while (i < end)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < end)
            {
                regex.Append(Regex.Escape(pattern[i + 1].ToString()));
                i += 2;
            }
            else if (c == '{')
            {
                int close = FindClosingBrace(pattern, i, end);
                if (close < 0)
                {
                    error = "Unmatched { in pattern.";
                    return false;
                }
                slots.Add(++lastSlot);
                AppendGroupStart(regex, slots.Count - 1);
                regex.Append(pattern, i + 1, close - i - 1).Append(')');
                i = close + 1;
            }
            else if (c == '%' && i + 1 < end)
            {
                i = AppendWildcard(pattern, i + 1, end, regex, slots, ref lastSlot, ref usesSlotZero);
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }

        if (anchorEnd)
        {
            regex.Append('$');
        }

        try
        {
            var built = new Regex(regex.ToString(), RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, MatchTimeout);
            compiled = new TriggerPattern(pattern, built, [.. slots], Math.Max(lastSlot + 1, 10), usesSlotZero);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Finds the first match at or after <paramref name="startIndex"/>.</summary>
    public bool TryMatch(string text, int startIndex, out PatternMatch match)
    {
        ArgumentNullException.ThrowIfNull(text);
        match = default;
        if (startIndex > text.Length)
        {
            return false;
        }

        Match m;
        try
        {
            m = _regex.Match(text, startIndex);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological pattern must not stall the session. Treat it as no match.
            return false;
        }
        if (!m.Success)
        {
            return false;
        }

        string[] captures = new string[_slotCount];
        Array.Fill(captures, string.Empty);
        if (!_usesSlotZero)
        {
            captures[0] = m.Value;
        }
        for (int g = 1; g < _slotOfGroup.Length; g++)
        {
            Group group = m.Groups["g" + g.ToString(CultureInfo.InvariantCulture)];
            if (group.Success)
            {
                captures[_slotOfGroup[g]] = group.Value;
            }
        }
        match = new PatternMatch(m.Index, m.Length, captures);
        return true;
    }

    public bool IsMatch(string text) => TryMatch(text, 0, out _);

    private static int FindClosingBrace(string pattern, int open, int end)
    {
        int depth = 0;
        for (int i = open; i < end; i++)
        {
            if (pattern[i] == '\\')
            {
                i++;
            }
            else if (pattern[i] == '{')
            {
                depth++;
            }
            else if (pattern[i] == '}' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Appends the regex for the wildcard starting after a percent sign. Returns the next index.</summary>
    private static int AppendWildcard(string pattern, int i, int end, StringBuilder regex, List<int> slots, ref int lastSlot, ref bool usesSlotZero)
    {
        bool capture = true;
        if (pattern[i] == '!' && i + 1 < end)
        {
            capture = false;
            i++;
        }

        char c = pattern[i];
        if (c == '%')
        {
            regex.Append('%');
            return i + 1;
        }
        if (c == 'i')
        {
            regex.Append("(?i)");
            return i + 1;
        }
        if (c == 'I')
        {
            regex.Append("(?-i)");
            return i + 1;
        }

        int slot = -1;
        string charClass;
        string quantifier = "*";

        if (char.IsAsciiDigit(c))
        {
            slot = c - '0';
            i++;
            if (i < end && char.IsAsciiDigit(pattern[i]))
            {
                slot = (slot * 10) + (pattern[i] - '0');
                i++;
            }
            charClass = ".";
        }
        else
        {
            if (c == '+' && i + 1 < end && char.IsAsciiDigit(pattern[i + 1]))
            {
                // %+N..Mx or %+Nx : explicit repeat range for class x.
                i++;
                int min = ReadNumber(pattern, ref i, end);
                string max = min.ToString(CultureInfo.InvariantCulture);
                if (i + 1 < end && pattern[i] == '.' && pattern[i + 1] == '.')
                {
                    i += 2;
                    max = i < end && char.IsAsciiDigit(pattern[i])
                        ? ReadNumber(pattern, ref i, end).ToString(CultureInfo.InvariantCulture)
                        : string.Empty;
                }
                quantifier = "{" + min.ToString(CultureInfo.InvariantCulture) + "," + max + "}";
                if (i >= end)
                {
                    regex.Append(Regex.Escape("%+"));
                    return end;
                }
                c = pattern[i];
                string? ranged = ClassFor(c);
                if (ranged is null)
                {
                    regex.Append(Regex.Escape("%+"));
                    return i;
                }
                charClass = ranged;
                i++;
            }
            else
            {
                switch (c)
                {
                    case '*': charClass = "."; break;
                    case '+': charClass = "."; quantifier = "+"; break;
                    case '?': charClass = "."; quantifier = "?"; break;
                    case '.': charClass = "."; quantifier = "{1}"; break;
                    default:
                        string? named = ClassFor(c);
                        if (named is null)
                        {
                            // Not a wildcard: a literal percent sign.
                            regex.Append('%');
                            return capture ? i : i - 1;
                        }
                        charClass = named;
                        break;
                }
                i++;
            }
        }

        // A wildcard that ends the pattern is greedy; anywhere else it is lazy.
        bool last = i >= end;
        string body = charClass + quantifier + (last || quantifier == "{1}" ? string.Empty : "?");
        if (!capture)
        {
            regex.Append("(?:").Append(body).Append(')');
            return i;
        }

        if (slot < 0)
        {
            slot = ++lastSlot;
        }
        else
        {
            lastSlot = Math.Max(lastSlot, slot);
            usesSlotZero |= slot == 0;
        }
        slots.Add(slot);
        AppendGroupStart(regex, slots.Count - 1);
        regex.Append(body).Append(')');
        return i;
    }

    // Groups are named so that unnamed groups inside an embedded {regex} cannot shift the
    // numbering. ExplicitCapture turns those inner groups into non-capturing ones.
    private static void AppendGroupStart(StringBuilder regex, int group) =>
        regex.Append("(?<g").Append(group.ToString(CultureInfo.InvariantCulture)).Append('>');

    private static string? ClassFor(char c) => c switch
    {
        'd' => "[0-9]",
        'D' => "[^0-9]",
        'w' => @"\w",
        'W' => @"\W",
        's' => @"\s",
        'S' => @"\S",
        'a' => ".",
        _ => null,
    };

    private static int ReadNumber(string pattern, ref int i, int end)
    {
        int value = 0;
        while (i < end && char.IsAsciiDigit(pattern[i]))
        {
            value = Math.Min((value * 10) + (pattern[i] - '0'), 9999);
            i++;
        }
        return value;
    }
}

/// <summary>A successful <see cref="TriggerPattern"/> match. Slot 0 is the whole match unless the pattern captured into %0.</summary>
public readonly record struct PatternMatch(int Index, int Length, IReadOnlyList<string> Captures);
