using System.Globalization;
using Comet.Core.Text;

namespace Comet.Core.Scripting;

/// <summary>The colors and attributes a #highlight applies over matched text.</summary>
public sealed record HighlightStyle(TextColor? Foreground, TextColor? Background, TextAttributes Attributes)
{
    /// <summary>Color names accepted in a highlight, in the order shown to users.</summary>
    public static IReadOnlyList<string> ColorNames { get; } =
    [
        "black", "red", "green", "brown", "blue", "magenta", "cyan", "grey",
        "charcoal", "light red", "light green", "yellow", "light blue", "light magenta", "light cyan", "white",
    ];

    public static IReadOnlyList<string> AttributeNames { get; } =
        ["bold", "italic", "underline", "reverse", "blink", "strikethrough"];

    public TextStyle Apply(TextStyle style) => new(
        Foreground ?? style.Foreground,
        Background ?? style.Background,
        style.Attributes | Attributes);

    /// <summary>
    /// Parses a comma-separated spec such as "light red, b blue, underline". A color is a
    /// name, a 0-255 palette index or #rrggbb; the prefix "b " makes it the background.
    /// </summary>
    public static bool TryParse(string spec, out HighlightStyle? style)
    {
        ArgumentNullException.ThrowIfNull(spec);
        style = null;
        TextColor? foreground = null;
        TextColor? background = null;
        TextAttributes attributes = TextAttributes.None;
        bool any = false;

        foreach (string raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = string.Join(' ', raw.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            any = true;

            TextAttributes? attribute = token switch
            {
                "bold" => TextAttributes.Bold,
                "italic" => TextAttributes.Italic,
                "underline" or "underscore" => TextAttributes.Underline,
                "reverse" or "inverse" => TextAttributes.Inverse,
                "blink" => TextAttributes.Blink,
                "strikethrough" or "strike" => TextAttributes.Strikethrough,
                _ => null,
            };
            if (attribute is not null)
            {
                attributes |= attribute.Value;
                continue;
            }

            bool isBackground = token.StartsWith("b ", StringComparison.Ordinal);
            if (isBackground)
            {
                token = token[2..];
            }
            if (!TryParseColor(token, out TextColor color))
            {
                return false;
            }
            if (isBackground)
            {
                background = color;
            }
            else
            {
                foreground = color;
            }
        }

        if (!any)
        {
            return false;
        }
        style = new HighlightStyle(foreground, background, attributes);
        return true;
    }

    private static bool TryParseColor(string token, out TextColor color)
    {
        color = TextColor.Default;
        if (token.Length == 7 && token[0] == '#'
            && int.TryParse(token.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            color = new TextColor(TextColorKind.Rgb, rgb);
            return true;
        }
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index <= 255)
        {
            color = TextColor.Palette(index);
            return true;
        }

        bool bright = false;
        foreach (string prefix in (ReadOnlySpan<string>)["light ", "bright "])
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
            {
                bright = true;
                token = token[prefix.Length..];
            }
        }

        int basic = token switch
        {
            "black" => 0,
            "red" => 1,
            "green" => 2,
            "brown" => 3,
            "blue" => 4,
            "magenta" or "purple" => 5,
            "cyan" => 6,
            "grey" or "gray" => 7,
            "charcoal" => 8,
            "yellow" => 11,
            "white" => 15,
            _ => -1,
        };
        if (basic < 0)
        {
            return false;
        }
        color = TextColor.Palette(bright && basic < 8 ? basic + 8 : basic);
        return true;
    }
}
