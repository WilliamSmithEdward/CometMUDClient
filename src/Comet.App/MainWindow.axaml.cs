using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Comet.App.Dialogs;
using Comet.App.Views;
using Comet.Core.Configuration;
using Comet.Core.Sessions;

namespace Comet.App;

public sealed partial class MainWindow : Window
{
    private static readonly KeyModifiers CommandKey = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly IBrush DisconnectedBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly DispatcherTimer _statusTimer;
    private readonly List<Action> _menuRefreshers = [];
    private Button? _connectButton;
    private Button? _disconnectButton;
    private Button? _stopLogButton;
    private DateTime _bellUntil;
    private bool _closeConfirmed;

    // The XAML loader needs a parameterless constructor for design-time tooling.
    public MainWindow() : this(new AppSettings(), AppPaths.Default(), null)
    {
    }

    public MainWindow(AppSettings settings, AppPaths paths, string? startupWarning)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        InitializeComponent();
        _settings = settings;
        _paths = paths;

        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        BuildMenu();
        BuildToolbar();
        ApplyChromeSettings();

        if (startupWarning is not null)
        {
            WarningText.Text = startupWarning;
            WarningBar.IsVisible = true;
        }
        WarningDismiss.Click += (_, _) => WarningBar.IsVisible = false;

