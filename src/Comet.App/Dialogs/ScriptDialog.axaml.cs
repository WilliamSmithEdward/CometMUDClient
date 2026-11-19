using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Comet.Core.Scripting;
using Comet.Core.Sessions;

namespace Comet.App.Dialogs;

/// <summary>Lists and edits a session's aliases, actions, highlights and other script items.</summary>
public sealed partial class ScriptDialog : Window
{
    private sealed record KindInfo(
        ScriptItemKind Kind,
        string Plural,
        string Singular,
        string KeyLabel,
        string? ValueLabel,
        string? ExtraLabel,
        string Hint);

    private static readonly KindInfo[] Kinds =
    [
        new(ScriptItemKind.Alias, "Aliases", "alias", "_Name (one word)", "_Commands", null,
            "Typing the name runs the commands. %1 to %9 stand for the words typed after the name and %0 for all of them. Separate commands with ; for example: kill %1;loot corpse"),
        new(ScriptItemKind.Action, "Actions", "action", "_Text to match", "_Commands", "_Priority",
            "Runs the commands when a line from the MUD contains the text. Start with ^ to match only at the start of a line. %1 to %9 capture any text and can be used in the commands, for example: ^%1 tells you '%2'"),
        new(ScriptItemKind.Highlight, "Highlights", "highlight", "_Text to match", "_Colors", null,
            "Colors matching text. Colors: " + string.Join(", ", HighlightStyle.ColorNames) + ". Put 'b ' in front for a background. Also: " + string.Join(", ", HighlightStyle.AttributeNames) + ". Separate several with commas, for example: light red, underline"),
        new(ScriptItemKind.Substitute, "Substitutions", "substitution", "_Text to match", "_Replace with", null,
            "Replaces matching text in the output. %1 to %9 captured in the match can be used in the replacement."),
        new(ScriptItemKind.Gag, "Gags", "gag", "_Text to match", null, null,
            "Hides every line from the MUD that contains the text."),
        new(ScriptItemKind.AntiSubstitute, "Anti-substitutes", "anti-substitute", "_Text to match", null, null,
            "Lines containing the text are never gagged or substituted."),
        new(ScriptItemKind.Variable, "Variables", "variable", "_Name", "_Value", null,
            "Use a variable in any command as $name. Names hold letters, digits and underscores."),
        new(ScriptItemKind.Ticker, "Tickers", "ticker", "_Name", "_Commands", "_Seconds",
            "Runs the commands again and again at a fixed interval."),
        new(ScriptItemKind.Macro, "Key macros", "key macro", "_Key", "_Commands", null,
            "Runs the commands when the key is pressed. Function keys, keypad keys and Ctrl or Alt combinations can be used."),
        new(ScriptItemKind.TabWord, "Tab words", "tab word", "_Word", null, null,
            "Words offered first when you press Tab in the command line. Words seen in the output are offered automatically."),
    ];

    private readonly MudSession _session;
    private ScriptItem? _original;
    private bool _loading;

    public ScriptDialog() : this(null!)
    {
    }

    public ScriptDialog(MudSession session)
    {
        InitializeComponent();
        _session = session;
        if (session is null)
        {
            return;
        }

        Title = $"Script Editor - {(string.IsNullOrWhiteSpace(session.Profile.Name) ? session.Profile.Host : session.Profile.Name)}";
        FileHint.Text = $"Saved automatically to {session.ScriptPath}";

        KindList.SelectionChanged += (_, _) => OnKindChanged();
        ItemList.SelectionChanged += (_, _) => LoadSelectedItem();
        FilterBox.TextChanged += (_, _) => RefreshItems(_original?.Key);
        NewButton.Click += (_, _) => StartNew();
        DeleteButton.Click += async (_, _) => await DeleteAsync().ConfigureAwait(true);
        SaveButton.Click += (_, _) => Save();
        RevertButton.Click += (_, _) => LoadSelectedItem();
        CloseButton.Click += (_, _) => Close();
        KeyBox.AddHandler(KeyDownEvent, OnKeyBoxKeyDown, RoutingStrategies.Tunnel);
        ValueBox.KeyDown += OnFieldKeyDown;
        ExtraBox.KeyDown += OnFieldKeyDown;
        KeyBox.KeyDown += OnFieldKeyDown;

        RefreshKinds(0);
        OnKindChanged();
    }

