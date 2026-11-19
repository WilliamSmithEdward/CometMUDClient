using System.Text;

namespace Comet.Core.Scripting;

/// <summary>Lexical helpers for the TinTin-style command language.</summary>
public static class ScriptText
{
    private const string EscapableCharacters = ";{}$%\\#";

    /// <summary>Splits a line into commands at each separator outside braces. A backslash escapes the next character.</summary>
    public static List<string> SplitCommands(string input, char separator)
    {
        ArgumentNullException.ThrowIfNull(input);
        var commands = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\\' && i + 1 < input.Length)
            {
                i++;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == separator && depth == 0)
            {
                commands.Add(input[start..i]);
                start = i + 1;
            }
        }
        commands.Add(input[start..]);
        return commands;
    }

    /// <summary>
    /// Takes the next argument from <paramref name="rest"/>: a braced group with its outer
    /// braces removed, or else one word, or the remainder of the line.
    /// </summary>
    public static string TakeArgument(ref ReadOnlySpan<char> rest, bool restOfLine)
    {
        rest = rest.TrimStart();
        if (rest.IsEmpty)
        {
            return string.Empty;
        }

        if (rest[0] == '{')
        {
            int depth = 0;
            for (int i = 0; i < rest.Length; i++)
            {
                char c = rest[i];
                if (c == '\\' && i + 1 < rest.Length)
                {
                    i++;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}' && --depth == 0)
                {
                    string inner = rest[1..i].ToString();
                    rest = rest[(i + 1)..];
                    return inner;
                }
            }
            // Unbalanced: take what is there rather than lose the user's text.
            string unbalanced = rest[1..].ToString();
            rest = [];
            return unbalanced;
        }

        if (restOfLine)
        {
            string all = rest.TrimEnd().ToString();
            rest = [];
            return all;
        }

        int end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end]))
        {
            end++;
        }
        string word = rest[..end].ToString();
        rest = rest[end..];
        return word;
    }

    /// <summary>Splits an argument string into words, treating each braced group as one word.</summary>
    public static List<string> SplitArguments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = new List<string>();
        ReadOnlySpan<char> rest = text;
        while (true)
        {
            rest = rest.TrimStart();
            if (rest.IsEmpty)
            {
                return words;
            }
            words.Add(TakeArgument(ref rest, restOfLine: false));
        }
    }

    /// <summary>True when the brace depth of <paramref name="text"/> returns to zero.</summary>
    public static bool BracesBalanced(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
        }
        return depth <= 0;
    }

    /// <summary>True when the text holds a %0-%99 reference that <see cref="SubstituteArguments"/> would replace.</summary>
    public static bool HasArgumentReference(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '%')
            {
                if (text[i + 1] == '%')
                {
                    i++;
                }
                else if (char.IsAsciiDigit(text[i + 1]))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Replaces %0-%99 with <paramref name="arguments"/>. A doubled percent defers one level,
    /// so "%%1" becomes "%1" for a body that is defined now and run later.
    /// </summary>
    public static string SubstituteArguments(string text, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!text.Contains('%', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                result.Append(c).Append(text[++i]);
            }
            else if (c == '%' && i + 1 < text.Length && text[i + 1] == '%')
            {
                int run = i;
                while (run < text.Length && text[run] == '%')
                {
                    run++;
                }
                bool reference = run < text.Length && char.IsAsciiDigit(text[run]);
                result.Append('%', reference ? run - i - 1 : run - i);
                i = run - 1;
            }
            else if (c == '%' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
            {
                int index = text[++i] - '0';
                if (i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]) && (index * 10) + (text[i + 1] - '0') < arguments.Count)
                {
                    index = (index * 10) + (text[++i] - '0');
                }
                if (index < arguments.Count)
                {
                    result.Append(arguments[index]);
                }
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Replaces $name and ${name} with variable values. Unknown names are left as typed.
    /// With <paramref name="topLevelOnly"/>, text inside braces is left for whoever runs it later.
    /// </summary>
    public static string SubstituteVariables(string text, Func<string, string?> lookup, bool topLevelOnly)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lookup);
        if (!text.Contains('$', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length + 16);
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                result.Append(c).Append(text[++i]);
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
            }

            if (c != '$' || (topLevelOnly && depth > 0) || i + 1 >= text.Length)
            {
                result.Append(c);
                continue;
            }

            if (text[i + 1] == '$')
            {
                // "$$name" defers one level, like "%%1".
                result.Append('$');
                i++;
                continue;
            }

            int nameStart;
            int nameEnd;
            int resume;
            if (text[i + 1] == '{')
            {
                int close = text.IndexOf('}', i + 2);
                if (close < 0)
                {
                    result.Append(c);
                    continue;
                }
                nameStart = i + 2;
                nameEnd = close;
                resume = close;
            }
            else
            {
                nameStart = i + 1;
                nameEnd = nameStart;
                while (nameEnd < text.Length && (char.IsAsciiLetterOrDigit(text[nameEnd]) || text[nameEnd] == '_'))
                {
                    nameEnd++;
                }
                resume = nameEnd - 1;
            }

            string? value = nameEnd > nameStart ? lookup(text[nameStart..nameEnd]) : null;
            if (value is null)
            {
                result.Append(c);
            }
            else
            {
                result.Append(value);
                i = resume;
            }
        }
        return result.ToString();
    }

    /// <summary>Removes the backslash from escaped special characters. Other backslashes are kept.</summary>
    public static string Unescape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && EscapableCharacters.Contains(text[i + 1], StringComparison.Ordinal))
            {
                i++;
            }
            result.Append(text[i]);
        }
        return result.ToString();
    }

    /// <summary>
    /// Expands a speedwalk word such as "3n2e" into single steps. Returns false when the word
    /// is not a speedwalk: it must hold only n, s, e, w, u, d and counts, include a count, and
    /// end in a direction.
    /// </summary>
    /// <remarks>
    /// Requiring a count keeps ordinary words made of those letters ("news", "see", "sw")
    /// from being walked.
    /// </remarks>
    public static bool TryExpandSpeedwalk(string word, out List<string> steps)
    {
        ArgumentNullException.ThrowIfNull(word);
        steps = [];
        const int MaxRepeat = 200;
        const int MaxSteps = 1000;

        bool sawCount = false;
        int count = -1;
        foreach (char c in word)
        {
            if (char.IsAsciiDigit(c))
            {
                count = (Math.Max(count, 0) * 10) + (c - '0');
                sawCount = true;
                if (count > MaxRepeat)
                {
                    return false;
                }
            }
            else if (c is 'n' or 's' or 'e' or 'w' or 'u' or 'd')
            {
                int repeat = count < 0 ? 1 : count;
                if (steps.Count + repeat > MaxSteps)
                {
                    return false;
                }
                for (int i = 0; i < repeat; i++)
                {
                    steps.Add(c.ToString());
                }
                count = -1;
            }
            else
            {
                return false;
            }
        }
        return sawCount && count < 0 && steps.Count > 0;
    }
}
