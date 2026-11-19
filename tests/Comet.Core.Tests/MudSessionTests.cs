using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Comet.Core.Configuration;
using Comet.Core.Input;
using Comet.Core.Sessions;

namespace Comet.Core.Tests;

/// <summary>Runs posted callbacks on the test thread, standing in for a UI thread.</summary>
internal sealed class PumpContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public async Task PumpUntilAsync(Func<bool> condition, int timeoutMilliseconds = 10_000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMilliseconds)
            {
                throw new TimeoutException("Condition was not met in time.");
            }
            while (_queue.TryTake(out var work))
            {
                work.Callback(work.State);
            }
            await Task.Delay(10).ConfigureAwait(true);
        }
    }
}

public sealed class MudSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "comet-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly PumpContext _context = new();
    private readonly int _port;

    public MudSessionTests()
    {
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _listener.Stop();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private MudSession CreateSession()
    {
        var profile = new SessionProfile { Name = "test", Host = "127.0.0.1", Port = _port };
        var paths = new AppPaths(_root, Path.Combine(_root, "scripts"), Path.Combine(_root, "logs"));
        return CreateOnPump(() => new MudSession(profile, new AppSettings(), paths, new ManualTime()));
    }

    /// <summary>Creates a session that captures the pump as its "UI thread" context.</summary>
    private MudSession CreateOnPump(Func<MudSession> create)
    {
        SynchronizationContext? current = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_context);
        try
        {
            return create();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(current);
        }
    }

    private static string[] Lines(MudSession session) =>
        [.. Enumerable.Range(0, session.Buffer.Count).Select(i => session.Buffer[i].Text)];

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        return buffer;
    }

    [Fact]
    public async Task NegotiatesShowsPromptEchoesCommandAndHidesPassword()
    {
        await using MudSession session = CreateSession();
        Task connect = session.ConnectAsync();
        using TcpClient server = await _listener.AcceptTcpClientAsync().ConfigureAwait(true);
        NetworkStream stream = server.GetStream();
        await _context.PumpUntilAsync(() => session.State == SessionState.Connected).ConfigureAwait(true);
        await connect.ConfigureAwait(true);

        // DO NAWS -> WILL NAWS + size. WILL MSSP -> DONT. Then a coloured line and a GA prompt.
        await stream.WriteAsync(new byte[] { 255, 253, 31, 255, 251, 70 }).ConfigureAwait(true);
        Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31, 0, 80, 0, 24, 255, 240, 255, 254, 70 }, await ReadAsync(stream, 15).ConfigureAwait(true));

        session.Engine.Execute("#action {^Name:} {#var seen yes}");
        await stream.WriteAsync(Encoding.UTF8.GetBytes("\x1b[32mWelcome\x1b[0m\r\nName: ")).ConfigureAwait(true);
        await stream.WriteAsync(new byte[] { 255, 249 }).ConfigureAwait(true);
        await _context.PumpUntilAsync(() => session.Engine.GetVariable("seen") == "yes").ConfigureAwait(true);
        Assert.Equal("Name: ", Lines(session)[^1]);

        session.SubmitInput("bob");
        Assert.Equal("bob\r\n", Encoding.ASCII.GetString(await ReadAsync(stream, 5).ConfigureAwait(true)));
        Assert.Equal("Name: bob", Lines(session)[^1]);
        Assert.Contains("Welcome", Lines(session));

        // WILL ECHO: password mode. The typed text is sent but never shown or remembered.
        byte[] passwordPrompt = [.. Encoding.ASCII.GetBytes("Password: "), 255, 251, 1];
        await stream.WriteAsync(passwordPrompt).ConfigureAwait(true);
        await _context.PumpUntilAsync(() => session.IsPasswordMode).ConfigureAwait(true);
        Assert.Equal(new byte[] { 255, 253, 1 }, await ReadAsync(stream, 3).ConfigureAwait(true));
        session.SubmitInput("s3cret;#alias");
        Assert.Equal("s3cret;#alias\r\n", Encoding.ASCII.GetString(await ReadAsync(stream, 15).ConfigureAwait(true)));
        Assert.DoesNotContain(Lines(session), line => line.Contains("s3cret", StringComparison.Ordinal));
        Assert.DoesNotContain(session.History.Entries, entry => entry.Contains("s3cret", StringComparison.Ordinal));

        await stream.WriteAsync(new byte[] { 255, 252, 1 }).ConfigureAwait(true);
        await _context.PumpUntilAsync(() => !session.IsPasswordMode).ConfigureAwait(true);

        server.Close();
        await _context.PumpUntilAsync(() => session.State == SessionState.Disconnected).ConfigureAwait(true);
        Assert.Contains(Lines(session), line => line.StartsWith("#SESSION CLOSED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DecompressesMccp2Stream()
    {
        await using MudSession session = CreateSession();
        Task connect = session.ConnectAsync();
        using TcpClient server = await _listener.AcceptTcpClientAsync().ConfigureAwait(true);
        NetworkStream stream = server.GetStream();
        await _context.PumpUntilAsync(() => session.State == SessionState.Connected).ConfigureAwait(true);
        await connect.ConfigureAwait(true);

        await stream.WriteAsync(new byte[] { 255, 251, 86 }).ConfigureAwait(true);
        Assert.Equal(new byte[] { 255, 253, 86 }, await ReadAsync(stream, 3).ConfigureAwait(true));

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(Encoding.ASCII.GetBytes("inside the stream\r\n"));
            zlib.Flush();
            // Plain text, the start marker and compressed bytes in one packet.
            byte[] packet = [.. Encoding.ASCII.GetBytes("before\r\n"), 255, 250, 86, 255, 240, .. compressed.ToArray()];
            await stream.WriteAsync(packet).ConfigureAwait(true);
            await _context.PumpUntilAsync(() => Lines(session).Contains("inside the stream")).ConfigureAwait(true);
        }

        Assert.True(session.IsCompressed);
        Assert.Contains("before", Lines(session));
    }

    [Fact]
    public async Task FailedConnectionIsReportedInTheOutput()
    {
        _listener.Stop();
        await using MudSession session = CreateSession();
        Task connect = session.ConnectAsync();
        await _context.PumpUntilAsync(() => connect.IsCompleted).ConfigureAwait(true);

        Assert.Equal(SessionState.Disconnected, session.State);
        Assert.Contains(Lines(session), line => line.StartsWith("#Could not connect:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScriptItemsAreSavedAndReloaded()
    {
        MudSession first = CreateSession();
        first.Engine.Execute("#alias {k} {kill %1}");
        string path = first.ScriptPath;
        await first.DisposeAsync().ConfigureAwait(true);

        Assert.True(File.Exists(path));
        var profile = new SessionProfile { Id = Path.GetFileNameWithoutExtension(path), Host = "127.0.0.1" };
        var paths = new AppPaths(_root, Path.Combine(_root, "scripts"), Path.Combine(_root, "logs"));
        await using var second = new MudSession(profile, new AppSettings(), paths, new ManualTime());
        Assert.Equal("kill %1", Assert.Single(second.Engine.GetItems(Scripting.ScriptItemKind.Alias)).Value);
    }

    [Fact]
    public void HistoryNavigatesAndRestoresDraft()
    {
        var history = new CommandHistory();
        history.Add("one");
        history.Add("two");
        history.Add("two");

        Assert.Equal(2, history.Entries.Count);
        Assert.Equal("two", history.Previous("draft"));
        Assert.Equal("one", history.Previous("two"));
        Assert.Null(history.Previous("one"));
        Assert.Equal("two", history.Next());
        Assert.Equal("draft", history.Next());
        Assert.Null(history.Next());
    }

    [Fact]
    public void TabCompletionPrefersUserWordsAndCycles()
    {
        var completer = new TabCompleter();
        completer.Observe("Gandalf the Grey greets Galadriel.");

        Assert.True(completer.TryComplete("tell ga", 7, ["Gamgee"], out string text, out int caret));
        Assert.Equal("tell Gamgee", text);
        Assert.True(completer.TryComplete(text, caret, ["Gamgee"], out text, out caret));
        Assert.Equal("tell Galadriel", text);
        Assert.True(completer.TryComplete(text, caret, ["Gamgee"], out text, out caret));
        Assert.Equal("tell Gandalf", text);
        Assert.True(completer.TryComplete(text, caret, ["Gamgee"], out text, out _));
        Assert.Equal("tell ga", text);
        Assert.False(completer.TryComplete("tell zz", 7, [], out _, out _));
    }

    [Fact]
    public void SettingsSurviveARoundTripAndABadFile()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "scripts"), Path.Combine(_root, "logs"));
        var settings = new AppSettings { FontSize = 15 };
        settings.Profiles.Add(new SessionProfile { Name = "Home", Host = "mud.example.org", Port = 23, UseTls = true });
        SettingsStore.Save(paths, settings);

        AppSettings loaded = SettingsStore.Load(paths, out string? warning);
        Assert.Null(warning);
        Assert.Equal(15, loaded.FontSize);
        Assert.Equal("mud.example.org", Assert.Single(loaded.Profiles).Host);

        File.WriteAllText(paths.SettingsFile, "{ not json");
        AppSettings fallback = SettingsStore.Load(paths, out warning);
        Assert.NotNull(warning);
        Assert.Empty(fallback.Profiles);
        Assert.True(File.Exists(paths.SettingsFile + ".bad"));
    }
}