        EmptyConnect.Click += async (_, _) => await ShowConnectDialogAsync().ConfigureAwait(true);
        RecentList.DoubleTapped += (_, _) => ConnectSelectedRecent();
        RecentList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ConnectSelectedRecent();
                e.Handled = true;
            }
        };

        Tabs.SelectionChanged += (_, _) => OnActiveSessionChanged();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        Opened += (_, _) => EnsureOnScreen();

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateStatus());
        _statusTimer.Start();

        RefreshEmptyState();
        UpdateStatus();
    }

    internal SessionView? ActiveView => (Tabs.SelectedItem as TabItem)?.Content as SessionView;

    private IEnumerable<SessionView> Views => Tabs.Items.OfType<TabItem>().Select(static tab => tab.Content).OfType<SessionView>();

    // ---- Menu and toolbar ------------------------------------------------------------

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }

    private MenuItem Item(string header, Action action, KeyGesture? gesture = null, bool bindKey = true)
    {
        var command = new RelayCommand(action);
        var item = new MenuItem { Header = header, Command = command, InputGesture = gesture };
        if (gesture is not null && bindKey)
        {
            KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = command });
        }
        return item;
    }

    private MenuItem Toggle(string header, Func<bool> get, Action<bool> set)
    {
        var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox };
        item.Click += (_, _) =>
        {
            set(!get());
            item.IsChecked = get();
        };
        _menuRefreshers.Add(() => item.IsChecked = get());
        return item;
    }

    private void EnableWhen(MenuItem item, Func<bool> condition) => _menuRefreshers.Add(() => item.IsEnabled = condition());

    private void BuildMenu()
    {
        bool HasSession() => ActiveView is not null;
        bool IsDisconnected() => ActiveView?.Session.State == SessionState.Disconnected;
        bool IsBusy() => ActiveView is { } view && view.Session.State != SessionState.Disconnected;

        MenuItem reconnect = Item("_Reconnect", () => Reconnect(), new KeyGesture(Key.R, CommandKey));
        MenuItem disconnect = Item("_Disconnect", () => ActiveView?.Session.Disconnect(), new KeyGesture(Key.D, CommandKey));
        MenuItem loadScript = Item("_Load Script...", async () => await LoadScriptAsync().ConfigureAwait(true));
        MenuItem saveScript = Item("_Save Script As...", async () => await SaveScriptAsAsync().ConfigureAwait(true));
        MenuItem startLog = Item("Lo_g to File...", async () => await StartLogAsync().ConfigureAwait(true), new KeyGesture(Key.L, CommandKey));
        MenuItem stopLog = Item("S_top Logging", StopLog);
        MenuItem closeTab = Item("_Close Tab", async () => await CloseTabAsync(Tabs.SelectedItem as TabItem).ConfigureAwait(true), new KeyGesture(Key.W, CommandKey));
        EnableWhen(reconnect, IsDisconnected);
        EnableWhen(disconnect, IsBusy);
        EnableWhen(loadScript, HasSession);
        EnableWhen(saveScript, HasSession);
        EnableWhen(startLog, HasSession);
        EnableWhen(stopLog, () => ActiveView?.Session.IsLogging == true);
        EnableWhen(closeTab, HasSession);

        var file = new MenuItem
        {
            Header = "_File",
            Items =
            {
                Item("_New Connection...", async () => await ShowConnectDialogAsync().ConfigureAwait(true), new KeyGesture(Key.N, CommandKey)),
                reconnect,
                disconnect,
                new Separator(),
                loadScript,
                saveScript,
                new Separator(),
                startLog,
                stopLog,
                new Separator(),
                Item("_Preferences...", async () => await ShowPreferencesAsync().ConfigureAwait(true), new KeyGesture(Key.OemComma, CommandKey)),
                new Separator(),
                closeTab,
                Item("E_xit", Close),
            },
        };

        // Copy and Paste show their shortcuts but are not window-level bindings: the focused
        // text box handles them unless the output has a selection (see OnPreviewKeyDown).
        MenuItem copy = Item("_Copy", async () => await CopyAsync().ConfigureAwait(true), new KeyGesture(Key.C, CommandKey), bindKey: false);
        MenuItem paste = Item("_Paste", async () => await PasteAsync().ConfigureAwait(true), new KeyGesture(Key.V, CommandKey), bindKey: false);
        MenuItem selectAll = Item("Select _All Output", () => ActiveView?.SelectAllOutput(), new KeyGesture(Key.A, CommandKey | KeyModifiers.Shift));
        MenuItem find = Item("_Find...", () => ActiveView?.ShowFind(), new KeyGesture(Key.F, CommandKey));
        MenuItem clear = Item("C_lear Output", () => ActiveView?.ClearOutput());
        foreach (MenuItem item in (MenuItem[])[copy, paste, selectAll, find, clear])
        {
            EnableWhen(item, HasSession);
        }
        var edit = new MenuItem { Header = "_Edit", Items = { copy, paste, selectAll, new Separator(), find, clear } };

        MenuItem bottom = Item("Return to _Bottom", () => ActiveView?.ReturnToBottom());
        EnableWhen(bottom, HasSession);
        var view = new MenuItem
        {
            Header = "_View",
            Items =
            {
                Toggle("_Toolbar", () => _settings.ShowToolbar, value => { _settings.ShowToolbar = value; ApplyChromeSettings(); SaveSettings(); }),
                Toggle("_Status Bar", () => _settings.ShowStatusBar, value => { _settings.ShowStatusBar = value; ApplyChromeSettings(); SaveSettings(); }),
                new Separator(),
                Item("_Larger Text", () => ChangeFontSize(+1), new KeyGesture(Key.OemPlus, CommandKey)),
                Item("S_maller Text", () => ChangeFontSize(-1), new KeyGesture(Key.OemMinus, CommandKey)),
                Item("_Reset Text Size", () => ChangeFontSize(0), new KeyGesture(Key.D0, CommandKey)),
                new Separator(),
                Item("_Next Tab", () => CycleTab(+1), new KeyGesture(Key.Tab, KeyModifiers.Control)),
                Item("_Previous Tab", () => CycleTab(-1), new KeyGesture(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift)),
                bottom,
            },
        };

        MenuItem editor = Item("Script _Editor...", async () => await ShowScriptEditorAsync().ConfigureAwait(true), new KeyGesture(Key.E, CommandKey));
        MenuItem speedwalk = Toggle("_Speedwalk (3n2e)", () => ActiveView?.Session.Engine.SpeedwalkEnabled == true, value => WithEngine(e => e.SpeedwalkEnabled = value));
        MenuItem ignore = Toggle("_Ignore Actions", () => ActiveView?.Session.Engine.IgnoreActions == true, value => WithEngine(e => e.IgnoreActions = value));
        MenuItem verbatim = Toggle("_Verbatim Input", () => ActiveView?.Session.Engine.Verbatim == true, value => WithEngine(e => e.Verbatim = value));
        foreach (MenuItem item in (MenuItem[])[editor, speedwalk, ignore, verbatim])
        {
            EnableWhen(item, HasSession);
        }
        var scripts = new MenuItem
        {
            Header = "_Scripts",
            Items =
            {
                editor,
                new Separator(),
                speedwalk,
                ignore,
                verbatim,
                Toggle("_Echo Sent Commands", () => _settings.EchoCommands, value => { _settings.EchoCommands = value; SaveSettings(); }),
            },
        };

        var help = new MenuItem
        {
            Header = "_Help",
            Items =
            {
                Item("_Command Reference", ShowHelp, new KeyGesture(Key.F1)),
                new Separator(),
                Item("_About Comet", async () => await new AboutDialog().ShowDialog(this).ConfigureAwait(true)),
            },
        };

        foreach (MenuItem top in (MenuItem[])[file, edit, view, scripts, help])
        {
            top.SubmenuOpened += (_, _) => RefreshMenuState();
            MainMenu.Items.Add(top);
        }
    }

    private void RefreshMenuState()
    {
        foreach (Action refresh in _menuRefreshers)
        {
            refresh();
        }
    }

    private void WithEngine(Action<Core.Scripting.ScriptEngine> change)
    {
        if (ActiveView is { } view)
        {
            change(view.Session.Engine);
        }
    }

    private Button ToolbarButton(string text, string tip, Action action)
    {
        var button = new Button { Content = text, Focusable = false };
        button.Classes.Add("toolbar");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        Toolbar.Children.Add(button);
        return button;
    }

    private void BuildToolbar()
    {
        string mod = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        ToolbarButton("New Connection", $"Open a saved connection or add a new one ({mod}+N)", async () => await ShowConnectDialogAsync().ConfigureAwait(true));
        _connectButton = ToolbarButton("Reconnect", $"Connect this tab again ({mod}+R)", () => Reconnect());
        _disconnectButton = ToolbarButton("Disconnect", $"Close this tab's connection ({mod}+D)", () => ActiveView?.Session.Disconnect());
        Toolbar.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 4), Background = Brushes.Gray, Opacity = 0.4 });
        ToolbarButton("Scripts", $"Edit aliases, actions, highlights and more ({mod}+E)", async () => await ShowScriptEditorAsync().ConfigureAwait(true));
        ToolbarButton("Find", $"Search the output ({mod}+F)", () => ActiveView?.ShowFind());
        ToolbarButton("Log", $"Save this session's output to a file ({mod}+L)", async () => await StartLogAsync().ConfigureAwait(true));
        _stopLogButton = ToolbarButton("Stop Log", "Stop logging this session", StopLog);
        Toolbar.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 4), Background = Brushes.Gray, Opacity = 0.4 });
        ToolbarButton("Preferences", $"Font, colors and behavior ({mod}+,)", async () => await ShowPreferencesAsync().ConfigureAwait(true));
    }

    private void ApplyChromeSettings()
    {
        ToolbarPane.IsVisible = _settings.ShowToolbar;
        StatusPane.IsVisible = _settings.ShowStatusBar;
    }

    // ---- Sessions and tabs -----------------------------------------------------------

    internal void OpenSession(SessionProfile profile)
    {
        // One tab per saved connection: two tabs would overwrite each other's script file.
        foreach (TabItem existing in Tabs.Items.OfType<TabItem>())
        {
            if (existing.Content is SessionView open && open.Session.Profile.Id == profile.Id)
            {
                Tabs.SelectedItem = existing;
                if (open.Session.State == SessionState.Disconnected)
                {
                    _ = open.Session.ConnectAsync();
                }
                return;
            }
        }

        // Sessions marshal network callbacks through the current synchronization context.
        if (SynchronizationContext.Current is null)
        {
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        }

        var session = new MudSession(profile, _settings, _paths);
        var view = new SessionView(session, _settings);

        var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Fill = DisconnectedBrush };
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        var close = new Button
        {
            Content = new ShapePath
            {
                Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
                Stroke = title.Foreground ?? Brushes.Gray,
                StrokeThickness = 1.4,
                Width = 8,
                Height = 8,
            },
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(close, "Close tab");
        ToolTip.SetTip(close, "Close tab");

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { dot, title, close } };
        var tab = new TabItem { Header = header, Content = view, Padding = new Thickness(10, 2, 4, 2) };
        if (close.Content is ShapePath glyph)
        {
            glyph.Bind(Shape.StrokeProperty, title.GetObservable(TextBlock.ForegroundProperty));
        }

        bool unseen = false;
        void RefreshHeader()
        {
            SessionState state = session.State;
            string name = SessionTitle(session);
            title.Text = unseen ? name + " *" : name;
            title.FontWeight = ReferenceEquals(Tabs.SelectedItem, tab) || unseen ? FontWeight.SemiBold : FontWeight.Normal;
            dot.Fill = state switch
            {
                SessionState.Connected => ConnectedBrush,
                SessionState.Connecting => ConnectingBrush,
                _ => DisconnectedBrush,
            };
            ToolTip.SetTip(header, $"{name}: {DescribeState(session)}");
        }

        session.StatusChanged += () =>
        {
            RefreshHeader();
            UpdateStatus();
        };
        session.BellRang += OnBell;
        view.ActivityWhileHidden += () =>
        {
            if (!unseen)
            {
                unseen = true;
                RefreshHeader();
            }
        };
        tab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty)
            {
                unseen = false;
                RefreshHeader();
            }
        };
        close.Click += async (_, _) => await CloseTabAsync(tab).ConfigureAwait(true);

        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        RefreshHeader();
        RefreshEmptyState();
        Dispatcher.UIThread.Post(view.FocusInput, DispatcherPriority.Input);
        _ = session.ConnectAsync();
    }

    private static string SessionTitle(MudSession session) =>
        !string.IsNullOrWhiteSpace(session.Profile.Name) ? session.Profile.Name
        : !string.IsNullOrWhiteSpace(session.Profile.Host) ? session.Profile.Host
        : "Session";

    private static string DescribeState(MudSession session) => session.State switch
    {
        SessionState.Connected => "connected",
        SessionState.Connecting => "connecting",
        _ => "not connected",
    };

    private async Task CloseTabAsync(TabItem? tab)
    {
        if (tab?.Content is not SessionView view)
        {
            return;
        }
        if (view.Session.State == SessionState.Connected && _settings.ConfirmCloseWhenConnected
            && !await MessageDialog.ConfirmAsync(this, "Close tab", $"{SessionTitle(view.Session)} is still connected. Close the tab and disconnect?", "Disconnect and Close").ConfigureAwait(true))
        {
            return;
        }

        view.Detach();
        Tabs.Items.Remove(tab);
        await view.Session.DisposeAsync().ConfigureAwait(true);
        RefreshEmptyState();
        OnActiveSessionChanged();
    }

    private void CycleTab(int direction)
    {
        int count = Tabs.Items.Count;
        if (count > 1)
        {
            Tabs.SelectedIndex = (Tabs.SelectedIndex + direction + count) % count;
        }
    }

    private void OnActiveSessionChanged()
    {
        UpdateStatus();
        if (ActiveView is { } view)
        {
            Dispatcher.UIThread.Post(view.FocusInput, DispatcherPriority.Input);
        }
    }

    private void Reconnect()
    {
        if (ActiveView is { Session.State: SessionState.Disconnected } view)
        {
            _ = view.Session.ConnectAsync();
        }
    }

    private void RefreshEmptyState()
    {
        bool empty = Tabs.Items.Count == 0;
        EmptyState.IsVisible = empty;
        Tabs.IsVisible = !empty;
        if (!empty)
        {
            return;
        }
        RecentList.ItemsSource = _settings.Profiles
            .Select(profile => new ListBoxItem
            {
                Tag = profile,
                Content = $"{(string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name)}    {profile.Host}:{profile.Port.ToString(CultureInfo.InvariantCulture)}{(profile.UseTls ? "  (TLS)" : string.Empty)}",
            })
            .ToList();
        RecentPane.IsVisible = _settings.Profiles.Count > 0;
    }

    private void ConnectSelectedRecent()
    {
        if (RecentList.SelectedItem is ListBoxItem { Tag: SessionProfile profile })
        {
            OpenSession(profile);
        }
    }

    // ---- Commands --------------------------------------------------------------------

    private async Task ShowConnectDialogAsync()
    {
        var dialog = new ConnectDialog(_settings, SaveSettings);
        SessionProfile? profile = await dialog.ShowDialog<SessionProfile?>(this).ConfigureAwait(true);
        SaveSettings();
        RefreshEmptyState();
        if (profile is not null)
        {
            OpenSession(profile);
        }
    }

    private async Task ShowPreferencesAsync()
    {
        var dialog = new PreferencesDialog(_settings);
        if (await dialog.ShowDialog<bool>(this).ConfigureAwait(true))
        {
            ApplySettingsToViews();
            SaveSettings();
        }
    }

    private async Task ShowScriptEditorAsync()
    {
        if (ActiveView is { } view)
        {
            await new ScriptDialog(view.Session).ShowDialog(this).ConfigureAwait(true);
            view.Session.SaveScript();
            view.FocusInput();
        }
    }

    private void ShowHelp()
    {
        char commandChar = ActiveView?.Session.Engine.CommandChar ?? '#';
        new HelpWindow(commandChar).Show(this);
    }

    private void ApplySettingsToViews()
    {
        ApplyChromeSettings();
        foreach (SessionView view in Views)
        {
            view.ApplySettings();
        }
    }

    private void ChangeFontSize(int direction)
    {
        _settings.FontSize = direction == 0 ? 13 : Math.Clamp(_settings.FontSize + direction, 6, 72);
        ApplySettingsToViews();
        SaveSettings();
    }

    private async Task CopyAsync()
    {
        if (ActiveView is not { } view)
        {
            return;
        }
        if (view.HasOutputSelection)
        {
            await view.CopySelectionAsync().ConfigureAwait(true);
        }
        else if (FocusManager?.GetFocusedElement() is TextBox box)
        {
            box.Copy();
        }
    }

    private async Task PasteAsync()
    {
        if (ActiveView is not { } view)
        {
            return;
        }
        if (FocusManager?.GetFocusedElement() is not TextBox)
        {
            view.FocusInput();
        }
        if (FocusManager?.GetFocusedElement() is TextBox box)
        {
            box.Paste();
        }
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private static readonly FilePickerFileType ScriptFileType = new("TinTin scripts") { Patterns = ["*.tin", "*.txt", "*.ws"] };
    private static readonly FilePickerFileType LogFileType = new("Log files") { Patterns = ["*.log", "*.txt"] };

    private async Task LoadScriptAsync()
    {
        if (ActiveView is not { } view)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Script",
            AllowMultiple = false,
            FileTypeFilter = [ScriptFileType, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
        {
            if (await MessageDialog.ConfirmAsync(this, "Load Script",
                $"Load {System.IO.Path.GetFileName(path)} into this session?\n\nIts aliases, actions and other items are added to the ones already defined. Only load scripts you trust: a script can send commands to the MUD when its triggers fire.",
                "Load").ConfigureAwait(true))
            {
                view.Session.RunCommands($"{view.Session.Engine.CommandChar}read {{{path}}}");
            }
        }
    }

    private async Task SaveScriptAsAsync()
    {
        if (ActiveView is not { } view)
        {
            return;
        }
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Script As",
            SuggestedFileName = SanitizeFileName(SessionTitle(view.Session)) + ".tin",
            DefaultExtension = "tin",
            FileTypeChoices = [ScriptFileType],
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is { } path)
        {
            view.Session.RunCommands($"{view.Session.Engine.CommandChar}write {{{path}}}");
        }
    }

    private async Task StartLogAsync()
    {
        if (ActiveView is not { } view)
        {
            return;
        }
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Log to File (appends if the file exists)",
            SuggestedFileName = $"{SanitizeFileName(SessionTitle(view.Session))}-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log",
            DefaultExtension = "log",
            FileTypeChoices = [LogFileType],
            ShowOverwritePrompt = false,
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is { } path)
        {
            view.Session.RunCommands($"{view.Session.Engine.CommandChar}log {{{path}}}");
            UpdateStatus();
        }
    }

    private void StopLog()
    {
        if (ActiveView is { Session.IsLogging: true } view)
        {
            view.Session.RunCommands($"{view.Session.Engine.CommandChar}log off");
            UpdateStatus();
        }
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        string cleaned = new([.. name.Select(c => invalid.Contains(c) || c is '{' or '}' ? '_' : c)]);
        return string.IsNullOrWhiteSpace(cleaned) ? "session" : cleaned.Trim();
    }

    // ---- Keyboard --------------------------------------------------------------------

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ActiveView is not { } view || !ReferenceEquals(e.Source is Visual visual ? TopLevel.GetTopLevel(visual) : null, this))
        {
            return;
        }

        // Copy the output selection when there is one; otherwise leave Ctrl+C to the text box.
        if (e.Key == Key.C && e.KeyModifiers == CommandKey && view.HasOutputSelection)
        {
            _ = view.CopySelectionAsync();
            e.Handled = true;
            return;
        }

        // A macro bound to a key wins over a menu shortcut on the same key.
        if (KeyNames.IsMacroCandidate(e.Key, e.KeyModifiers) && view.Session.TryRunMacro(KeyNames.Describe(e.Key, e.KeyModifiers)))
        {
            e.Handled = true;
        }
    }

    /// <summary>Typing anywhere in the window goes to the command line.</summary>
    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (ActiveView is { } view && FocusManager?.GetFocusedElement() is not TextBox && !string.IsNullOrEmpty(e.Text))
        {
            view.FocusInput();
        }
    }

    // ---- Status bar ------------------------------------------------------------------

    private void OnBell()
    {
        _bellUntil = DateTime.UtcNow.AddSeconds(1.5);
        UpdateStatus();
        if (_settings.AudibleBell && OperatingSystem.IsWindows())
        {
            _ = Task.Run(static () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    Console.Beep(800, 120);
                }
            });
        }
    }

    private void UpdateStatus()
    {
        ClockText.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        MudSession? session = ActiveView?.Session;
        Title = session is null ? "Comet" : $"{SessionTitle(session)} - Comet";

        if (_connectButton is not null && _disconnectButton is not null && _stopLogButton is not null)
        {
            _connectButton.IsEnabled = session?.State == SessionState.Disconnected;
            _disconnectButton.IsEnabled = session is not null && session.State != SessionState.Disconnected;
            _stopLogButton.IsVisible = session?.IsLogging == true;
        }

        if (session is null)
        {
            StatusText.Text = "No session";
            Panel1.IsVisible = Panel2.IsVisible = Panel3.IsVisible = TickPane.IsVisible = LogPane.IsVisible = false;
            return;
        }

        string status;
        if (DateTime.UtcNow < _bellUntil)
        {
            status = "Bell";
        }
        else
        {
            string address = $"{session.Profile.Host}:{session.Profile.Port.ToString(CultureInfo.InvariantCulture)}";
            status = session.State switch
            {
                SessionState.Connected => $"Connected to {address}",
                SessionState.Connecting => $"Connecting to {address}...",
                _ => "Not connected",
            };
            if (session.State == SessionState.Connected)
            {
                if (session.ConnectedSince is { } since)
                {
                    TimeSpan elapsed = DateTimeOffset.UtcNow - since;
                    status += $"  |  {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
                }
                if (session.IsEncrypted)
                {
                    status += "  |  TLS";
                }
                if (session.IsCompressed)
                {
                    status += "  |  Compressed";
                }
                if (session.IsPasswordMode)
                {
                    status += "  |  Input hidden";
                }
            }
        }
        StatusText.Text = status;

        SetPanel(Panel1, Panel1Text, session.Panels[0]);
        SetPanel(Panel2, Panel2Text, session.Panels[1]);
        SetPanel(Panel3, Panel3Text, session.Panels[2]);
        int? tick = session.Engine.SecondsToTick;
        TickPane.IsVisible = tick is not null;
        TickText.Text = tick is { } seconds ? $"Tick {seconds.ToString(CultureInfo.InvariantCulture)}s" : string.Empty;
        LogPane.IsVisible = session.IsLogging;
        ToolTip.SetTip(LogPane, session.LogPath);
    }

    private static void SetPanel(Border pane, TextBlock text, string value)
    {
        pane.IsVisible = value.Length > 0;
        text.Text = value;
    }

    // ---- Settings and shutdown -------------------------------------------------------

    private void SaveSettings()
    {
        try
        {
            SettingsStore.Save(_paths, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WarningText.Text = $"Settings could not be saved: {ex.Message}";
            WarningBar.IsVisible = true;
        }
    }

    /// <summary>Moves the window back if the saved position is on a monitor that is gone.</summary>
    private void EnsureOnScreen()
    {
        if (WindowState != WindowState.Normal || Screens.All.Count == 0)
        {
            return;
        }
        var frame = new PixelRect(Position, PixelSize.FromSize(ClientSize, RenderScaling));
        bool visible = Screens.All.Any(screen => screen.WorkingArea.Intersects(frame));
        if (!visible && Screens.Primary is { } primary)
        {
            Position = primary.WorkingArea.TopLeft + new PixelPoint(40, 40);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && _settings.ConfirmCloseWhenConnected)
        {
            int connected = Views.Count(static view => view.Session.State == SessionState.Connected);
            if (connected > 0)
            {
                e.Cancel = true;
                string message = connected == 1
                    ? "One session is still connected. Disconnect and exit?"
                    : $"{connected.ToString(CultureInfo.InvariantCulture)} sessions are still connected. Disconnect them and exit?";
                if (await MessageDialog.ConfirmAsync(this, "Exit Comet", message, "Disconnect and Exit").ConfigureAwait(true))
                {
                    _closeConfirmed = true;
                    Close();
                }
                return;
            }
        }

        _statusTimer.Stop();
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = ClientSize.Width;
            _settings.WindowHeight = ClientSize.Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        SaveSettings();

        foreach (SessionView view in Views.ToList())
        {
            view.Detach();
            // Saves the script synchronously, then closes the connection in the background.
            _ = view.Session.DisposeAsync().AsTask();
        }
    }
}
