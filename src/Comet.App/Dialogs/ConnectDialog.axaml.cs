using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Comet.Core.Configuration;

namespace Comet.App.Dialogs;

/// <summary>
/// The connection manager: saved MUD profiles on the left, the selected one's details on the
/// right. Closes with the profile to connect to, or null.
/// </summary>
public sealed partial class ConnectDialog : Window
{
    private static readonly (string Label, string Name)[] Encodings =
    [
        ("UTF-8 (most MUDs)", "utf-8"),
        ("Latin-1 (ISO-8859-1)", "iso-8859-1"),
        ("Western European (Windows-1252)", "windows-1252"),
        ("DOS box drawing (CP437)", "ibm437"),
        ("Central European (ISO-8859-2)", "iso-8859-2"),
        ("Cyrillic (KOI8-R)", "koi8-r"),
        ("Cyrillic (Windows-1251)", "windows-1251"),
        ("Chinese Simplified (GBK)", "gbk"),
        ("Chinese Traditional (Big5)", "big5"),
        ("Japanese (Shift-JIS)", "shift_jis"),
        ("Korean (EUC-KR)", "euc-kr"),
    ];

    private readonly AppSettings _settings;
    private readonly Action _save;
    private SessionProfile? _current;
    private bool _loading;

    public ConnectDialog() : this(new AppSettings(), static () => { })
    {
    }

    public ConnectDialog(AppSettings settings, Action save)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(save);
        InitializeComponent();
        _settings = settings;
        _save = save;

        EncodingBox.ItemsSource = Encodings.Select(static e => e.Label).ToList();

        ProfileList.SelectionChanged += (_, _) => LoadSelected();
        ProfileList.DoubleTapped += (_, _) => Connect();
        NewButton.Click += (_, _) => AddProfile();
        DeleteButton.Click += async (_, _) => await DeleteSelectedAsync().ConfigureAwait(true);
        ConnectButton.Click += (_, _) => Connect();
        CloseButton.Click += (_, _) => Close(null);

        NameBox.TextChanged += (_, _) => Apply(p => p.Name = (NameBox.Text ?? string.Empty).Trim());
        HostBox.TextChanged += (_, _) => Apply(p => p.Host = (HostBox.Text ?? string.Empty).Trim());
        PortBox.TextChanged += (_, _) => Apply(p =>
            p.Port = int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0);
        TlsBox.IsCheckedChanged += (_, _) =>
        {
            Apply(p => p.UseTls = TlsBox.IsChecked == true);
            InvalidCertificateBox.IsEnabled = TlsBox.IsChecked == true;
        };
        InvalidCertificateBox.IsCheckedChanged += (_, _) => Apply(p => p.AcceptInvalidCertificate = InvalidCertificateBox.IsChecked == true);
        ReconnectBox.IsCheckedChanged += (_, _) => Apply(p => p.AutoReconnect = ReconnectBox.IsChecked == true);
        NotesBox.TextChanged += (_, _) => Apply(p => p.Notes = NotesBox.Text ?? string.Empty);
        EncodingBox.SelectionChanged += (_, _) => Apply(p =>
        {
            if (EncodingBox.SelectedIndex >= 0)
            {
                p.Encoding = Encodings[EncodingBox.SelectedIndex].Name;
            }
        });

        Closing += (_, _) =>
        {
            // A profile that never got an address is an abandoned "New", not something to keep.
            _settings.Profiles.RemoveAll(static p => string.IsNullOrWhiteSpace(p.Host));
            _save();
        };

