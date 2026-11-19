using System.Globalization;
using System.Text;
using Comet.Core.Configuration;
using Comet.Core.Input;
using Comet.Core.Net;
using Comet.Core.Scripting;
using Comet.Core.Text;

namespace Comet.Core.Sessions;

public enum SessionState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// One MUD session: the connection, its scrollback, its script engine and its log.
/// </summary>
/// <remarks>
/// Everything public is called on the thread that created the session (the UI thread).
/// Network callbacks are marshalled back to that thread through the captured
/// <see cref="SynchronizationContext"/>, so the buffer and engine are single-threaded.
/// </remarks>
public sealed class MudSession : IScriptHost, IAsyncDisposable
{
    public const int PanelCount = 3;

    private static readonly TextStyle EchoStyle = new(TextColor.Palette(11), TextColor.Default, TextAttributes.None);
    private static readonly TimeSpan TimerInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ScriptSaveDelay = TimeSpan.FromSeconds(2);
    private static readonly int[] ReconnectDelaysSeconds = [5, 10, 30, 60];

    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly SynchronizationContext? _context;
    private readonly TimeProvider _time;
    private readonly AnsiParser _parser = new();
    private readonly ITimer _timer;
    private readonly string[] _panels = new string[PanelCount];

    private MudConnection? _connection;
    private ConnectionSink? _sink;
    private CancellationTokenSource? _connectCancellation;
    private StreamWriter? _log;
    private bool _openLine;
    private string? _promptSource;
    private TextLine? _promptDisplay;
    private bool _outputDirty;
    private string _savedScript = string.Empty;
    private DateTimeOffset? _scriptDirtySince;
    private int _reconnectAttempt;
    private DateTimeOffset? _reconnectAt;
    private bool _userDisconnected;
    private int _columns = 80;
    private int _rows = 24;
    private bool _disposed;

    public MudSession(SessionProfile profile, AppSettings settings, AppPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        Profile = profile;
        _settings = settings;
        _paths = paths;
        _context = SynchronizationContext.Current;
        _time = timeProvider ?? TimeProvider.System;
        Buffer = new ScrollbackBuffer(settings.ScrollbackLines);
        Engine = new ScriptEngine(this, _time);
        Array.Fill(_panels, string.Empty);

        _parser.LineCompleted += OnLineCompleted;
        _parser.Bell += () => BellRang?.Invoke();

        LoadProfileScript();
        Engine.ItemsChanged += () => _scriptDirtySince ??= _time.GetUtcNow();
        _timer = _time.CreateTimer(_ => Post(OnTimer), null, TimerInterval, TimerInterval);
    }

    public SessionProfile Profile { get; private set; }
    public ScrollbackBuffer Buffer { get; }
    public ScriptEngine Engine { get; }
    public CommandHistory History { get; } = new();
    public TabCompleter Completer { get; } = new();
    public SessionState State { get; private set; }

    /// <summary>True while the server is echoing, which MUDs do for password prompts.</summary>
    public bool IsPasswordMode { get; private set; }

    public bool IsLogging => _log is not null;
    public string? LogPath { get; private set; }
    public DateTimeOffset? ConnectedSince { get; private set; }
    public bool IsCompressed => _connection?.IsCompressed ?? false;
    public bool IsEncrypted => _connection?.IsEncrypted ?? false;

    public IReadOnlyList<string> Panels => _panels;

    /// <summary>Connection state, password mode, logging or panel text changed.</summary>
    public event Action? StatusChanged;

    /// <summary>The scrollback changed. Raised at most once per batch of work.</summary>
    public event Action? OutputChanged;

    public event Action? BellRang;

    // ---- Connection ------------------------------------------------------------------

