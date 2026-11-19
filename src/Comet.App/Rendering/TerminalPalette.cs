using Avalonia.Media;
using Avalonia.Media.Immutable;
using Comet.Core.Configuration;
using Comet.Core.Text;

namespace Comet.App.Rendering;

/// <summary>Turns the abstract colors of a <see cref="TextStyle"/> into brushes for the current settings.</summary>
public sealed class TerminalPalette
{
    private static readonly Color FallbackForeground = Color.FromRgb(0xBB, 0xBB, 0xBB);
    private static readonly Color FallbackBackground = Colors.Black;

    private readonly Color[] _colors = new Color[256];
    private readonly Dictionary<uint, IImmutableSolidColorBrush> _brushes = [];
    private readonly bool _boldAsBright;

    public TerminalPalette(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _boldAsBright = settings.BoldAsBright;
        Foreground = ParseOr(settings.Foreground, FallbackForeground);
        Background = ParseOr(settings.Background, FallbackBackground);

        for (int i = 0; i < 16; i++)
        {
            string? configured = i < settings.Palette.Count ? settings.Palette[i] : null;
            _colors[i] = ParseOr(configured, ParseOr(AppSettings.DefaultPalette[i], FallbackForeground));
        }

        // xterm 256-color layout: a 6x6x6 cube, then a 24-step grey ramp.
        ReadOnlySpan<byte> levels = [0, 95, 135, 175, 215, 255];
        for (int i = 16; i < 232; i++)
        {
            int n = i - 16;
            _colors[i] = Color.FromRgb(levels[n / 36], levels[n / 6 % 6], levels[n % 6]);
        }
        for (int i = 232; i < 256; i++)
        {
            byte grey = (byte)(8 + ((i - 232) * 10));
            _colors[i] = Color.FromRgb(grey, grey, grey);
        }

        ForegroundBrush = Brush(Foreground);
        BackgroundBrush = Brush(Background);
    }

    public Color Foreground { get; }
    public Color Background { get; }
    public IImmutableSolidColorBrush ForegroundBrush { get; }
    public IImmutableSolidColorBrush BackgroundBrush { get; }

    /// <summary>True when bold text should also use a heavier font weight.</summary>
    public bool BoldUsesWeight => !_boldAsBright;

    public static Color ParseOr(string? text, Color fallback) =>
        !string.IsNullOrWhiteSpace(text) && Color.TryParse(text.Trim(), out Color color) ? color : fallback;

    /// <summary>Resolves the foreground and background to draw for a style. Background is null when it is the default.</summary>
    public (IImmutableSolidColorBrush Foreground, IImmutableSolidColorBrush? Background) Resolve(TextStyle style)
    {
        bool bold = style.Has(TextAttributes.Bold);
        Color foreground = style.Foreground.Kind switch
        {
            TextColorKind.Palette => _colors[_boldAsBright && bold && style.Foreground.Value < 8 ? style.Foreground.Value + 8 : style.Foreground.Value],
            TextColorKind.Rgb => FromRgb(style.Foreground.Value),
            _ => _boldAsBright && bold ? _colors[15] : Foreground,
        };
        Color? background = style.Background.Kind switch
        {
            TextColorKind.Palette => _colors[style.Background.Value],
            TextColorKind.Rgb => FromRgb(style.Background.Value),
            _ => null,
        };

        if (style.Has(TextAttributes.Inverse))
        {
            (foreground, background) = (background ?? Background, foreground);
        }
        if (style.Has(TextAttributes.Faint))
        {
            foreground = Blend(foreground, background ?? Background, 0.4);
        }
        if (style.Has(TextAttributes.Conceal))
        {
            foreground = background ?? Background;
        }
        return (Brush(foreground), background is { } bg ? Brush(bg) : null);
    }

    private static Color FromRgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color Blend(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + ((b.R - a.R) * amount)),
        (byte)(a.G + ((b.G - a.G) * amount)),
        (byte)(a.B + ((b.B - a.B) * amount)));

    private IImmutableSolidColorBrush Brush(Color color)
    {
        uint key = color.ToUInt32();
        if (!_brushes.TryGetValue(key, out IImmutableSolidColorBrush? brush))
        {
            brush = new ImmutableSolidColorBrush(color);
            _brushes[key] = brush;
        }
        return brush;
    }
}
