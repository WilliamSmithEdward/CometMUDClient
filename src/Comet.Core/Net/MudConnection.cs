using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Comet.Core.Net;

public sealed record ConnectionOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public bool UseTls { get; init; }

    /// <summary>
    /// Accept a TLS certificate that fails validation. Off by default; the connection is then
    /// encrypted but the server's identity is not verified.
    /// </summary>
    public bool AcceptInvalidCertificate { get; init; }

    /// <summary>A .NET encoding name such as utf-8, iso-8859-1, windows-1252 or ibm437.</summary>
    public string EncodingName { get; init; } = "utf-8";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Name reported to the server in terminal-type and GMCP negotiation.</summary>
    public string ClientName { get; init; } = "Comet";
    public string ClientVersion { get; init; } = "0.1.0";
}

/// <summary>Callbacks from a <see cref="MudConnection"/>. All are raised on its read loop, in stream order.</summary>
public interface IMudConnectionEvents
{
    void OnText(string text);

    /// <summary>The server marked the end of a prompt with GA or EOR.</summary>
    void OnPromptMarker();

    /// <summary>The server took over echoing (true, typically a password prompt) or handed it back.</summary>
    void OnServerEchoChanged(bool serverEchoes);

    void OnGmcp(string package, string json);

    /// <summary>One network read has been fully processed. A good point to refresh a view.</summary>
    void OnBatchCompleted();

    /// <summary>The connection ended. <paramref name="error"/> is null for an orderly close.</summary>
    void OnClosed(Exception? error);
}

/// <summary>
/// A telnet connection to a MUD: TCP or TLS transport, option negotiation, MCCP2
/// decompression and character decoding.
/// </summary>
public sealed class MudConnection : ITelnetHandler, IAsyncDisposable
{
    private const byte TerminalTypeIs = 0;
    private const byte TerminalTypeSend = 1;
    private const byte CharsetRequest = 1;
    private const byte CharsetAccepted = 2;
    private const byte CharsetRejected = 3;

    // MTTS bits: ANSI, UTF-8, 256 colors, truecolor, and TLS when in use.
    private const int MttsAnsi = 1;
    private const int MttsUtf8 = 4;
    private const int Mtts256Colors = 8;
    private const int MttsTrueColor = 256;
    private const int MttsTls = 2048;

    private readonly ConnectionOptions _options;
    private readonly IMudConnectionEvents _events;
    private readonly TelnetParser _parser = new();
    private readonly List<byte> _replies = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly bool[] _remoteEnabled = new bool[256];
    private readonly bool[] _localEnabled = new bool[256];
    private readonly Encoding _encoding;
    private readonly Decoder _decoder;
    private readonly object _sizeGate = new();

    private TcpClient? _client;
    private Stream? _transport;
    private Stream? _input;
    private Task? _readLoop;
    private char[] _chars = new char[8192];
    private int _terminalTypeStep;
    private int _columns = 80;
    private int _rows = 24;
    private bool _startCompression;
    private int _closed;
    private int _disposed;

