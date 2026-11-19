namespace Comet.Core.Text;

public enum TextColorKind : byte
{
    /// <summary>The view's configured default foreground or background.</summary>
    Default,
    /// <summary>An index into the 256-color palette. 0-15 are the user-configurable ANSI colors.</summary>
    Palette,
    /// <summary>A 24-bit color, packed 0xRRGGBB.</summary>
    Rgb,
}

public readonly record struct TextColor(TextColorKind Kind, int Value)
{
    public static readonly TextColor Default = new(TextColorKind.Default, 0);

    public static TextColor Palette(int index) => new(TextColorKind.Palette, Math.Clamp(index, 0, 255));

    public static TextColor Rgb(int r, int g, int b) =>
        new(TextColorKind.Rgb, (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255));

    public bool IsDefault => Kind == TextColorKind.Default;
}

[Flags]
public enum TextAttributes : ushort
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Conceal = 1 << 6,
    Strikethrough = 1 << 7,
}

public readonly record struct TextStyle(TextColor Foreground, TextColor Background, TextAttributes Attributes)
{
    public static readonly TextStyle Default = new(TextColor.Default, TextColor.Default, TextAttributes.None);

    public bool Has(TextAttributes attribute) => (Attributes & attribute) != 0;

    public TextStyle With(TextAttributes attribute) => this with { Attributes = Attributes | attribute };

    public TextStyle Without(TextAttributes attribute) => this with { Attributes = Attributes & ~attribute };
}

/// <summary>A span of a <see cref="TextLine"/> drawn in one style.</summary>
public readonly record struct StyleRun(int Start, int Length, TextStyle Style)
{
    public int End => Start + Length;
}