    private KindInfo Current => Kinds[Math.Max(0, KindList.SelectedIndex)];

    private IEnumerable<ScriptItem> ItemsOf(ScriptItemKind kind) =>
        _session.Engine.GetItems(kind).Where(static item => item.Kind != ScriptItemKind.Variable || !ScriptEngine.IsTransientVariable(item.Key));

    private void RefreshKinds(int select)
    {
        _loading = true;
        KindList.ItemsSource = Kinds
            .Select(info => $"{info.Plural} ({ItemsOf(info.Kind).Count().ToString(CultureInfo.InvariantCulture)})")
            .ToList();
        KindList.SelectedIndex = select;
        _loading = false;
    }

    private void OnKindChanged()
    {
        if (_loading || KindList.SelectedIndex < 0)
        {
            return;
        }
        KindInfo info = Current;
        ItemsHeading.Text = info.Plural;
        KindHint.Text = info.Hint;
        KeyLabel.Content = info.KeyLabel;
        ValuePane.IsVisible = info.ValueLabel is not null;
        ValueLabel.Content = info.ValueLabel;
        ExtraPane.IsVisible = info.ExtraLabel is not null;
        ExtraLabel.Content = info.ExtraLabel;
        ExtraHint.Text = info.Kind == ScriptItemKind.Action
            ? "Lower numbers run first. The usual value is 5."
            : "From 0.1 to 86400.";
        KeyHint.IsVisible = info.Kind == ScriptItemKind.Macro;
        KeyHint.Text = "Click in the box and press the key combination.";
        FilterBox.Text = string.Empty;
        RefreshItems(null);
    }

    private void RefreshItems(string? selectKey)
    {
        KindInfo info = Current;
        string filter = (FilterBox.Text ?? string.Empty).Trim();
        List<ScriptItem> items = [.. ItemsOf(info.Kind).Where(item =>
            filter.Length == 0
            || item.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))];

        _loading = true;
        ItemList.ItemsSource = items.Select(static item => new ListBoxItem { Tag = item, Content = item.Key }).ToList();
        int index = selectKey is null ? -1 : items.FindIndex(item => item.Key == selectKey);
        ItemList.SelectedIndex = index >= 0 ? index : (items.Count > 0 ? 0 : -1);
        _loading = false;