    static MudConnection()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public MudConnection(ConnectionOptions options, IMudConnectionEvents events)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(events);
        _options = options;
        _events = events;
        _encoding = ResolveEncoding(options.EncodingName);
        _decoder = _encoding.GetDecoder();
    }

    public bool IsConnected => _transport is not null && Volatile.Read(ref _closed) == 0;

    /// <summary>True once the server has switched the inbound stream to MCCP2 compression.</summary>
    public bool IsCompressed { get; private set; }

    public bool IsEncrypted => _transport is SslStream;

    /// <summary>Returns the encoding for a name, falling back to UTF-8 when the name is unknown.</summary>
    public static Encoding ResolveEncoding(string name)
    {
        try
        {
            return Encoding.GetEncoding(name, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (ArgumentException)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            throw new InvalidOperationException("ConnectAsync may only be called once.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        timeout.CancelAfter(_options.ConnectTimeout);

        var client = new TcpClient { NoDelay = true };
        _client = client;
        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            Stream stream = client.GetStream();
            if (_options.UseTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                var sslOptions = new SslClientAuthenticationOptions { TargetHost = _options.Host };
                if (_options.AcceptInvalidCertificate)
                {
#pragma warning disable CA5359 // Opt-in per profile, off by default, and labelled as unverified in the UI.
                    sslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
                }
                await ssl.AuthenticateAsClientAsync(sslOptions, timeout.Token).ConfigureAwait(false);
                stream = ssl;
            }

            _transport = stream;
            _input = stream;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Connecting to {_options.Host}:{_options.Port} timed out.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    /// <summary>Sends one line of text followed by CR LF.</summary>
    public Task SendLineAsync(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        byte[] encoded = _encoding.GetBytes(line);
        var buffer = new List<byte>(encoded.Length + 2);
        TelnetParser.EscapeIac(encoded, buffer);
        buffer.Add((byte)'\r');
        buffer.Add((byte)'\n');
        return WriteAsync([.. buffer]);
    }

    /// <summary>Sends a GMCP message if the server enabled GMCP. Returns false otherwise.</summary>
    public async Task<bool> SendGmcpAsync(string package, string? json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        if (!_remoteEnabled[TelnetOption.Gmcp])
        {
            return false;
        }
        string text = string.IsNullOrEmpty(json) ? package : package + " " + json;
        var buffer = new List<byte>();
        AppendSubnegotiation(buffer, TelnetOption.Gmcp, Encoding.UTF8.GetBytes(text));
        await WriteAsync([.. buffer]).ConfigureAwait(false);
        return true;
    }

    /// <summary>Records the view size and reports it to the server when NAWS is active.</summary>
    public Task SetWindowSizeAsync(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, ushort.MaxValue);
        rows = Math.Clamp(rows, 1, ushort.MaxValue);
        lock (_sizeGate)
        {
            if (columns == _columns && rows == _rows)
            {
                return Task.CompletedTask;
            }
            _columns = columns;
            _rows = rows;
        }
        if (!IsConnected || !_localEnabled[TelnetOption.WindowSize])
        {
            return Task.CompletedTask;
        }
        var buffer = new List<byte>();
        AppendWindowSize(buffer);
        return WriteAsync([.. buffer]);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _shutdown.CancelAsync().ConfigureAwait(false);
        CloseTransport();
        if (_readLoop is not null)
        {
            await _readLoop.ConfigureAwait(false);
        }
        _shutdown.Dispose();
        _writeLock.Dispose();
    }

    private void CloseTransport()
    {
        try
        {
            _transport?.Dispose();
            _client?.Dispose();
        }
        catch (IOException)
        {
            // Closing a socket that is already broken has nothing left to report.
        }
    }

    private async Task WriteAsync(byte[] data)
    {
        Stream? transport = _transport;
        if (transport is null || Volatile.Read(ref _closed) != 0 || data.Length == 0)
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(_options.WriteTimeout);
            await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await transport.WriteAsync(data, timeout.Token).ConfigureAwait(false);
                await transport.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // A failed or stalled write means the link is gone. Closing the transport makes
            // the read loop report it once, with the real cause.
            CloseTransport();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                int read = await _input!.ReadAsync(buffer, _shutdown.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (IsCompressed && !ReferenceEquals(_input, _transport))
                    {
                        // The server ended the compressed stream; plain telnet may follow.
                        _input = _transport;
                        IsCompressed = false;
                        continue;
                    }
                    break;
                }

                int offset = 0;
                while (offset < read)
                {
                    offset += _parser.Process(buffer.AsSpan(offset, read - offset), this);
                    if (_startCompression)
                    {
                        _startCompression = false;
                        byte[] leftover = buffer.AsSpan(offset, read - offset).ToArray();
                        _input = new ZLibStream(new PrefixedStream(leftover, _transport!), CompressionMode.Decompress, leaveOpen: true);
                        IsCompressed = true;
                        offset = read;
                    }
                }

                if (_replies.Count > 0)
                {
                    byte[] replies = [.. _replies];
                    _replies.Clear();
                    await WriteAsync(replies).ConfigureAwait(false);
                }
                _events.OnBatchCompleted();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
        {
            error = _shutdown.IsCancellationRequested ? null : ex;
        }
        finally
        {
            Volatile.Write(ref _closed, 1);
            CloseTransport();
            _events.OnBatchCompleted();
            _events.OnClosed(error);
        }
    }

    void ITelnetHandler.OnData(ReadOnlySpan<byte> data)
    {
        int needed = _encoding.GetMaxCharCount(data.Length);
        if (_chars.Length < needed)
        {
            _chars = new char[needed];
        }
        int count = _decoder.GetChars(data, _chars, flush: false);
        if (count > 0)
        {
            _events.OnText(new string(_chars, 0, count));
        }
    }

    void ITelnetHandler.OnCommand(byte command)
    {
        if (command is TelnetCommand.GoAhead or TelnetCommand.EndOfRecord)
        {
            _events.OnPromptMarker();
        }
    }

    void ITelnetHandler.OnNegotiation(byte command, byte option)
    {
        switch (command)
        {
            case TelnetCommand.Will:
                bool accept = option is TelnetOption.Echo or TelnetOption.SuppressGoAhead or TelnetOption.EndOfRecord
                    or TelnetOption.Compress2 or TelnetOption.Gmcp or TelnetOption.Charset;
                // MCCP2 must not be re-enabled while a compressed stream is already running.
                if (option == TelnetOption.Compress2 && IsCompressed)
                {
                    accept = false;
                }
                if (accept != _remoteEnabled[option] || !accept)
                {
                    Reply(accept ? TelnetCommand.Do : TelnetCommand.Dont, option);
                }
                if (accept && !_remoteEnabled[option])
                {
                    _remoteEnabled[option] = true;
                    OnRemoteOptionEnabled(option);
                }
                break;

            case TelnetCommand.Wont:
                if (_remoteEnabled[option])
                {
                    _remoteEnabled[option] = false;
                    Reply(TelnetCommand.Dont, option);
                    if (option == TelnetOption.Echo)
                    {
                        _events.OnServerEchoChanged(false);
                    }
                }
                break;

            case TelnetCommand.Do:
                bool agree = option is TelnetOption.TerminalType or TelnetOption.WindowSize or TelnetOption.Charset;
                if (agree != _localEnabled[option] || !agree)
                {
                    Reply(agree ? TelnetCommand.Will : TelnetCommand.Wont, option);
                }
                if (agree && !_localEnabled[option])
                {
                    _localEnabled[option] = true;
                    if (option == TelnetOption.WindowSize)
                    {
                        AppendWindowSize(_replies);
                    }
                }
                break;

            case TelnetCommand.Dont:
                if (_localEnabled[option])
                {
                    _localEnabled[option] = false;
                    Reply(TelnetCommand.Wont, option);
                }
                break;
        }
    }

    bool ITelnetHandler.OnSubnegotiation(byte option, ReadOnlySpan<byte> payload)
    {
        switch (option)
        {
            case TelnetOption.Compress2 when _remoteEnabled[TelnetOption.Compress2] && !IsCompressed:
                _startCompression = true;
                return true;

            case TelnetOption.TerminalType when payload.Length >= 1 && payload[0] == TerminalTypeSend:
                SendTerminalType();
                break;

            case TelnetOption.Charset when payload.Length >= 2 && payload[0] == CharsetRequest:
                AnswerCharsetRequest(payload[1..]);
                break;

            case TelnetOption.Gmcp when _remoteEnabled[TelnetOption.Gmcp]:
                string message = Encoding.UTF8.GetString(payload);
                int space = message.IndexOf(' ', StringComparison.Ordinal);
                if (space < 0)
                {
                    _events.OnGmcp(message, string.Empty);
                }
                else
                {
                    _events.OnGmcp(message[..space], message[(space + 1)..].Trim());
                }
                break;
        }
        return false;
    }

    private void OnRemoteOptionEnabled(byte option)
    {
        switch (option)
        {
            case TelnetOption.Echo:
                _events.OnServerEchoChanged(true);
                break;
            case TelnetOption.Gmcp:
                string hello = $"Core.Hello {{\"client\":\"{JsonEscape(_options.ClientName)}\",\"version\":\"{JsonEscape(_options.ClientVersion)}\"}}";
                AppendSubnegotiation(_replies, TelnetOption.Gmcp, Encoding.UTF8.GetBytes(hello));
                break;
        }
    }

    private static string JsonEscape(string value) => System.Text.Json.JsonEncodedText.Encode(value).ToString();

    private void SendTerminalType()
    {
        // MTTS: client name, then terminal type, then the capability bitvector, which repeats.
        string value;
        switch (_terminalTypeStep)
        {
            case 0:
                value = _options.ClientName.ToUpperInvariant();
                break;
            case 1:
                value = "XTERM-TRUECOLOR";
                break;
            default:
                int bits = MttsAnsi | Mtts256Colors | MttsTrueColor;
                if (_encoding.CodePage == Encoding.UTF8.CodePage)
                {
                    bits |= MttsUtf8;
                }
                if (IsEncrypted)
                {
                    bits |= MttsTls;
                }
                value = "MTTS " + bits.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
        }
        _terminalTypeStep = Math.Min(_terminalTypeStep + 1, 2);

        byte[] payload = new byte[value.Length + 1];
        payload[0] = TerminalTypeIs;
        Encoding.ASCII.GetBytes(value, payload.AsSpan(1));
        AppendSubnegotiation(_replies, TelnetOption.TerminalType, payload);
    }

    private void AnswerCharsetRequest(ReadOnlySpan<byte> request)
    {
        // RFC 2066: REQUEST <sep> name <sep> name ... An optional "[TTABLE]" version prefix is not used by MUDs.
        string text = Encoding.ASCII.GetString(request);
        if (text.Length < 2)
        {
            AppendSubnegotiation(_replies, TelnetOption.Charset, [CharsetRejected]);
            return;
        }

        char separator = text[0];
        foreach (string offered in text[1..].Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Encoding candidate;
            try
            {
                candidate = Encoding.GetEncoding(offered);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (candidate.CodePage == _encoding.CodePage)
            {
                byte[] name = Encoding.ASCII.GetBytes(offered);
                byte[] payload = new byte[name.Length + 1];
                payload[0] = CharsetAccepted;
                name.CopyTo(payload, 1);
                AppendSubnegotiation(_replies, TelnetOption.Charset, payload);
                return;
            }
        }
        AppendSubnegotiation(_replies, TelnetOption.Charset, [CharsetRejected]);
    }

    private void AppendWindowSize(List<byte> destination)
    {
        int columns;
        int rows;
        lock (_sizeGate)
        {
            columns = _columns;
            rows = _rows;
        }
        AppendSubnegotiation(destination, TelnetOption.WindowSize,
            [(byte)(columns >> 8), (byte)columns, (byte)(rows >> 8), (byte)rows]);
    }

    private void Reply(byte command, byte option)
    {
        _replies.Add(TelnetCommand.Iac);
        _replies.Add(command);
        _replies.Add(option);
    }

    private static void AppendSubnegotiation(List<byte> destination, byte option, ReadOnlySpan<byte> payload)
    {
        destination.Add(TelnetCommand.Iac);
        destination.Add(TelnetCommand.SubnegotiationBegin);
        destination.Add(option);
        TelnetParser.EscapeIac(payload, destination);
        destination.Add(TelnetCommand.Iac);
        destination.Add(TelnetCommand.SubnegotiationEnd);
    }

    /// <summary>Reads a byte prefix first, then continues from an inner stream it does not own.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (TryReadPrefix(buffer, out int copied))
            {
                return copied;
            }
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (TryReadPrefix(buffer.Span, out int copied))
            {
                return ValueTask.FromResult(copied);
            }
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private bool TryReadPrefix(Span<byte> buffer, out int copied)
        {
            copied = Math.Min(buffer.Length, prefix.Length - _position);
            if (copied <= 0)
            {
                return false;
            }
            prefix.AsSpan(_position, copied).CopyTo(buffer);
            _position += copied;
            return true;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
