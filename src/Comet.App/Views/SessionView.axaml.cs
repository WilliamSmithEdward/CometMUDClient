using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Comet.Core.Configuration;
using Comet.Core.Scripting;
using Comet.Core.Sessions;

namespace Comet.App.Views;

/// <summary>One session tab: output, scrollback tail, find bar and the input line.</summary>
public sealed partial class SessionView : UserControl
{
    private readonly AppSettings _settings;
    private bool _updatingScrollBar;
    private bool _sizeUpdateQueued;

    public SessionView(MudSession session, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        Session = session;
        _settings = settings;

        Output.Buffer = session.Buffer;
        Tail.Buffer = session.Buffer;
        ApplySettings();

        session.OutputChanged += OnOutputChanged;
        session.StatusChanged += OnStatusChanged;

        Output.ScrollChanged += UpdateScrollState;
        Output.LinkActivated += OnLinkActivated;
        Tail.LinkActivated += OnLinkActivated;
        Output.SizeChanged += (_, _) => QueueWindowSizeUpdate();
        Scroll.ValueChanged += OnScrollBarChanged;
        JumpToBottom.Click += (_, _) => ReturnToBottom();

        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        FindText.KeyDown += OnFindKeyDown;
        FindPreviousButton.Click += (_, _) => RunFind(forward: false);
        FindNextButton.Click += (_, _) => RunFind(forward: true);
        FindClose.Click += (_, _) => CloseFind();

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += async (_, _) => await CopySelectionAsync().ConfigureAwait(true);
        var selectAll = new MenuItem { Header = "Select All" };
        selectAll.Click += (_, _) => Output.SelectAll();
        var clear = new MenuItem { Header = "Clear Output" };
        clear.Click += (_, _) => ClearOutput();
        var menu = new ContextMenu { Items = { copy, selectAll, new Separator(), clear } };
        menu.Opening += (_, _) => copy.IsEnabled = Output.HasSelection || Tail.HasSelection;
        Output.ContextMenu = menu;

        UpdateScrollState();
        OnStatusChanged();
    }

    public MudSession Session { get; }

    /// <summary>Text arrived while this view was not the selected tab.</summary>
    public event Action? ActivityWhileHidden;

    public bool HasOutputSelection => Output.HasSelection || Tail.HasSelection;

    public void FocusInput() => Input.Focus();

    /// <summary>True when keyboard focus is in one of this view's text boxes.</summary>
    public bool IsTextEntryFocused => Input.IsFocused || FindText.IsFocused;

    public void ApplySettings()
    {
        Output.ApplySettings(_settings);
        Tail.ApplySettings(_settings);
        Input.FontFamily = new FontFamily(_settings.FontFamily);
        Input.FontSize = _settings.FontSize;
        Session.ApplySettings();
        QueueWindowSizeUpdate();
    }

