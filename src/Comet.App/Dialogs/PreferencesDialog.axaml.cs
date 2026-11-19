using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Comet.Core.Configuration;
using Comet.Core.Text;

namespace Comet.App.Dialogs;

/// <summary>Edits a copy of the settings. Closes with true after writing the copy back on OK.</summary>
public sealed partial class PreferencesDialog : Window
{
    private const string DefaultFontLabel = "Default (Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono)";

    private static readonly string[] PaletteNames =
    [
        "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White",
        "Bright black", "Bright red", "Bright green", "Bright yellow", "Bright blue", "Bright magenta", "Bright cyan", "Bright white",
    ];

    private readonly AppSettings _settings;
    private readonly List<(string Name, TextBox Box, Border Swatch)> _colorFields = [];
    private readonly ScrollbackBuffer _previewBuffer = new(ScrollbackBuffer.MinCapacity);
    private bool _loading;

    public PreferencesDialog() : this(new AppSettings())
    {
    }

    public PreferencesDialog(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        _settings = settings;

        var fonts = new List<string> { DefaultFontLabel };
        fonts.AddRange(FontManager.Current.SystemFonts.Select(static f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        if (settings.FontFamily != AppSettings.DefaultFontFamily && !fonts.Contains(settings.FontFamily, StringComparer.OrdinalIgnoreCase))
        {
            fonts.Insert(1, settings.FontFamily);
        }
        FontBox.ItemsSource = fonts;

        StackPanel foreground = AddColorField("Text", "Default text color");
        StackPanel background = AddColorField("Background", "Background color");
        Grid.SetColumn(foreground, 0);
        Grid.SetColumn(background, 2);
        BaseColors.Children.Add(foreground);
        BaseColors.Children.Add(background);
        foreach (string name in PaletteNames)
        {
            PaletteGrid.Children.Add(AddColorField(name, name + " ANSI color"));
        }

        foreach (string line in (string[])
        [
            "\x1b[1;37mThe Town Square\x1b[0m",
            "You stand in the middle of a busy square. \x1b[36mExits: north, east, west.\x1b[0m",
            "\x1b[31mred \x1b[32mgreen \x1b[33myellow \x1b[34mblue \x1b[35mmagenta \x1b[36mcyan \x1b[37mwhite\x1b[0m  \x1b[1;31mred \x1b[1;32mgreen \x1b[1;33myellow \x1b[1;34mblue \x1b[1;35mmagenta \x1b[1;36mcyan\x1b[0m",
            "\x1b[33mA city guard\x1b[0m says, 'Move along.'   \x1b[4munderlined\x1b[0m  \x1b[7m inverse \x1b[0m",
            "<120hp 85mana 200mv> ",
        ])
        {
            _previewBuffer.Append(AnsiParser.ParseLine(line));
        }
        Preview.Buffer = _previewBuffer;

        Load(settings);

        FontBox.SelectionChanged += (_, _) => UpdatePreview();
        FontSizeBox.ValueChanged += (_, _) => UpdatePreview();
        BoldBrightBox.IsCheckedChanged += (_, _) => UpdatePreview();
        WordWrapBox.IsCheckedChanged += (_, _) => UpdatePreview();
        DefaultsButton.Click += (_, _) => RestoreDefaults();
        CancelButton.Click += (_, _) => Close(false);
        OkButton.Click += (_, _) => Accept();
    }

    private StackPanel AddColorField(string name, string accessibleName)
    {
        var swatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new TextBox { MaxLength = 7, Width = 92, FontFamily = new FontFamily(AppSettings.DefaultFontFamily) };
        Avalonia.Automation.AutomationProperties.SetName(box, accessibleName);
        box.TextChanged += (_, _) =>
        {
            bool valid = TryParseColor(box.Text, out Color color);
            swatch.Background = valid ? new SolidColorBrush(color) : null;
            // Shape as well as color marks a bad value: the swatch empties and the box says why.
            ToolTip.SetTip(box, valid ? null : "Enter a color as #RRGGBB, for example #FFAA00.");
            UpdatePreview();
        };
        _colorFields.Add((name, box, swatch));

        return new StackPanel
        {
            Margin = new Thickness(0, 0, 8, 8),
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = name, FontSize = 12 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { swatch, box } },
            },
        };
    }

    private static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        string value = (text ?? string.Empty).Trim();
        return value.Length == 7 && value[0] == '#' && Color.TryParse(value, out color);
    }

    private void Load(AppSettings source)
    {
        _loading = true;
        FontBox.SelectedItem = source.FontFamily == AppSettings.DefaultFontFamily ? DefaultFontLabel : source.FontFamily;
        if (FontBox.SelectedIndex < 0)
        {
            FontBox.SelectedIndex = 0;
        }
        FontSizeBox.Value = (decimal)source.FontSize;
        _colorFields[0].Box.Text = source.Foreground;
        _colorFields[1].Box.Text = source.Background;
        for (int i = 0; i < 16; i++)
        {
            _colorFields[i + 2].Box.Text = i < source.Palette.Count ? source.Palette[i] : AppSettings.DefaultPalette[i];
        }
        BoldBrightBox.IsChecked = source.BoldAsBright;
        ScrollbackBox.Value = source.ScrollbackLines;
        WordWrapBox.IsChecked = source.WordWrap;
        EchoBox.IsChecked = source.EchoCommands;
        KeepInputBox.IsChecked = source.KeepInputAfterSend;
        BellBox.IsChecked = source.AudibleBell;
        ConfirmCloseBox.IsChecked = source.ConfirmCloseWhenConnected;
        _loading = false;
        UpdatePreview();
    }

    /// <summary>Copies the form into <paramref name="target"/>. Returns a message naming the first invalid field, or null.</summary>
    private string? Store(AppSettings target)
    {
        foreach ((string name, TextBox box, _) in _colorFields)
        {
            if (!TryParseColor(box.Text, out _))
            {
                return $"{name}: enter a color as #RRGGBB.";
            }
        }

        target.FontFamily = FontBox.SelectedItem is string font && font != DefaultFontLabel ? font : AppSettings.DefaultFontFamily;
        target.FontSize = (double)(FontSizeBox.Value ?? 13);
        target.Foreground = _colorFields[0].Box.Text!.Trim().ToUpperInvariant();
        target.Background = _colorFields[1].Box.Text!.Trim().ToUpperInvariant();
        target.Palette = [.. _colorFields.Skip(2).Select(static f => f.Box.Text!.Trim().ToUpperInvariant())];
        target.BoldAsBright = BoldBrightBox.IsChecked == true;
        target.ScrollbackLines = (int)(ScrollbackBox.Value ?? 50_000);
        target.WordWrap = WordWrapBox.IsChecked == true;
        target.EchoCommands = EchoBox.IsChecked == true;
        target.KeepInputAfterSend = KeepInputBox.IsChecked == true;
        target.AudibleBell = BellBox.IsChecked == true;
        target.ConfirmCloseWhenConnected = ConfirmCloseBox.IsChecked == true;
        target.Normalize();
        return null;
    }

    private void UpdatePreview()
    {
        if (_loading)
        {
            return;
        }
        var draft = new AppSettings();
        if (Store(draft) is null)
        {
            ErrorText.Text = string.Empty;
            Preview.ApplySettings(draft);
        }
    }

    private void RestoreDefaults()
    {
        Load(new AppSettings());
        ErrorText.Text = "Defaults restored. Choose OK to keep them.";
    }

    private void Accept()
    {
        if (Store(new AppSettings()) is { } problem)
        {
            ErrorText.Text = problem;
            return;
        }
        Store(_settings);
        Close(true);
    }
}