    public async Task ConnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != SessionState.Disconnected)
        {
            return;
        }
        if (Profile.Validate() is { } problem)
        {
            PrintError("#" + problem);
            FlushOutput();
            return;
        }

        _userDisconnected = false;
        _reconnectAt = null;
        string host = Profile.Host.Trim();
        var options = new ConnectionOptions
        {
            Host = host,
            Port = Profile.Port,
            UseTls = Profile.UseTls,
            AcceptInvalidCertificate = Profile.AcceptInvalidCertificate,
            EncodingName = Profile.Encoding,
            ClientVersion = typeof(MudSession).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
        };

        var sink = new ConnectionSink(this);
        var connection = new MudConnection(options, sink);
        var cancellation = new CancellationTokenSource();
        _sink = sink;
        _connection = connection;
        _connectCancellation = cancellation;
        SetState(SessionState.Connecting);
        PrintInfo($"#Connecting to {host} port {Profile.Port.ToString(CultureInfo.InvariantCulture)}{(Profile.UseTls ? " (TLS)" : string.Empty)}...");
        FlushOutput();

        try
        {
            await connection.SetWindowSizeAsync(_columns, _rows).ConfigureAwait(true);
            await connection.ConnectAsync(cancellation.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException
            or OperationCanceledException or System.Security.Authentication.AuthenticationException or ArgumentException)
        {
            if (!ReferenceEquals(_connection, connection))
            {
                return;
            }
            _connection = null;
            _sink = null;
            await connection.DisposeAsync().ConfigureAwait(true);
            if (ex is OperationCanceledException)
            {
                PrintInfo("#Connection attempt cancelled.");
            }
            else
            {
                PrintError($"#Could not connect: {DescribeError(ex)}");
                ScheduleReconnect();
            }
            SetState(SessionState.Disconnected);
            FlushOutput();
            return;
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_connectCancellation, cancellation))
            {
                _connectCancellation = null;
            }
        }

        if (!ReferenceEquals(_connection, connection))
        {
            return;
        }
        _reconnectAttempt = 0;
        ConnectedSince = _time.GetUtcNow();
        SetState(SessionState.Connected);
        PrintInfo(Profile.UseTls && Profile.AcceptInvalidCertificate
            ? "#CONNECTED. The connection is encrypted, but the server's certificate was not verified."
            : "#CONNECTED.");
        FlushOutput();
    }

    /// <summary>Closes the connection, or cancels one being made or a pending reconnect.</summary>
    public void Disconnect()
    {
        _userDisconnected = true;
        if (_reconnectAt is not null)
        {
            _reconnectAt = null;
            PrintInfo("#Reconnect cancelled.");
            FlushOutput();
        }
        _connectCancellation?.Cancel();
        MudConnection? connection = _connection;
        if (connection is not null && State == SessionState.Connected)
        {
            // The read loop reports the close, which prints the message and resets state.
            _ = connection.DisposeAsync().AsTask();
        }
    }

    /// <summary>Replaces the profile, for example after it is edited. Takes effect on the next connect.</summary>
    public void UpdateProfile(SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;
        StatusChanged?.Invoke();
    }

    /// <summary>Tells the server the size of the output view in character cells.</summary>
    public void SetWindowSize(int columns, int rows)
    {
        _columns = columns;
        _rows = rows;
        _ = _connection?.SetWindowSizeAsync(columns, rows);
    }

    /// <summary>Applies settings that can change while the session is open.</summary>
    public void ApplySettings() => Buffer.Resize(_settings.ScrollbackLines);

    // ---- Input -----------------------------------------------------------------------

    /// <summary>Handles a line typed by the user.</summary>
    public void SubmitInput(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (IsPasswordMode)
        {
            // Never run a password through the interpreter, the history or the log.
            SendRaw(line, echo: false);
        }
        else
        {
            History.Add(line);
            Engine.ExecuteUserInput(line);
        }
        FlushOutput();
    }

    /// <summary>Runs commands as if typed, without adding them to the history.</summary>
    public void RunCommands(string commands)
    {
        Engine.Execute(commands);
        FlushOutput();
    }

    public bool TryRunMacro(string key)
    {
        if (IsPasswordMode || !Engine.TryRunMacro(key))
        {
            return false;
        }
        FlushOutput();
        return true;
    }

    // ---- Script file -----------------------------------------------------------------

    public string ScriptPath => _paths.ScriptFileFor(Profile);

    private void LoadProfileScript()
    {
        string path = ScriptPath;
        try
        {
            if (File.Exists(path))
            {
                Engine.Load(File.ReadAllText(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PrintError($"#Could not read the script {path}: {ex.Message}");
        }
        _savedScript = Engine.Save();
    }

    /// <summary>Writes the session's script items to its script file if they changed.</summary>
    public void SaveScript()
    {
        _scriptDirtySince = null;
        string script = Engine.Save();
        if (script == _savedScript)
        {
            return;
        }
        try
        {
            AtomicFile.WriteAllText(ScriptPath, script);
            _savedScript = script;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PrintError($"#Could not save the script {ScriptPath}: {ex.Message}");
            FlushOutput();
        }
    }

    // ---- IScriptHost -----------------------------------------------------------------

    void IScriptHost.Send(string text) => SendRaw(text, echo: _settings.EchoCommands);

    void IScriptHost.Print(TextLine line) => AppendLocal(line);

    void IScriptHost.Bell() => BellRang?.Invoke();

    void IScriptHost.ClearOutput()
    {
        Buffer.Clear();
        _openLine = false;
        _outputDirty = true;
    }

    void IScriptHost.Connect(string name, string host, int port, bool useTls)
    {
        if (State != SessionState.Disconnected)
        {
            PrintError("#Already connected. Use #zap first.");
            return;
        }
        SessionProfile profile = Profile.Clone();
        profile.Name = name;
        profile.Host = host;
        profile.Port = port;
        profile.UseTls = useTls;
        Profile = profile;
        _ = ConnectAsync();
    }

    void IScriptHost.Disconnect()
    {
        if (State == SessionState.Disconnected && _reconnectAt is null)
        {
            PrintError("#Not connected.");
            return;
        }
        Disconnect();
    }

    bool IScriptHost.SendGmcp(string package, string json)
    {
        MudConnection? connection = _connection;
        if (connection is null || State != SessionState.Connected)
        {
            return false;
        }
        Task<bool> send = connection.SendGmcpAsync(package, json);
        return !send.IsCompleted || send.Result;
    }

    void IScriptHost.SetPanel(int index, string text)
    {
        if (index is >= 1 and <= PanelCount)
        {
            _panels[index - 1] = text;
            StatusChanged?.Invoke();
        }
    }

    string IScriptHost.ResolvePath(string path) => Path.GetFullPath(path, _paths.ScriptsDirectory);

    public string? StartLog(string path, out string fullPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        fullPath = Path.GetFullPath(path, _paths.LogsDirectory);
        try
        {
            StopLog();
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _log = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
            LogPath = fullPath;
            _log.WriteLine($"--- Log opened {_time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} ---");
            StatusChanged?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ex.Message;
        }
    }

    public void StopLog()
    {
        StreamWriter? log = _log;
        if (log is null)
        {
            return;
        }
        _log = null;
        LogPath = null;
        try
        {
            log.WriteLine($"--- Log closed {_time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} ---");
            log.Dispose();
        }
        catch (IOException)
        {
            // The log is being abandoned either way.
        }
        StatusChanged?.Invoke();
    }

    // ---- Output ----------------------------------------------------------------------

    private void SendRaw(string text, bool echo)
    {
        if (_openLine)
        {
            // The prompt the user answered. Let actions see it once, then close the line.
            TextLine prompt = Buffer[Buffer.Count - 1];
            if (_promptSource is null && !IsPasswordMode && prompt.Length > 0)
            {
                Engine.ProcessLine(prompt);
            }
            TextLine closed = echo && !IsPasswordMode ? prompt.Append(TextLine.Styled(text, EchoStyle)) : prompt;
            Buffer.ReplaceLast(closed);
            WriteLog(closed.Text);
            _parser.DiscardPartialLine();
            _openLine = false;
            _promptSource = null;
            _promptDisplay = null;
            _outputDirty = true;
        }
        else if (echo && !IsPasswordMode)
        {
            AppendClosedLine(TextLine.Styled(text, EchoStyle));
        }

        MudConnection? connection = _connection;
        if (connection is null || State != SessionState.Connected)
        {
            PrintError("#Not connected. Use File > Connect, or #session {name} {host port}.");
            return;
        }
        _ = connection.SendLineAsync(text);
    }

    private void PrintInfo(string message) => AppendLocal(TextLine.Styled(message, ScriptEngine.InfoStyle));

    private void PrintError(string message) => AppendLocal(TextLine.Styled(message, ScriptEngine.ErrorStyle));

    /// <summary>Adds a client-generated line above any prompt that is still open.</summary>
    private void AppendLocal(TextLine line)
    {
        if (_openLine)
        {
            TextLine prompt = Buffer[Buffer.Count - 1];
            Buffer.ReplaceLast(line);
            WriteLog(line.Text);
            Buffer.Append(prompt);
            _outputDirty = true;
        }
        else
        {
            AppendClosedLine(line);
        }
    }

    private void AppendClosedLine(TextLine line)
    {
        Buffer.Append(line);
        WriteLog(line.Text);
        _outputDirty = true;
    }

    private void WriteLog(string text)
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            _log.WriteLine(text);
        }
        catch (IOException ex)
        {
            StopLog();
            PrintError($"#Logging stopped: {ex.Message}");
        }
    }

    private void OnLineCompleted(TextLine line)
    {
        TextLine? display;
        if (_promptSource is not null && line.Text == _promptSource)
        {
            // Already run through the triggers when the server marked it as a prompt.
            display = _promptDisplay;
        }
        else
        {
            display = Engine.ProcessLine(line);
        }
        _promptSource = null;
        _promptDisplay = null;

        if (_openLine)
        {
            _openLine = false;
            if (display is null)
            {
                Buffer.RemoveLast();
            }
            else
            {
                Buffer.ReplaceLast(display);
                WriteLog(display.Text);
            }
            _outputDirty = true;
        }
        else if (display is not null)
        {
            AppendClosedLine(display);
        }

        if (display is not null)
        {
            Completer.Observe(display.Text);
        }
        Engine.RunPendingActions();
    }

    private void ShowPartialLine(bool isPrompt)
    {
        if (!_parser.HasPartialLine)
        {
            return;
        }
        TextLine partial = _parser.PartialLine;
        TextLine display = partial;
        if (isPrompt && _promptSource != partial.Text)
        {
            TextLine? processed = Engine.ProcessLine(partial);
            _promptSource = partial.Text;
            _promptDisplay = processed;
            display = processed ?? TextLine.Empty;
        }
        else if (_promptSource == partial.Text)
        {
            display = _promptDisplay ?? TextLine.Empty;
        }

        if (_openLine)
        {
            Buffer.ReplaceLast(display);
        }
        else
        {
            Buffer.Append(display);
            _openLine = true;
        }
        _outputDirty = true;
        if (isPrompt)
        {
            Engine.RunPendingActions();
        }
    }

    private void FlushOutput()
    {
        if (_outputDirty)
        {
            _outputDirty = false;
            OutputChanged?.Invoke();
        }
    }

    // ---- Connection events (UI thread) -------------------------------------------------

    private void HandleEvents(ConnectionSink sink, List<ConnectionEvent> events)
    {
        if (!ReferenceEquals(sink, _sink) || _disposed)
        {
            return;
        }

        foreach (ConnectionEvent e in events)
        {
            switch (e.Kind)
            {
                case ConnectionEventKind.Text:
                    _parser.Feed(e.Text);
                    break;
                case ConnectionEventKind.Prompt:
                    ShowPartialLine(isPrompt: true);
                    break;
                case ConnectionEventKind.Echo:
                    IsPasswordMode = e.Flag;
                    StatusChanged?.Invoke();
                    break;
                case ConnectionEventKind.Gmcp:
                    Engine.SetVariable("gmcp." + e.Text, e.Extra);
                    break;
                case ConnectionEventKind.Closed:
                    OnConnectionClosed(e.Error);
                    break;
            }
        }
        ShowPartialLine(isPrompt: false);
        FlushOutput();
    }

    private void OnConnectionClosed(Exception? error)
    {
        MudConnection? connection = _connection;
        _connection = null;
        _sink = null;
        if (connection is not null)
        {
            _ = connection.DisposeAsync().AsTask();
        }

        _parser.FlushPartialLine();
        _openLine = false;
        IsPasswordMode = false;
        ConnectedSince = null;
        if (error is null)
        {
            PrintInfo("#SESSION CLOSED.");
        }
        else
        {
            PrintError($"#SESSION CLOSED: {DescribeError(error)}");
        }
        SetState(SessionState.Disconnected);
        if (!_userDisconnected)
        {
            ScheduleReconnect();
        }
    }

    private void ScheduleReconnect()
    {
        if (!Profile.AutoReconnect || _userDisconnected || _disposed)
        {
            return;
        }
        int seconds = ReconnectDelaysSeconds[Math.Min(_reconnectAttempt, ReconnectDelaysSeconds.Length - 1)];
        _reconnectAttempt++;
        _reconnectAt = _time.GetUtcNow() + TimeSpan.FromSeconds(seconds);
        PrintInfo($"#Reconnecting in {seconds.ToString(CultureInfo.InvariantCulture)} seconds. Disconnect to cancel.");
    }

    private static string DescribeError(Exception error) => error switch
    {
        System.Net.Sockets.SocketException socket => socket.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData => "the address could not be found.",
            System.Net.Sockets.SocketError.ConnectionRefused => "the server refused the connection. Check the port.",
            System.Net.Sockets.SocketError.TimedOut => "the server did not answer.",
            System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable => "the network is unreachable.",
            _ => socket.Message,
        },
        System.Security.Authentication.AuthenticationException => "the server's TLS certificate could not be verified. " + error.Message,
        IOException { InnerException: System.Net.Sockets.SocketException inner } => DescribeError(inner),
        _ => error.Message,
    };

    private void SetState(SessionState state)
    {
        if (State != state)
        {
            State = state;
            StatusChanged?.Invoke();
        }
    }

    private void OnTimer()
    {
        if (_disposed)
        {
            return;
        }
        Engine.Advance();

        if (_reconnectAt is { } due && _time.GetUtcNow() >= due && State == SessionState.Disconnected)
        {
            _reconnectAt = null;
            _ = ConnectAsync();
        }
        if (_scriptDirtySince is { } since && _time.GetUtcNow() - since >= ScriptSaveDelay)
        {
            SaveScript();
        }
        try
        {
            _log?.Flush();
        }
        catch (IOException ex)
        {
            StopLog();
            PrintError($"#Logging stopped: {ex.Message}");
        }
        FlushOutput();
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(static state => ((Action)state!)(), action);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        SaveScript();
        _disposed = true;
        await _timer.DisposeAsync().ConfigureAwait(false);
        _connectCancellation?.Cancel();
        StopLog();
        MudConnection? connection = _connection;
        _connection = null;
        _sink = null;
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private enum ConnectionEventKind { Text, Prompt, Echo, Gmcp, Closed }

    private readonly record struct ConnectionEvent(ConnectionEventKind Kind, string Text = "", string Extra = "", bool Flag = false, Exception? Error = null);

    /// <summary>Collects one connection's callbacks on its read thread and hands them over in batches.</summary>
    private sealed class ConnectionSink(MudSession session) : IMudConnectionEvents
    {
        private List<ConnectionEvent> _pending = [];

        public void OnText(string text) => _pending.Add(new ConnectionEvent(ConnectionEventKind.Text, text));
        public void OnPromptMarker() => _pending.Add(new ConnectionEvent(ConnectionEventKind.Prompt));
        public void OnServerEchoChanged(bool serverEchoes) => _pending.Add(new ConnectionEvent(ConnectionEventKind.Echo, Flag: serverEchoes));
        public void OnGmcp(string package, string json) => _pending.Add(new ConnectionEvent(ConnectionEventKind.Gmcp, package, json));

        public void OnClosed(Exception? error)
        {
            _pending.Add(new ConnectionEvent(ConnectionEventKind.Closed, Error: error));
            OnBatchCompleted();
        }

        public void OnBatchCompleted()
        {
            if (_pending.Count == 0)
            {
                return;
            }
            List<ConnectionEvent> batch = _pending;
            _pending = [];
            session.Post(() => session.HandleEvents(this, batch));
        }
    }
}