    public async Task CopySelectionAsync()
    {
        string text = Output.HasSelection ? Output.GetSelectedText() : Tail.GetSelectedText();
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    public void SelectAllOutput() => Output.SelectAll();

    public void ClearOutput()
    {
        Output.ClearSelection();
        Tail.ClearSelection();
        Session.RunCommands(Session.Engine.CommandChar + "cls");
        Output.ScrollToBottom();
    }

    public void ReturnToBottom()
    {
        Output.ScrollToBottom();
        FocusInput();
    }

    public void ShowFind()
    {
        FindBar.IsVisible = true;
        FindStatus.Text = string.Empty;
        FindText.Focus();
        FindText.SelectAll();
    }

    private void CloseFind()
    {
        FindBar.IsVisible = false;
        FocusInput();
    }

    private void RunFind(bool forward)
    {
        string text = FindText.Text ?? string.Empty;
        if (text.Length == 0)
        {
            FindStatus.Text = string.Empty;
            return;
        }
        bool found = forward ? Output.FindNext(text) : Output.FindPrevious(text);
        FindStatus.Text = found ? string.Empty : (forward ? "No later match" : "No earlier match");
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunFind(forward: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    // ---- Session events --------------------------------------------------------------

    private void OnOutputChanged()
    {
        Output.NotifyBufferChanged();
        Tail.NotifyBufferChanged();
        UpdateScrollState();
        if (!IsEffectivelyVisible)
        {
            ActivityWhileHidden?.Invoke();
        }
    }

    private void OnStatusChanged()
    {
        bool password = Session.IsPasswordMode;
        PasswordLabel.IsVisible = password;
        Input.PasswordChar = password ? '•' : '\0';
        Input.PlaceholderText = password ? "Type your password and press Enter" : "Type a command and press Enter";
    }

    private void OnLinkActivated(Uri link)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            _ = launcher.LaunchUriAsync(link);
        }
    }

    // ---- Scrolling -------------------------------------------------------------------

    private void UpdateScrollState()
    {
        int count = Session.Buffer.Count;
        _updatingScrollBar = true;
        Scroll.Minimum = 0;
        Scroll.Maximum = Math.Max(0, count - 1);
        Scroll.ViewportSize = Output.VisibleLineCount;
        Scroll.SmallChange = 1;
        Scroll.LargeChange = Math.Max(1, Output.VisibleLineCount - 1);
        Scroll.Value = Output.BottomIndex;
        Scroll.IsEnabled = count > Output.VisibleLineCount || Output.IsScrolledBack;
        _updatingScrollBar = false;

        if (TailPane.IsVisible != Output.IsScrolledBack)
        {
            TailPane.IsVisible = Output.IsScrolledBack;
            TailPane.Height = Output.IsScrolledBack ? Math.Clamp(Bounds.Height * 0.3, 80, 320) : double.NaN;
        }
    }

    private void OnScrollBarChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingScrollBar)
        {
            Output.SetBottomIndex((int)Math.Round(e.NewValue));
        }
    }

    private void QueueWindowSizeUpdate()
    {
        if (_sizeUpdateQueued)
        {
            return;
        }
        _sizeUpdateQueued = true;
        // Coalesce the burst of size changes a window drag produces into one NAWS update.
        DispatcherTimer.RunOnce(() =>
        {
            _sizeUpdateQueued = false;
            (int columns, int rows) = Output.CellDimensions;
            Session.SetWindowSize(columns, rows);
        }, TimeSpan.FromMilliseconds(250));
    }

    // ---- Input -----------------------------------------------------------------------

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Enter:
                Submit();
                e.Handled = true;
                break;

            case Key.Up when e.KeyModifiers == KeyModifiers.None && !Session.IsPasswordMode:
                if (Session.History.Previous(Input.Text ?? string.Empty) is { } previous)
                {
                    SetInput(previous);
                }
                e.Handled = true;
                break;

            case Key.Down when e.KeyModifiers == KeyModifiers.None && !Session.IsPasswordMode:
                if (Session.History.Next() is { } next)
                {
                    SetInput(next);
                }
                e.Handled = true;
                break;

            case Key.Tab when e.KeyModifiers == KeyModifiers.None && !Session.IsPasswordMode:
                CompleteWord();
                e.Handled = true;
                break;

            case Key.PageUp:
                Output.ScrollPages(1);
                e.Handled = true;
                break;

            case Key.PageDown:
                Output.ScrollPages(-1);
                e.Handled = true;
                break;

            case Key.Home when control:
                Output.ScrollToTop();
                e.Handled = true;
                break;

            case Key.End when control:
                Output.ScrollToBottom();
                e.Handled = true;
                break;

            case Key.Escape:
                if (Output.IsScrolledBack)
                {
                    Output.ScrollToBottom();
                }
                else if (HasOutputSelection)
                {
                    Output.ClearSelection();
                    Tail.ClearSelection();
                }
                else
                {
                    Input.Text = string.Empty;
                    Session.History.ResetNavigation();
                }
                e.Handled = true;
                break;
        }
    }

    private void Submit()
    {
        string text = Input.Text ?? string.Empty;
        bool password = Session.IsPasswordMode;
        Output.ScrollToBottom();
        Session.SubmitInput(text);

        if (_settings.KeepInputAfterSend && !password && text.Length > 0)
        {
            Input.SelectAll();
        }
        else
        {
            Input.Text = string.Empty;
        }
    }

    private void SetInput(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
    }

    private void CompleteWord()
    {
        string text = Input.Text ?? string.Empty;
        IEnumerable<string> userWords = Session.Engine.GetItems(ScriptItemKind.TabWord).Select(static item => item.Key);
        if (Session.Completer.TryComplete(text, Input.CaretIndex, userWords, out string completed, out int caret))
        {
            // Pressing Tab again with this exact text cycles to the next candidate.
            Input.Text = completed;
            Input.CaretIndex = caret;
        }
    }

    /// <summary>Releases the session's event handlers when the tab closes.</summary>
    public void Detach()
    {
        Session.OutputChanged -= OnOutputChanged;
        Session.StatusChanged -= OnStatusChanged;
    }
}