        NoItems.IsVisible = items.Count == 0;
        NoItems.Text = filter.Length > 0
            ? "Nothing matches the filter."
            : $"No {info.Plural.ToLowerInvariant()} yet. Fill in the form on the right and choose Save.";
        LoadSelectedItem();
    }

    private void LoadSelectedItem()
    {
        if (_loading)
        {
            return;
        }
        _original = (ItemList.SelectedItem as ListBoxItem)?.Tag as ScriptItem;
        KindInfo info = Current;
        FormHeading.Text = _original is null ? $"New {info.Singular}" : $"Edit {info.Singular}";
        KeyBox.Text = _original?.Key ?? string.Empty;
        ValueBox.Text = _original?.Value ?? string.Empty;
        ExtraBox.Text = _original?.Extra ?? (info.Kind == ScriptItemKind.Action ? "5" : info.Kind == ScriptItemKind.Ticker ? "60" : string.Empty);
        ErrorText.Text = string.Empty;
        DeleteButton.IsEnabled = _original is not null;
        RevertButton.IsEnabled = _original is not null;
    }

    private void StartNew()
    {
        _loading = true;
        ItemList.SelectedIndex = -1;
        _loading = false;
        LoadSelectedItem();
        KeyBox.Focus();
    }

    /// <summary>In the key field of a macro, a key press is recorded instead of typed.</summary>
    private void OnKeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (Current.Kind != ScriptItemKind.Macro || e.Key is Key.Tab or Key.Escape or Key.Enter || KeyNames.IsModifierKey(e.Key))
        {
            return;
        }
        if (KeyNames.IsMacroCandidate(e.Key, e.KeyModifiers))
        {
            KeyBox.Text = KeyNames.Describe(e.Key, e.KeyModifiers);
            ErrorText.Text = string.Empty;
        }
        else
        {
            ErrorText.Text = "That key is used for typing. Choose a function key, a keypad key, or a combination with Ctrl or Alt.";
        }
        e.Handled = true;
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.Handled)
        {
            Save();
            e.Handled = true;
        }
    }

    private static string? Validate(KindInfo info, string key, string value, ref string extra)
    {
        if (key.Length == 0)
        {
            return $"Fill in \"{info.KeyLabel.Replace("_", string.Empty, StringComparison.Ordinal)}\".";
        }
        if (info.ValueLabel is not null && value.Length == 0 && info.Kind != ScriptItemKind.Variable && info.Kind != ScriptItemKind.Substitute)
        {
            return $"Fill in \"{info.ValueLabel.Replace("_", string.Empty, StringComparison.Ordinal)}\".";
        }
        if (!ScriptText.BracesBalanced(key) || !ScriptText.BracesBalanced(value) || key.Count(c => c == '{') != key.Count(c => c == '}') || value.Count(c => c == '{') != value.Count(c => c == '}'))
        {
            return "Every { needs a matching }.";
        }

        switch (info.Kind)
        {
            case ScriptItemKind.Alias or ScriptItemKind.TabWord when key.Any(char.IsWhiteSpace):
                return "This must be a single word with no spaces.";
            case ScriptItemKind.Variable when char.IsAsciiDigit(key[0]) || !key.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'):
                return "A variable name may hold letters, digits and underscores, and must not start with a digit.";
            case ScriptItemKind.Action or ScriptItemKind.Substitute or ScriptItemKind.Gag or ScriptItemKind.AntiSubstitute or ScriptItemKind.Highlight:
                if (!TriggerPattern.TryCompile(key, out _, out string? patternError))
                {
                    return $"The text to match is not a valid pattern: {patternError}";
                }
                break;
        }

        switch (info.Kind)
        {
            case ScriptItemKind.Highlight when !HighlightStyle.TryParse(value, out _):
                return "One of the colors is not recognized. See the list above.";
            case ScriptItemKind.Action:
                if (!int.TryParse(extra, NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority) || priority is < 0 or > 9999)
                {
                    return "Priority must be a whole number from 0 to 9999.";
                }
                extra = priority.ToString(CultureInfo.InvariantCulture);
                break;
            case ScriptItemKind.Ticker:
                if (!double.TryParse(extra, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds is < 0.1 or > 86_400)
                {
                    return "Seconds must be a number from 0.1 to 86400.";
                }
                extra = seconds.ToString(CultureInfo.InvariantCulture);
                break;
            default:
                extra = string.Empty;
                break;
        }
        return null;
    }

    private void Save()
    {
        KindInfo info = Current;
        string key = (KeyBox.Text ?? string.Empty).Trim();
        string value = info.ValueLabel is null ? string.Empty : (ValueBox.Text ?? string.Empty).Trim();
        string extra = (ExtraBox.Text ?? string.Empty).Trim();
        if (info.Kind == ScriptItemKind.Macro)
        {
            key = ScriptEngine.NormalizeKey(key);
        }

        if (Validate(info, key, value, ref extra) is { } problem)
        {
            ErrorText.Text = problem;
            return;
        }
        if (_original?.Key != key && ItemsOf(info.Kind).Any(item => item.Key == key))
        {
            ErrorText.Text = $"There is already {(info.Singular[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an" : "a")} {info.Singular} for \"{key}\". Select it in the list to change it.";
            return;
        }

        if (_original is not null && _original.Key != key)
        {
            _session.Engine.RemoveItem(info.Kind, _original.Key);
        }
        _session.Engine.SetItem(new ScriptItem(info.Kind, key, value, extra));

        int kindIndex = KindList.SelectedIndex;
        RefreshKinds(kindIndex);
        OnKindChanged();
        RefreshItems(key);
    }

    private async Task DeleteAsync()
    {
        if (_original is not { } item)
        {
            return;
        }
        KindInfo info = Current;
        if (!await MessageDialog.ConfirmAsync(this, $"Delete {info.Singular}", $"Delete the {info.Singular} \"{item.Key}\"?", "Delete").ConfigureAwait(true))
        {
            return;
        }
        _session.Engine.RemoveItem(info.Kind, item.Key);
        int kindIndex = KindList.SelectedIndex;
        RefreshKinds(kindIndex);
        OnKindChanged();
    }
}