        RefreshList(select: _settings.Profiles.FirstOrDefault());
        if (_settings.Profiles.Count == 0)
        {
            AddProfile();
        }
        Opened += (_, _) => (string.IsNullOrEmpty(HostBox.Text) ? HostBox : (Control)ProfileList).Focus();
    }

    private static string Describe(SessionProfile profile)
    {
        string name = string.IsNullOrWhiteSpace(profile.Name) ? (string.IsNullOrWhiteSpace(profile.Host) ? "New connection" : profile.Host) : profile.Name;
        return string.IsNullOrWhiteSpace(profile.Host)
            ? name
            : $"{name}\n{profile.Host}:{profile.Port.ToString(CultureInfo.InvariantCulture)}{(profile.UseTls ? " (TLS)" : string.Empty)}";
    }

    private void RefreshList(SessionProfile? select)
    {
        _loading = true;
        ProfileList.ItemsSource = _settings.Profiles.Select(static p => new ListBoxItem { Tag = p, Content = Describe(p) }).ToList();
        ProfileList.SelectedIndex = select is null ? -1 : _settings.Profiles.IndexOf(select);
        _loading = false;
        NoProfiles.IsVisible = _settings.Profiles.Count == 0;
        LoadSelected();
    }

    private void LoadSelected()
    {
        if (_loading)
        {
            return;
        }
        _current = (ProfileList.SelectedItem as ListBoxItem)?.Tag as SessionProfile;
        Form.IsEnabled = _current is not null;
        DeleteButton.IsEnabled = _current is not null;
        ConnectButton.IsEnabled = _current is not null;
        ErrorText.Text = string.Empty;

        _loading = true;
        SessionProfile shown = _current ?? new SessionProfile();
        NameBox.Text = shown.Name;
        HostBox.Text = shown.Host;
        PortBox.Text = shown.Port > 0 ? shown.Port.ToString(CultureInfo.InvariantCulture) : string.Empty;
        TlsBox.IsChecked = shown.UseTls;
        InvalidCertificateBox.IsChecked = shown.AcceptInvalidCertificate;
        InvalidCertificateBox.IsEnabled = shown.UseTls;
        ReconnectBox.IsChecked = shown.AutoReconnect;
        NotesBox.Text = shown.Notes;
        int encoding = Array.FindIndex(Encodings, e => e.Name.Equals(shown.Encoding, StringComparison.OrdinalIgnoreCase));
        EncodingBox.SelectedIndex = encoding < 0 ? 0 : encoding;
        _loading = false;
    }

    /// <summary>Writes a field edit to the selected profile and refreshes its label in the list.</summary>
    private void Apply(Action<SessionProfile> change)
    {
        if (_loading || _current is null)
        {
            return;
        }
        change(_current);
        ErrorText.Text = string.Empty;
        if (ProfileList.SelectedItem is ListBoxItem item)
        {
            item.Content = Describe(_current);
        }
    }

    private void AddProfile()
    {
        var profile = new SessionProfile();
        _settings.Profiles.Add(profile);
        RefreshList(profile);
        NameBox.Focus();
    }

    private async Task DeleteSelectedAsync()
    {
        if (_current is not { } profile)
        {
            return;
        }
        bool blank = string.IsNullOrWhiteSpace(profile.Host) && string.IsNullOrWhiteSpace(profile.Name);
        if (!blank && !await MessageDialog.ConfirmAsync(this, "Delete connection",
            $"Delete the saved connection \"{Describe(profile).Split('\n')[0]}\"?\n\nIts script file is kept on disk.", "Delete").ConfigureAwait(true))
        {
            return;
        }
        int index = _settings.Profiles.IndexOf(profile);
        _settings.Profiles.Remove(profile);
        RefreshList(_settings.Profiles.Count == 0 ? null : _settings.Profiles[Math.Min(index, _settings.Profiles.Count - 1)]);
        _save();
    }

    private void Connect()
    {
        if (_current is not { } profile)
        {
            return;
        }
        if (profile.Validate() is { } problem)
        {
            ErrorText.Text = problem;
            (string.IsNullOrWhiteSpace(profile.Host) || problem.Contains("address", StringComparison.OrdinalIgnoreCase) ? HostBox : PortBox).Focus();
            return;
        }
        Close(profile);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        // Enter in the multi-line notes box must add a line, not connect.
        if (e.Key == Key.Enter && NotesBox.IsFocused)
        {
            return;
        }
        base.OnKeyDown(e);
    }
}
