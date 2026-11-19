using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Comet.Core.Configuration;
using Comet.Core.Scripting;

namespace Comet.App.Dialogs;

/// <summary>A searchable reference of every # command, plus the basics of the command line.</summary>
public sealed class HelpWindow : Window
{
    private static readonly (string Title, string Text)[] Basics =
    [
        ("Several commands on one line", "Separate them with a semicolon: north;open door;east. Type \\; for a real semicolon."),
        ("Repeat", "#5 {kill rat} sends the command five times."),
        ("Speedwalk", "3n2e walks north three times and east twice. A count is required, so ordinary words are never walked."),
        ("Send exactly what you type", "Start the line with \\ to skip aliases and commands, or turn on Scripts > Verbatim Input."),
        ("Variables", "#variable {target} {orc} then kill $target."),
        ("Patterns", "In actions, highlights, gags and substitutions: ^ matches the start of the line, $ the end, %1 to %9 capture any text, %d digits, %w a word, and {regex} embeds a regular expression."),
        ("Keys", "Up and Down recall earlier commands. Tab completes a word. Page Up and Page Down scroll the output; Esc returns to the bottom."),
    ];

    private readonly char _commandChar;
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBlock _empty = new() { Text = "No command matches.", IsVisible = false };

    public HelpWindow() : this('#')
    {
    }

    public HelpWindow(char commandChar)
    {
        _commandChar = commandChar;
        Title = "Command Reference";
        Width = 720;
        Height = 640;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var filter = new TextBox { PlaceholderText = "Search commands" };
        Avalonia.Automation.AutomationProperties.SetName(filter, "Search commands");
        filter.TextChanged += (_, _) => Populate(filter.Text ?? string.Empty);

        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();

        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(filter, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(filter);
        panel.Children.Add(close);
        panel.Children.Add(new ScrollViewer
        {
            Margin = new Thickness(0, 12),
            Content = new StackPanel { Margin = new Thickness(0, 0, 12, 0), Children = { _empty, _list } },
        });
        Content = panel;

        Populate(string.Empty);
        Opened += (_, _) => filter.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    private void Populate(string filter)
    {
        filter = filter.Trim().TrimStart(_commandChar);
        var mono = new FontFamily(AppSettings.DefaultFontFamily);
        _list.Children.Clear();

        if (filter.Length == 0)
        {
            _list.Children.Add(new TextBlock { Text = "The command line", Classes = { "h1" } });
            foreach ((string title, string text) in Basics)
            {
                _list.Children.Add(Entry(title, text, null));
            }
            _list.Children.Add(new TextBlock { Text = "Commands", Classes = { "h1" }, Margin = new Thickness(0, 12, 0, 0) });
            _list.Children.Add(new TextBlock
            {
                Classes = { "hint" },
                Text = $"Names may be shortened: {_commandChar}al works for {_commandChar}alias. Arguments go in braces when they contain spaces.",
            });
        }

        int shown = 0;
        foreach ((string name, string usage, string summary) in ScriptEngine.CommandReference)
        {
            if (filter.Length > 0
                && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !summary.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            shown++;
            _list.Children.Add(Entry($"{_commandChar}{name} {usage}".TrimEnd(), summary, mono));
        }
        _empty.IsVisible = shown == 0 && filter.Length > 0;
    }

    private static StackPanel Entry(string title, string text, FontFamily? titleFont)
    {
        var heading = new SelectableTextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (titleFont is not null)
        {
            heading.FontFamily = titleFont;
        }
        return new StackPanel
        {
            Spacing = 2,
            Children = { heading, new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left } },
        };
    }
}
